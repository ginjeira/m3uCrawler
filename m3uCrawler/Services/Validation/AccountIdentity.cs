using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// PHASE 9A.1 (2026-09-16): unidade de serializacao numa work item.
///
/// Identidade EXACTA = (playlist URL sem password) + "|" + username.
/// Password NAO participa da identidade. Dois URLs com mesmas paths/query
/// mas passwords diferentes mapeiam a MESMA account.
///
/// Workers diferentes (A/B/C) podem executar channels em paralelo.
/// Channels da MESMA account sao serializados (MaxConcurrentChannelsPerAccount = 1).
///
/// Host-only NAO e' a unidade. (host=X/user=A) e (host/X/user=B) sao accounts
/// independentes e podem correr em paralelo.
/// </summary>
public static class AccountIdentity
{
    public const string Delimiter = "|";

    public static string Compute(string playlistUrl, string? username)
    {
        if (string.IsNullOrWhiteSpace(playlistUrl)) return "0";
        var u = username ?? string.Empty;
        var composite = playlistUrl + Delimiter + u;
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(composite));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strip do parametro password= na query string. Accounts que diferem
    /// apenas em password mapeiam para a MESMA identidade.
    /// </summary>
    public static string ComputeSafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        try
        {
            var qIdx = url.IndexOf('?');
            if (qIdx < 0) return url;
            var baseUrl = url.Substring(0, qIdx);
            var query = url.Substring(qIdx + 1);
            var pairs = query.Split('&');
            var kept = new List<string>(pairs.Length);
            foreach (var pair in pairs)
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                var key = eq <= 0 ? pair : pair.Substring(0, eq);
                if (string.Equals(Uri.UnescapeDataString(key), "password", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                kept.Add(pair);
            }
            if (kept.Count == 0) return baseUrl;
            return baseUrl + "?" + string.Join("&", kept);
        }
        catch
        {
            return url;
        }
    }

    /// <summary>
    /// Extrai username da query string de um URL Xtream. Devolve null
    /// se nao encontrar.
    /// </summary>
    public static string? ExtractUsername(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var qIdx = url.IndexOf('?');
            if (qIdx < 0) return null;
            var query = url.Substring(qIdx + 1);
            foreach (var pair in query.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = Uri.UnescapeDataString(pair.Substring(0, eq));
                if (string.Equals(key, "username", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extrai username, strip password, calcula fingerprint.
    /// Devolve null se nao ha username no URL.
    /// </summary>
    public static string? FromXtreamUrl(string? url)
    {
        var username = ExtractUsername(url);
        if (string.IsNullOrEmpty(username)) return null;
        var stripped = ComputeSafeUrl(url);
        return Compute(stripped, username);
    }

    // Xtream server URL shape: /live/USER/PASS/id.ts (also movie/series).
    // Reuses the shape already recognised by M3uCandidateDetector.
    private static readonly Regex _xtreamUserPathRegex = new(
        @"/(?:live|movie|series)/(?<user>[^/]+)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? ExtractXtreamUsernameFromPath(Uri uri)
    {
        var match = _xtreamUserPathRegex.Match(uri.AbsolutePath);
        if (!match.Success) return null;
        var raw = match.Groups["user"].Value;
        try { return Uri.UnescapeDataString(raw); }
        catch { return raw; }
    }

    /// <summary>
    /// W1 (2026-09-19) — identidade externa funcional estável de uma conta
    /// Xtream, quando o provider a disponibiliza.
    ///
    /// <para>
    /// A identidade funcional Xtream é <c>(endpoint normalizado, username)</c>:
    /// o <c>username</c> é a identidade emitida pelo provider e o endpoint
    /// (scheme + authority) distingue contas em infra-estruturas diferentes.
    /// A <b>password nunca participa</b>. O path/query da API (get.php,
    /// type, output) é routing, não identidade, pelo que não é usado —
    /// assim variações de formato de playlist não criam outra conta
    /// (T4). O resultado é codificado como um token determinístico
    /// (SHA-256 truncado, 16 hex) para não persistir credenciais nem
    /// usernames em claro.
    /// </para>
    ///
    /// <para>
    /// Reutiliza o algoritmo de fingerprint existente
    /// (<see cref="Compute"/>) aplicando a normalização normativa
    /// (NFKC + trim) aos componentes antes do hash. Não introduz um
    /// algoritmo de identidade novo.
    /// </para>
    /// </summary>
    public static bool TryComputeXtreamExternalIdentity(string? url, out string? externalIdentity)
    {
        externalIdentity = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Username pode vir da query (get.php?username=...) ou do path
        // de servidor (/live/USER/PASS/...). Em ambos os casos a password
        // é ignorada.
        var username = ExtractUsername(url) ?? ExtractXtreamUsernameFromPath(uri);
        if (string.IsNullOrEmpty(username)) return false;

        // Endpoint/base resource normalizado: scheme + authority
        // (host[:port]). Não é usado o path/query porque não fazem
        // parte da identidade funcional da conta.
        var endpoint = uri.Scheme.ToLowerInvariant() + "://" + uri.Authority.ToLowerInvariant();
        var normalizedEndpoint = AccountKey.NormalizeExternalIdentity(endpoint);
        var normalizedUsername = AccountKey.NormalizeExternalIdentity(username);
        if (normalizedEndpoint.Length == 0 || normalizedUsername.Length == 0) return false;

        externalIdentity = Compute(normalizedEndpoint, normalizedUsername);
        return true;
    }

    /// <summary>
    /// W1 — AccountKey de uma conta Xtream: namespace do provider
    /// (<c>"xtream"</c>) + identidade externa funcional. Devolve
    /// <c>null</c> quando a evidência não permite derivar uma identidade
    /// estável (nunca inventa identidade).
    /// </summary>
    public static string? ComputeXtreamAccountKey(string? url)
    {
        if (!TryComputeXtreamExternalIdentity(url, out var externalIdentity)) return null;
        return AccountKey.Compose(ProviderNamespaces.Xtream, externalIdentity);
    }
}

/// <summary>
/// W1 (2026-09-19) — composição canónica de <c>AccountKey</c>.
///
/// <para>
/// Regra normativa (<c>docs/Reestructure/03-DISCOVERY.md §3</c>,
/// <c>32-DOMAIN-SCHEMA.md</c>): <c>AccountKey = Provider namespace +
/// external functional identity</c>. A identidade funcional é distinta
/// de identificadores técnicos (<c>ProviderAccountId</c>).
/// </para>
///
/// <para>
/// <b>Normalização não-colapsante.</b> A única normalização permitida
/// é NFKC + trim. Não há casefold, colapso de espaços, remoção de
/// pontuação nem qualquer transformação que possa fundir contas
/// funcionalmente distintas.
/// </para>
/// </summary>
public static class AccountKey
{
    public const char Separator = '|';

    /// <summary>
    /// Normalização não-colapsante: NFKC + trim. Valores nulos ou vazios
    /// devolvem string vazia (ausência de identidade, não identidade vazia).
    /// </summary>
    public static string NormalizeExternalIdentity(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Normalize(NormalizationForm.FormKC).Trim();
    }

    /// <summary>
    /// Compõe a identidade funcional canónica. Devolve <c>null</c> se o
    /// namespace ou a identidade externa normalizada forem vazios — a
    /// ausência de evidência estável não produz identidade.
    /// </summary>
    public static string? Compose(string? providerNamespace, string? externalFunctionalIdentity)
    {
        var ns = NormalizeExternalIdentity(providerNamespace);
        var identity = NormalizeExternalIdentity(externalFunctionalIdentity);
        if (ns.Length == 0 || identity.Length == 0) return null;
        return ns + Separator + identity;
    }
}

/// <summary>
/// W1 — namespaces de provider usados na composição de <c>AccountKey</c>.
/// O namespace é a <c>Key</c> da entidade <c>Provider</c>; nomes físicos
/// podem evoluir, mas o namespace faz parte de identidade persistida.
/// </summary>
public static class ProviderNamespaces
{
    public const string Xtream = "xtream";
    public const string Telegram = "telegram";
    public const string M3u = "m3u";
    public const string Http = "http";
    public const string File = "file";
    public const string Manual = "manual";

    /// <summary>
    /// Mapeia o nome do tipo de source do pipeline para o namespace do
    /// provider do mecanismo de discovery.
    /// </summary>
    public static string FromSourceKindName(string? sourceKindName) =>
        (sourceKindName?.Trim().ToLowerInvariant()) switch
        {
            "xtream" => Xtream,
            "telegram" => Telegram,
            "http" => Http,
            "file" => File,
            "manual" => Manual,
            _ => M3u,
        };
}

/// <summary>
/// Marcador para work items que respeitam a serializacao por identidade
/// de account. Todo work item que participe do <c>AccountBoundedWorkerPool</c>
/// deve implementar esta interface; a chave <see cref="AccountId"/> e'
/// a chave de serializacao.
///
/// Definicao da identidade (PHASE 9A.1):
///   (URL sem password) + "|" + username  →  SHA-256 hex truncado a 16 chars.
///
/// Password NAO participa da identidade. Dois URLs com mesmas paths/query
/// mas passwords diferentes mapeiam a MESMA identidade.
/// </summary>
public interface IAccountWork
{
    /// <summary>
    /// Chave de serializacao. Dois work items com o mesmo
    /// <see cref="AccountId"/> NAO podem ser processados em paralelo:
    /// os canais dessa account sao seriais por design (1 in-flight).
    /// </summary>
    string AccountId { get; }
}

/// <summary>
/// Work item que representa UMA account/playlist e os seus streams a
/// validar sequencialmente.
/// </summary>
public sealed record AccountValidationWork(
    string AccountId,
    string PlaylistUrl,
    string? Username,
    IReadOnlyList<AccountStreamWork> Streams) : IAccountWork;

/// <summary>
/// Um stream individual dentro de uma account.
/// </summary>
public sealed record AccountStreamWork(
    string Url,
    string Title,
    string Group);

/// <summary>
/// Resultado da validacao de uma account/playlist.
/// </summary>
public sealed record AccountValidationResult(
    AccountValidationWork Work,
    IReadOnlyList<StreamTestOutcome> Outcomes,
    int Working,
    int Failed,
    int ShortCircuited,
    int Tested);

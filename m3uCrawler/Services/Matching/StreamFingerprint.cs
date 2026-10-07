using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace m3uCrawler.Services.Matching;

/// <summary>
/// W4 (2026-09-19) — Fingerprint canónico, determinístico e versionado de
/// um URL de stream (<c>docs/Reestructure/04-PLAYLIST-STREAM.md §4</c>,
/// <c>32-DOMAIN-SCHEMA.md</c> Stream, DL-108).
///
/// <para>
/// <b>Contrato.</b>
/// <c>Fingerprint = lowercase hex SHA-256(UTF-8(version + "\n" + canonicalUrl))</c>.
/// A versão (<see cref="Version"/>, actualmente <c>"sfp1"</c>) pertence ao
/// algoritmo e é persistida em conjunto com o hash; o URL canónico nunca é
/// persistido.
/// </para>
///
/// <para>
/// <b>Canonicalização do URL.</b> Só <c>http</c>/<c>https</c> absolutos são
/// fingerprintáveis. Em ordem:
/// <list type="number">
///   <item>scheme e host em minúsculas; ponto final do host removido;</item>
///   <item>porta por omissão removida (80/http, 443/https), não-omissa preservada;</item>
///   <item>fragmento sempre removido;</item>
///   <item>userinfo nunca é incluído (credenciais fora da identidade técnica);</item>
///   <item>path preservado case-sensitive, sem descodificar percent-encoding
///         (<c>%2F</c> distinto de <c>/</c>); path Xtream
///         <c>/live|movie|series/USER/PASS/ID</c> preserva <c>ID</c> e mascara
///         <c>USER</c>/<c>PASS</c> como <c>***</c>;</item>
///   <item>query: apenas <c>username</c>, <c>password</c>, <c>token</c> e
///         <c>authorization</c> são removidos; os restantes parâmetros são
///         preservados verbatim e pela ordem original.</item>
/// </list>
/// A ordem de query é preservada (sem reordenação) nesta versão.
/// </para>
///
/// <para>
/// <b>Segurança.</b> O URL canónico e o material de hash nunca contêm
/// password, token, <c>Authorization</c> nem username de credencial. O
/// método nunca lança com input malformado e nunca inclui o URL original em
/// excepções ou mensagens.
/// </para>
///
/// <para>
/// <b>Não confundir</b> com <see cref="CredentialSanitizer"/> (sanitização de
/// apresentação, não é o canonicalizador) nem com
/// <c>ReviewFingerprint</c> (fingerprint de itens de revisão).
/// </para>
/// </summary>
public static class StreamFingerprint
{
    /// <summary>
    /// Versão do algoritmo de fingerprint (<c>sfp1</c>). Faz parte do
    /// contrato: uma alteração incompatível da representação canónica cria
    /// uma nova versão (DL-108).
    /// </summary>
    public const string Version = "sfp1";

    /// <summary>Tamanho esperado do hash em hex minúsculo (SHA-256).</summary>
    public const int HashLength = 64;

    /// <summary>Parâmetros de query considerados credenciais e sempre removidos.</summary>
    private static readonly HashSet<string> CredentialQueryParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "username",
        "password",
        "token",
        "authorization",
    };

    /// <summary>
    /// Tenta produzir a representação canónica do URL e o respectivo
    /// fingerprint. Devolve <c>false</c> (com <paramref name="canonical"/> e
    /// <paramref name="fingerprint"/> a <c>null</c>) quando o URL é
    /// nulo/vazio, não é absoluto, ou o scheme não é <c>http</c>/<c>https</c>.
    /// </summary>
    public static bool TryCreate(string? url, out string? canonical, out string? fingerprint)
    {
        canonical = null;
        fingerprint = null;

        if (string.IsNullOrWhiteSpace(url)) return false;

        var trimmed = url.Trim();
        if (!TrySplit(trimmed, out var scheme, out var authority, out var path, out var query))
        {
            return false;
        }

        if (!TryCanonicalizeAuthority(scheme, authority, out var hostPort))
        {
            return false;
        }

        var canonicalPath = MaskXtreamPath(string.IsNullOrEmpty(path) ? "/" : path);
        var canonicalQuery = RemoveCredentialParameters(query);

        canonical = canonicalQuery.Length == 0
            ? $"{scheme}://{hostPort}{canonicalPath}"
            : $"{scheme}://{hostPort}{canonicalPath}?{canonicalQuery}";

        fingerprint = ComputeHash(Version + "\n" + canonical);
        return true;
    }

    /// <summary>
    /// Devolve apenas o fingerprint, ou <c>null</c> quando o URL não é
    /// fingerprintável. Atalho para o caminho de persistência/dedup.
    /// </summary>
    public static string? TryComputeFingerprint(string? url)
        => TryCreate(url, out _, out var fingerprint) ? fingerprint : null;

    // ─────────────────────────────────────────────────────────────────────
    // Parsing manual (sem Uri): preserva percent-encoding e case do path.
    // ─────────────────────────────────────────────────────────────────────

    private static bool TrySplit(
        string input,
        out string scheme,
        out string authority,
        out string path,
        out string query)
    {
        scheme = string.Empty;
        authority = string.Empty;
        path = string.Empty;
        query = string.Empty;

        var schemeSeparator = input.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator <= 0) return false;

        scheme = input[..schemeSeparator].ToLowerInvariant();
        if (scheme != "http" && scheme != "https") return false;

        var rest = input[(schemeSeparator + 3)..];
        if (rest.Length == 0) return false;

        var authorityEnd = rest.Length;
        var pathStart = -1;
        var queryStart = -1;
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (c == '/')
            {
                authorityEnd = i;
                pathStart = i;
                break;
            }
            if (c == '?')
            {
                authorityEnd = i;
                queryStart = i;
                break;
            }
            if (c == '#')
            {
                authorityEnd = i;
                break;
            }
        }

        authority = rest[..authorityEnd];
        if (authority.Length == 0) return false;

        if (queryStart >= 0)
        {
            query = TrimAtFragment(rest[(queryStart + 1)..]);
            return true;
        }

        if (pathStart >= 0)
        {
            var pathAndQuery = rest[pathStart..];
            var questionMark = pathAndQuery.IndexOf('?');
            if (questionMark >= 0)
            {
                path = pathAndQuery[..questionMark];
                query = TrimAtFragment(pathAndQuery[(questionMark + 1)..]);
            }
            else
            {
                path = TrimAtFragment(pathAndQuery);
            }
        }

        return true;
    }

    private static string TrimAtFragment(string value)
    {
        var hash = value.IndexOf('#');
        return hash >= 0 ? value[..hash] : value;
    }

    private static bool TryCanonicalizeAuthority(string scheme, string authority, out string hostPort)
    {
        hostPort = string.Empty;

        // userinfo nunca entra na identidade técnica (dados de credencial).
        var at = authority.LastIndexOf('@');
        var hostSection = at >= 0 ? authority[(at + 1)..] : authority;
        if (hostSection.Length == 0) return false;

        string host;
        string? portRaw = null;

        if (hostSection[0] == '[')
        {
            var closing = hostSection.IndexOf(']');
            if (closing < 0) return false;
            host = hostSection[..(closing + 1)];
            var afterBracket = hostSection[(closing + 1)..];
            if (afterBracket.Length > 0)
            {
                if (afterBracket[0] != ':') return false;
                portRaw = afterBracket[1..];
            }
        }
        else
        {
            var colon = hostSection.IndexOf(':');
            if (colon >= 0)
            {
                host = hostSection[..colon];
                portRaw = hostSection[(colon + 1)..];
            }
            else
            {
                host = hostSection;
            }
        }

        if (host.Length == 0) return false;

        host = host.ToLowerInvariant();
        while (host.Length > 1 && host.EndsWith(".", StringComparison.Ordinal))
        {
            host = host[..^1];
        }

        if (portRaw is null || portRaw.Length == 0)
        {
            hostPort = host;
            return true;
        }

        if (!int.TryParse(portRaw, out var port) || port < 0 || port > 65535)
        {
            return false;
        }

        var defaultPort = scheme == "https" ? 443 : 80;
        hostPort = port == defaultPort ? host : $"{host}:{port}";
        return true;
    }

    /// <summary>
    /// Mascara os segmentos USER/PASS de um path Xtream
    /// (<c>/live|movie|series/USER/PASS/ID…</c>) preservando o <c>ID</c> e o
    /// resto do path. Paths que não correspondem ao padrão ficam inalterados.
    /// </summary>
    private static string MaskXtreamPath(string path)
    {
        var segments = path.Split('/');

        var start = 0;
        while (start < segments.Length && segments[start].Length == 0) start++;
        if (start >= segments.Length) return path;

        var prefix = segments[start];
        if (!prefix.Equals("live", StringComparison.OrdinalIgnoreCase)
            && !prefix.Equals("movie", StringComparison.OrdinalIgnoreCase)
            && !prefix.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        // Padrão só é Xtream quando existem USER e PASS; sem ambos o path
        // não é reconhecido como Xtream e não é alterado.
        if (start + 2 >= segments.Length) return path;

        segments[start + 1] = "***";
        segments[start + 2] = "***";
        return string.Join('/', segments);
    }

    private static string RemoveCredentialParameters(string query)
    {
        if (query.Length == 0) return query;

        var parts = query.Split('&');
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var equals = part.IndexOf('=');
            var name = equals >= 0 ? part[..equals] : part;
            if (CredentialQueryParameters.Contains(name)) continue;
            kept.Add(part);
        }

        return string.Join('&', kept);
    }

    private static string ComputeHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

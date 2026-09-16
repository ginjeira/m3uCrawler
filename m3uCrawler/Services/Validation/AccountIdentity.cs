using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

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

using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Namespaces canónicos de <see cref="ExternalIdentityEntity"/>.
/// </summary>
public static class ExternalIdentityNamespaces
{
    /// <summary>Namespace do atributo <c>tvg-id</c> de uma playlist.</summary>
    public const string TvgId = "tvg-id";

    /// <summary>Prefixo de namespaces específicos de um provider (<c>provider:{id}</c>).</summary>
    public const string ProviderPrefix = "provider:";

    /// <summary>Constrói o namespace de um provider a partir do seu id.</summary>
    public static string ForProvider(string? providerId)
        => string.IsNullOrWhiteSpace(providerId)
            ? TvgId
            : ProviderPrefix + providerId.Trim();
}

/// <summary>
/// Normalizador determinístico de valores de identidade externa
/// (<c>tvg-id</c>, ids de provider). Implementa os passos fixados por
/// <c>docs/adr/ADR-0002-stream-fingerprint-canonicalization.md</c> §5:
/// trim → lower-case compatível com Unicode → colapsar espaços
/// internos → remover quotes envolventes. Valores com forma de URI
/// http/https absoluto são adicionalmente canonicalizados segundo o
/// §3 (lower-case de scheme/host, remoção de porta por defeito e
/// remoção de fragmento), preservando o path e a query.
///
/// <para>
/// A normalização nunca reduz o valor a um hash: o significado é
/// mantido de forma legível e comparável. Um valor nulo/vazio produz
/// <see cref="string.Empty"/> (nenhuma identidade externa é criada).
/// </para>
/// </summary>
public static class ExternalIdentityNormalizer
{
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var s = raw.Trim();
        s = StripWrappingQuotes(s);
        if (s.Length == 0) return string.Empty;

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return CanonicalizeHttpUri(uri);
        }

        // ADR-0002 §5: lower-case compatível com Unicode + colapsar
        // espaços internos. FormC para estabilidade de comparação.
        s = MultiSpace.Replace(s, " ").Trim();
        return s.Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static string StripWrappingQuotes(string value)
    {
        var s = value.Trim();
        while (s.Length >= 2
            && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            s = s[1..^1].Trim();
        }

        return s;
    }

    private static string CanonicalizeHttpUri(Uri uri)
    {
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        while (host.Length > 1 && host.EndsWith(".", StringComparison.Ordinal))
        {
            host = host[..^1];
        }

        var defaultPort = scheme == Uri.UriSchemeHttp ? 80 : 443;
        var portPart = uri.Port == defaultPort
            ? string.Empty
            : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

        var userInfo = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : uri.UserInfo + "@";
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path)) path = "/";

        // uri.Query inclui o '?' quando existe; fragmento é descartado.
        return $"{scheme}://{userInfo}{host}{portPart}{path}{uri.Query}";
    }
}

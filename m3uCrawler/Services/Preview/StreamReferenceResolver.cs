using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.Preview;

/// <summary>
/// Resolve uma referência textual (vinda do cliente HTTP) para uma
/// <see cref="StreamReference"/> concreta, sem nunca devolver credenciais ao
/// chamador.
///
/// <para>
/// Gramática (separador <c>:</c>). Tudo o resto é rejeitado — incluindo URLs
/// (<c>http://…</c>) recebidos do cliente, que nunca são aceites como
/// referência:
/// <list type="bullet">
 ///   <item><c>review:&lt;longId&gt;</c> — item de revisão do catálogo (evidência
 ///         do item, retro-compat);</item>
 ///   <item><c>review:&lt;longId&gt;:&lt;candidateId&gt;</c> — candidato acumulado
 ///         dessa revisão (<see cref="Catalog.ReviewCandidateEntity"/>);</item>
///   <item><c>playlist:&lt;which&gt;:&lt;entryId&gt;</c> — entrada de uma das
///         playlists escritas em <c>outputDir</c>
///         (<c>playlist</c>, <c>playlist_temp</c>, <c>playlist_acquired</c>);
///         <c>entryId</c> é o resultado de <see cref="PlaylistEntryId"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// O URL resolvido vive apenas dentro do <see cref="StreamReference"/> e é
/// consumido server-side pelo <see cref="PreviewSessionManager"/>. Esta classe
/// nunca registra nem expõe o URL.
/// </para>
/// </summary>
public sealed class StreamReferenceResolver
{
    private const string PlaylistFile = "playlist";
    private const string PlaylistTempFile = "playlist_temp";
    private const string PlaylistAcquiredFile = "playlist_acquired";

    private static readonly HashSet<string> ValidPlaylists = new(StringComparer.Ordinal)
    {
        PlaylistFile,
        PlaylistTempFile,
        PlaylistAcquiredFile,
    };

    private readonly CatalogResolver _catalog;
    private readonly string _outputDir;
    private readonly M3uParserService _parser;

    public StreamReferenceResolver(CatalogResolver catalog, string outputDir, M3uParserService parser)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _outputDir = outputDir ?? throw new ArgumentNullException(nameof(outputDir));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <summary>
    /// Resolve <paramref name="reference"/>. Devolve <c>null</c> quando a forma
    /// é inválida, a entidade não existe, ou a URL resolvida é vazia.
    /// </summary>
    public async Task<StreamReference?> ResolveAsync(string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parts = reference.Split(':');
        if (parts.Length == 0)
        {
            return null;
        }

        return parts[0] switch
        {
            "review" => await ResolveReviewAsync(parts, ct).ConfigureAwait(false),
            "playlist" => ResolvePlaylist(parts),
            _ => null,
        };
    }

    private async Task<StreamReference?> ResolveReviewAsync(string[] parts, CancellationToken ct)
    {
        // Gramática: review:<id> (retro-compat, evidência do item) ou
        // review:<id>:<candidateId> (evidência acumulada do candidato).
        if (parts.Length is < 2 or > 3
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0)
        {
            return null;
        }

        if (parts.Length == 3)
        {
            if (parts[2].Length == 0
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var candidateId)
                || candidateId <= 0)
            {
                return null;
            }

            var candidate = await _catalog.GetReviewCandidateAsync(candidateId, ct).ConfigureAwait(false);
            if (candidate is null
                || candidate.ReviewItemId != id
                || string.IsNullOrWhiteSpace(candidate.StreamUrl))
            {
                return null;
            }

            var title = string.IsNullOrWhiteSpace(candidate.Title) ? null : candidate.Title;
            return new StreamReference(
                "review",
                id.ToString(CultureInfo.InvariantCulture),
                candidate.StreamUrl!,
                title);
        }

        var item = await _catalog.GetReviewItemAsync(id, ct).ConfigureAwait(false);
        if (item is null || string.IsNullOrWhiteSpace(item.StreamUrl))
        {
            return null;
        }

        return new StreamReference(
            "review",
            id.ToString(CultureInfo.InvariantCulture),
            item.StreamUrl!,
            item.NormalizedIdentity);
    }

    private StreamReference? ResolvePlaylist(string[] parts)
    {
        if (parts.Length != 3 || !ValidPlaylists.Contains(parts[1]))
        {
            return null;
        }

        var which = parts[1];
        var entryId = parts[2];
        if (entryId.Length == 0)
        {
            return null;
        }

        var path = Path.Combine(_outputDir, which + ".m3u");
        if (!File.Exists(path))
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            return null;
        }

        var parsed = _parser.ParseDetailed(content);
        foreach (var stream in parsed.Streams)
        {
            if (string.Equals(PlaylistEntryId(stream.Url), entryId, StringComparison.OrdinalIgnoreCase))
            {
                return new StreamReference("playlist", entryId, stream.Url, stream.Title);
            }
        }

        return null;
    }

    /// <summary>
    /// Identificador estável de uma entrada de playlist: os primeiros 16
    /// caracteres hexadecimais (minúsculas) do SHA-256 (UTF-8) do URL.
    /// Partilhado com o endpoint de entries para que o cliente e o resolver
    /// produzam exactamente o mesmo id.
    /// </summary>
    public static string PlaylistEntryId(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url ?? string.Empty));
        var hex = Convert.ToHexString(bytes);
        return hex[..16].ToLowerInvariant();
    }
}

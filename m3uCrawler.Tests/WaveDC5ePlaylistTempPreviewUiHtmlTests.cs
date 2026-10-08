using System;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-5e — Cobertura estrutural do HTML/JS do Dashboard para a pré-visualização
/// de <c>playlist_temp.m3u</c> (<c>GET /api/playlist_temp/preview</c>). Verifica
/// que a vista Playlist tem o nó <c>#playlistTempPreview</c> a seguir à
/// pré-visualização existente e que <c>loadPlaylist()</c> consome o endpoint
/// sanitizado, tratando o <c>404</c> ("playlist_temp.m3u não encontrada") e
/// falhas de rede, sem alterar a pré-visualização de <c>playlist.m3u</c>.
/// </summary>
public class WaveDC5ePlaylistTempPreviewUiHtmlTests
{
    private static string BuildDashboardHtml()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }

    private static string Slice(string html, string from, string to)
    {
        var start = html.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Não encontrei '{from}' no HTML.");
        var end = html.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Não encontrei '{to}' depois de '{from}' no HTML.");
        return html.Substring(start, end - start);
    }

    [Fact]
    public void Temp_preview_node_exists_after_playlist_preview()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='playlistTempPreview'", html);

        var main = html.IndexOf("id='playlistPreview'", StringComparison.Ordinal);
        var temp = html.IndexOf("id='playlistTempPreview'", StringComparison.Ordinal);
        Assert.True(main >= 0 && temp > main,
            "#playlistTempPreview deve estar depois de #playlistPreview.");
    }

    [Fact]
    public void Loader_fetches_temp_preview_and_handles_404_and_network_failure()
    {
        var html = BuildDashboardHtml();
        var loader = Slice(html, "async function loadPlaylist()", "// DC-5d");

        Assert.Contains("/api/playlist_temp/preview", loader);
        Assert.Contains("playlistTempPreview", loader);
        Assert.Contains("r.status === 404", loader);
        Assert.Contains("playlist_temp.m3u não encontrada", loader);
        Assert.Contains("catch (e)", loader);

        // A pré-visualização de playlist.m3u mantém-se intocada.
        Assert.Contains("/api/playlist/preview", loader);
        Assert.Contains("playlistPreview", loader);
    }
}

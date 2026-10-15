using System;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-PREVIEW / Player — cobertura estrutural do HTML/JS do Dashboard:
/// <list type="bullet">
///   <item>o <c>hls.js</c> vendorizado é carregado antes do script principal;</item>
///   <item>as Reviews expõem um botão "Pré-visualizar" quando existe evidência;</item>
///   <item>a vista Player tem os IDs esperados (<c>playerWhich</c>/<c>playerSearch</c>/<c>playerGrid</c>);</item>
///   <item>os handlers inline estão re-exportados para <c>window</c> (regra W1);</item>
///   <item>a superfície nova (Player/preview) não contém <c>?token=</c> nem URLs de
///         stream com credenciais — o browser só usa caminhos internos.</item>
/// </list>
/// </summary>
public class WavePreviewPlayerUiHtmlTests
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
    public void Hls_script_is_loaded_before_the_main_inline_script()
    {
        var html = BuildDashboardHtml();

        var hls = html.IndexOf("<script src='/assets/hls.min.js'></script>", StringComparison.Ordinal);
        var main = html.IndexOf("<script>\n  (function(){", StringComparison.Ordinal);
        Assert.True(hls >= 0, "Falta o <script src='/assets/hls.min.js'> no HTML.");
        Assert.True(main > hls, "O hls.js deve ser carregado antes do script principal.");
    }

    [Fact]
    public void Review_rows_expose_preview_button_when_has_evidence()
    {
        var html = BuildDashboardHtml();
        var loader = Slice(html, "async function loadCatalogReviews(", "function reviewsPrevPage(");

        Assert.Contains("r.hasEvidence", loader);
        Assert.Contains("previewReview(${r.id})", loader);
        Assert.Contains("data-review-id", loader);
        Assert.Contains("Pré-visualizar", loader);
    }

    [Fact]
    public void Player_view_and_preview_modal_ids_exist()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='view-playlists'", html);
        Assert.Contains("id='playerWhich'", html);
        Assert.Contains("id='playerSearch'", html);
        Assert.Contains("id='playerGrid'", html);
        Assert.Contains("value='playlist'", html);
        Assert.Contains("value='playlist_temp'", html);
        Assert.Contains("value='playlist_acquired'", html);

        Assert.Contains("id='reviewPreviewModal'", html);
        Assert.Contains("id='reviewPreviewCandidates'", html);
        Assert.Contains("id='reviewPreviewVideo'", html);
        Assert.Contains("id='reviewPreviewThumb'", html);
        Assert.Contains("id='reviewPreviewMeta'", html);
    }

    [Fact]
    public void Shared_start_preview_uses_api_request_and_handles_gate_errors()
    {
        var html = BuildDashboardHtml();
        var body = Slice(html, "async function startPreview(", "function previewReview(");

        Assert.Contains("/api/preview/start", body);
        Assert.Contains("manifestUrl", body);
        Assert.Contains("thumbnailUrl", body);
        Assert.Contains("Hls.isSupported()", body);
        Assert.Contains("application/vnd.apple.mpegurl", body);
        Assert.Contains("res.status === 404", body);
        Assert.Contains("res.status === 429", body);
        Assert.Contains("res.status === 503", body);

        // A referência nunca é composta com a URL do stream: só o id.
        Assert.Contains("'review:' + id", html);
        Assert.Contains("'playlist:' + _playerWhich + ':' + id", html);
    }

    [Fact]
    public void Review_preview_fetches_candidates_and_renders_selector_without_url()
    {
        var html = BuildDashboardHtml();
        var body = Slice(html, "function clearReviewPreviewCandidates(", "// === W-PLAYER");

        // Busca o detalhe (metadados dos candidatos) e compõe a referência só
        // com ids — nunca com a URL do stream.
        Assert.Contains("/api/review?id=", body);
        Assert.Contains("reviewPreviewCandidates", body);
        Assert.Contains("previewReviewCandidate", body);
        Assert.Contains("candidates.length > 1", body);
        Assert.Contains("'review:' + id + ':' + activeId", body);
        Assert.DoesNotContain("://", body);
        Assert.DoesNotContain("?token=", body);
        Assert.DoesNotContain("streamUrl", body);
        Assert.DoesNotContain("username=", body);
        Assert.DoesNotContain("password=", body);
    }

    [Fact]
    public void Player_loads_entries_and_filters_by_title_or_group()
    {
        var html = BuildDashboardHtml();
        var loader = Slice(html, "async function loadPlaylistEntries(", "function _playerFiltered(");

        Assert.Contains("/api/playlist-entries?which=", loader);
        Assert.Contains("renderPlaylistEntries()", loader);

        var filter = Slice(html, "function _playerFiltered()", "function renderPlaylistEntries(");
        Assert.Contains("e.title", filter);
        Assert.Contains("e.group", filter);
    }

    [Fact]
    public void Download_uses_the_single_entry_endpoint()
    {
        var html = BuildDashboardHtml();
        var body = Slice(html, "function downloadSelectedEntry(", "function openPlaylistPlayer(");

        Assert.Contains("/api/playlist-entry?which=", body);
        Assert.Contains("&id=", body);
        Assert.DoesNotContain("token", body);
    }

    [Fact]
    public void New_inline_handlers_are_exported_to_window()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("window.previewReview = previewReview;", html);
        Assert.Contains("window.previewReviewCandidate = previewReviewCandidate;", html);
        Assert.Contains("window.stopReviewPreview = stopReviewPreview;", html);
        Assert.Contains("window.closeReviewPreviewModal = closeReviewPreviewModal;", html);
        Assert.Contains("window.loadPlaylistEntries = loadPlaylistEntries;", html);
        Assert.Contains("window.renderPlaylistEntries = renderPlaylistEntries;", html);
        Assert.Contains("window.previewPlaylistEntry = previewPlaylistEntry;", html);
        Assert.Contains("window.downloadSelectedEntry = downloadSelectedEntry;", html);
        Assert.Contains("window.onPlayerWhichChange = onPlayerWhichChange;", html);
        Assert.Contains("window.openPlaylistPlayer = openPlaylistPlayer;", html);
    }

    [Fact]
    public void Preview_stops_on_modal_close_and_before_unload()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("_modalCloseHook = stopReviewPreview;", html);
        Assert.Contains("window.addEventListener('beforeunload'", html);
        Assert.Contains("/api/preview/' + encodeURIComponent(sid) + '/stop'", html);
    }

    [Fact]
    public void Served_html_preview_and_player_surface_has_no_token_or_credentials()
    {
        var html = BuildDashboardHtml();

        // Superfície nova: vista Player + modal partilhado + funções de preview/player.
        var playerView = Slice(html, "<!-- PLAYER (pré-visualização de entradas de playlist) -->", "<!-- DISPATCHARR -->");
        var previewJs = Slice(html, "// === W-PREVIEW / Player", "// DC-5b — visualizador de auditoria.");
        var playerJs = Slice(html, "// === W-PLAYER — grelha das playlists ===", "// DC-5b — visualizador de auditoria.");
        var combined = playerView + previewJs + playerJs;

        Assert.DoesNotContain("?token=", combined);
        Assert.DoesNotContain("://", combined);
        Assert.DoesNotContain("username=", combined);
        Assert.DoesNotContain("password=", combined);
    }
}

using System;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W5 — Cobertura estrutural do HTML/JS do Dashboard para a aprovação
/// estruturada de Reviews: escolha explícita Add Alias / Create Channel
/// (sem <c>prompt()</c>), dropdown de canais existentes e reutilização do
/// formulário W4 de criação de canal em modo Review. A exportação para
/// <c>window</c> é reforçada por <see cref="DashboardInlineHandlerScopeTests"/>.
/// </summary>
public class WaveW5UiHtmlTests
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
    public void Structured_approve_modal_and_choices_exist()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='reviewApproveModal'", html);
        Assert.Contains("id='reviewAliasChannel'", html);
        Assert.Contains("id='reviewAliasValue'", html);
        Assert.Contains("id='reviewApproveStatus'", html);
        Assert.Contains("id='reviewExcludeReason'", html);
        Assert.Contains("id='reviewReopen'", html);
        Assert.Contains("id='reviewReopenReason'", html);
        Assert.Contains("id='reviewsStateFilter'", html);
        Assert.Contains("id='reviewsPrevBtn'", html);
        Assert.Contains("id='reviewsNextBtn'", html);

        // Escolhas explícitas (sem prompt).
        Assert.Contains("selectReviewAction(\"add-alias\")", html);
        Assert.Contains("selectReviewAction(\"create-channel\")", html);
        Assert.Contains("selectReviewAction(\"exclude\")", html);
    }

    [Fact]
    public void Approve_review_has_no_prompt_chain()
    {
        var html = BuildDashboardHtml();
        var body = Slice(html, "function approveReview(", "function loadReviewAliasChannels(");

        Assert.DoesNotContain("prompt(", body);
        // A escolha passa a ser estruturada e guarda o item pendente por id.
        Assert.Contains("_pendingReviewId", body);
    }

    [Fact]
    public void Add_alias_select_is_sourced_from_catalog_channels_endpoint()
    {
        var html = BuildDashboardHtml();
        var body = Slice(html, "async function loadReviewAliasChannels(", "function selectReviewAction(");

        Assert.Contains("/api/catalog/channels", body);
        Assert.Contains("reviewAliasChannel", body);
    }

    [Fact]
    public void Create_channel_reuses_w4_form_in_review_mode()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("function showCreateChannelFormForReview(", html);
        Assert.Contains("_reviewChannelReviewId", html);

        // O modo Review abre o formulário W4 existente.
        var reviewBody = Slice(html, "function showCreateChannelFormForReview(", "function closeReviewApproveModal(");
        Assert.Contains("createChannelForm", reviewBody);
        Assert.Contains("loadCatalogTab('channels')", reviewBody);

        // submitCreateChannel ramifica para a nova Review API (id numérico).
        var submitBody = Slice(html, "async function submitCreateChannel(", "document.getElementById('channelsSearch')");
        Assert.Contains("/api/review/resolve", submitBody);
        Assert.Contains("canonicalChannel", submitBody);
        Assert.Contains("isEnabled", submitBody);
        Assert.DoesNotContain("/api/catalog/reviews/", submitBody);
    }

    [Fact]
    public void Reviews_tab_uses_the_new_review_api_with_pagination()
    {
        var html = BuildDashboardHtml();

        // Lista: nova API com paginação e filtro de estado; identidade por id.
        var loader = Slice(html, "async function loadCatalogReviews(", "function reviewsPrevPage(");
        Assert.Contains("/api/reviews?", loader);
        Assert.Contains("params.set('offset'", loader);
        Assert.Contains("params.set('limit'", loader);
        Assert.Contains("params.set('state'", loader);
        Assert.Contains("data-review-id", loader);
        Assert.DoesNotContain("/api/catalog/reviews", loader);

        // Acções: resolve (alias/channel), ignore e reopen — nada de legacy.
        var alias = Slice(html, "async function submitReviewAliasApproval(", "async function submitReviewExclude(");
        Assert.Contains("/api/review/resolve", alias);
        Assert.Contains("channelAlias", alias);
        Assert.Contains("canonicalChannelKey", alias);
        Assert.DoesNotContain("/api/catalog/reviews/", alias);

        var exclude = Slice(html, "async function submitReviewExclude(", "async function submitReviewReopen(");
        Assert.Contains("/api/review/ignore", exclude);
        Assert.Contains("reviewItemId", exclude);
        Assert.DoesNotContain("/api/catalog/reviews/", exclude);

        var reopen = Slice(html, "async function submitReviewReopen(", "async function showCreateChannelFormForReview(");
        Assert.Contains("/api/review/reopen", reopen);
        Assert.Contains("justification", reopen);
        Assert.DoesNotContain("/api/catalog/reviews/", reopen);
    }

    [Fact]
    public void New_inline_handlers_are_exported_to_window()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("window.reviewsPrevPage = reviewsPrevPage;", html);
        Assert.Contains("window.reviewsNextPage = reviewsNextPage;", html);
        Assert.Contains("window.reopenReview = reopenReview;", html);
        Assert.Contains("window.selectReviewAction = selectReviewAction;", html);
        Assert.Contains("window.submitReviewAliasApproval = submitReviewAliasApproval;", html);
        Assert.Contains("window.submitReviewExclude = submitReviewExclude;", html);
        Assert.Contains("window.submitReviewReopen = submitReviewReopen;", html);
        Assert.Contains("window.closeReviewApproveModal = closeReviewApproveModal;", html);
        Assert.Contains("window.showCreateChannelFormForReview = showCreateChannelFormForReview;", html);
    }
}

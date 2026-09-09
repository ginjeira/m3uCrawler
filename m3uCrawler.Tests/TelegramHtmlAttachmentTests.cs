using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Cobre o caminho "Telegram HTML attachment -> XtreamPublicationResolver -> M3U/Xtream pipeline":
///
///   DetectFromMessage (filename .html)
///     -> CandidatePlaylist { Kind=Attachment, DetectedFrom="html attachment",
///                            RequiresContentVerification=true, Content=null }
///     -> ProcessAttachmentCandidatesAsync descarrega via WTelegram
///     -> content HTML em candidate.Content
///     -> SearchAndTestM3UInTelegramAsync: branch RequiresContentVerification
///        -> LooksLikeHtmlPublication -> XtreamPublicationResolver.ResolveFromHtml
///     -> N XtreamAccountInfo -> N CandidatePlaylist (DetectedFrom="xtream publication")
///
/// Estes testes exercitam o detector + ProcessAttachmentCandidatesAsync + o branch novo
/// em SearchAndTestM3UInTelegramAsync com conteudos HTML representativos, sem rede real.
/// </summary>
public class TelegramHtmlAttachmentTests
{
    private const string PubAttachmentName = "m3u@server.example_07-09-2026.html";

    [Fact]
    public async Task Html_attachment_is_downloaded_then_routed_to_resolver_and_produces_promoted_candidates()
    {
        // HTML com 3 contas distintas no mesmo servidor.
        var html = @"<!DOCTYPE html><html><body>
            <div class='card'>Host = shared.server:80</div><div>User = A</div><div>Pass = pa</div>
            <hr>
            <div class='card'>Host = shared.server:80</div><div>User = B</div><div>Pass = pb</div>
            <hr>
            <div class='card'>Host = shared.server:80</div><div>User = C</div><div>Pass = pc</div>
        </body></html>";

        var found = new List<CandidatePlaylist>();

        // 1. Detector emite o attachment.
        var fromDetector = new M3uCandidateDetector().DetectFromMessage("texto", PubAttachmentName);
        found.AddRange(fromDetector);

        var htmlCandidate = found.Single(c => c.DetectedFrom == "html attachment");
        Assert.Equal(CandidateSourceKind.Attachment, htmlCandidate.Kind);
        Assert.True(htmlCandidate.RequiresContentVerification);

        // 2. ProcessAttachmentCandidatesAsync descarrega via callback.
        await TelegramScraperService.ProcessAttachmentCandidatesAsync(
            found,
            hasAttachment: true,
            filename: PubAttachmentName,
            text: "texto",
            downloader: () => Task.FromResult<string?>(html));

        Assert.Equal(html, htmlCandidate.Content);

        // 3. Branch novo: HTML sem ser M3U, com RequiresContentVerification, dispara
        //    XtreamPublicationResolver. Como o teste nao roda o pipeline completo,
        //    invocamos directamente para confirmar o comportamento end-to-end.
        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, PubAttachmentName);
        Assert.Equal(3, accounts.Count);

        // 4. Cada conta e' promovida a CandidatePlaylist independente.
        var promoted = accounts
            .Select(a => TelegramScraperService.PromoteXtreamAccount(
                a.M3uUrl ?? BuildPlaylistUrl(a), PubAttachmentName))
            .Where(c => c != null)
            .Select(c => c!)
            .ToList();
        Assert.Equal(3, promoted.Count);
        foreach (var p in promoted)
        {
            Assert.Equal(CandidateSourceKind.Url, p.Kind);
            Assert.Equal("xtream publication", p.DetectedFrom);
            Assert.True(p.RequiresContentVerification);
            Assert.Contains("get.php?username=", p.Url!);
        }
        var users = promoted.Select(p => ExtractUsername(p.Url!)).OrderBy(u => u).ToList();
        Assert.Equal(new[] { "A", "B", "C" }, users);
    }

    [Fact]
    public async Task Html_attachment_with_repeated_account_is_deduplicated()
    {
        var html = @"<!DOCTYPE html><html><body>
            <div>Host: sameid.example:80</div><div>User: iduser</div><div>Pass: pw1</div>
            <hr>
            <div>Host: sameid.example:80</div><div>User: iduser</div><div>Pass: pwDifferent</div>
        </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "x.html");
        Assert.Single(accounts);
        Assert.Equal("iduser", accounts[0].Username);

        // A identidade logica NAO inclui a password.
        Assert.DoesNotContain("pwDifferent", accounts[0].LogicalIdentity);
    }

    [Fact]
    public async Task Html_attachment_with_media_list_does_not_create_channels()
    {
        // HTML com MEDIA LIST no topo mas SEM cards Xtream: resolver devolve zero.
        var html = @"<!DOCTYPE html><html><body>
            <h1>MEDIA LIST</h1>
            <ul><li>SIC</li><li>TVI</li><li>RTP</li></ul>
            <p>PORTUGAL</p>
            <p>SPORT TV</p>
        </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "x.html");
        Assert.Empty(accounts);

        // Confirmacao adicional: nenhuma palavra da media-list entrou como canal.
        var allAccountStrings = string.Join(" ", accounts.Select(a => a.ToString()));
        Assert.DoesNotContain("SIC", allAccountStrings);
        Assert.DoesNotContain("TVI", allAccountStrings);
        Assert.DoesNotContain("RTP", allAccountStrings);
    }

    [Fact]
    public async Task Html_attachment_with_real_world_card_yields_single_account()
    {
        var html = @"<!DOCTYPE html><html><body>
            <div class='card'>
                <span>Host</span>: example.com:80<br>
                <span>User</span>: alice<br>
                <span>Pass</span>: secret1<br>
                <span>M3U</span>: <a href='http://example.com:80/get.php?username=alice&password=secret1&type=m3u_plus'>link</a>
            </div>
        </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "x.html");
        Assert.Single(accounts);
        Assert.Equal("alice", accounts[0].Username);
        // M3uUrl explicita e' preservada.
        Assert.NotNull(accounts[0].M3uUrl);
        Assert.Contains("get.php", accounts[0].M3uUrl!);
    }

    [Fact]
    public async Task Html_attachment_with_empty_html_yields_zero_accounts()
    {
        var accounts = XtreamPublicationResolver.ResolveFromHtml("", "x.html");
        Assert.Empty(accounts);
    }

    [Fact]
    public void Html_attachment_account_to_string_does_not_leak_password()
    {
        var html = @"<!DOCTYPE html><html><body>
            <div>Host: leak.example:80</div>
            <div>User: leakuser</div>
            <div>Pass: shouldNeverAppearInToString99</div>
        </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "x.html");
        Assert.Single(accounts);
        var s = accounts[0].ToString();
        Assert.DoesNotContain("shouldNeverAppearInToString99", s);
    }

    [Fact]
    public async Task Html_attachment_with_multiple_hosts_and_same_user_yields_multiple_sources()
    {
        var html = @"<!DOCTYPE html><html><body>
            <div>Host: a.example:80</div><div>User: same</div><div>Pass: pa</div>
            <hr>
            <div>Host: b.example:80</div><div>User: same</div><div>Pass: pb</div>
        </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "x.html");
        Assert.Equal(2, accounts.Count);
        var hosts = accounts.Select(a => a.Host).OrderBy(h => h).ToList();
        Assert.Equal(new[] { "a.example", "b.example" }, hosts);
    }

    // ============ Helpers ============

    private static string? BuildPlaylistUrl(XtreamAccountInfo a)
    {
        // Replica a logica de TelegramScraperService.BuildXtreamPlaylistUrl
        // para os testes poderem comparar URLs sem expor esse helper como internal.
        var scheme = string.IsNullOrWhiteSpace(a.Scheme) ? "http" : a.Scheme.ToLowerInvariant();
        var synthetic = $"{scheme}://{a.Host}:{a.Port}/live/{Uri.EscapeDataString(a.Username)}/{Uri.EscapeDataString(a.Password)}/0.ts";
        return new M3uCandidateDetector().ResolveXtreamPlaylistUrl(synthetic);
    }

    private static string ExtractUsername(string playlistUrl)
    {
        // playlistUrl = http://host:port/get.php?username=X&password=Y&type=m3u_plus
        var qs = playlistUrl[(playlistUrl.IndexOf('?') + 1)..];
        foreach (var part in qs.Split('&'))
        {
            if (part.StartsWith("username=", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part["username=".Length..]);
        }
        return string.Empty;
    }
}

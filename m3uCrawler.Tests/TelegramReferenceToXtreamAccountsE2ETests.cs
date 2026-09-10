using System.Text;
using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Teste de aceitacao end-to-end do caso de uso principal desta fase:
///   Telegram message containing only a t.me/c reference
///     -> discovered as TelegramPublicationRef
///     -> resolved via fetcher (mock) -> HTML attachment
///     -> XtreamPublicationResolver finds N accounts
///     -> promoted to N CandidatePlaylist
///     -> each enters the existing pipeline (we don't re-test that here)
/// </summary>
public class TelegramReferenceToXtreamAccountsE2ETests
{
    private const string PubUrl = "https://t.me/c/1635952193/110637";

    [Fact]
    public async Task EndToEnd_tme_reference_to_multiple_independent_sources()
    {
        // HTML de publicacao real com 3 contas no mesmo servidor.
        var html = @"<!DOCTYPE html><html><body>
            <div class='card'>
                <span>Host</span>: shared.example:80<br>
                <span>User</span>: alice<br>
                <span>Pass</span>: secret1<br>
                <span>M3U</span>: <a href='http://shared.example:80/get.php?username=alice&password=secret1&type=m3u_plus'>link</a>
            </div>
            <div class='card'>
                <span>Host</span>: shared.example:80<br>
                <span>User</span>: bob<br>
                <span>Pass</span>: secret2<br>
            </div>
            <div class='card'>
                <span>Host</span>: shared.example:80<br>
                <span>User</span>: carol<br>
                <span>Pass</span>: secret3<br>
            </div>
        </body></html>";

        // 1. Discovery encontra a referencia Telegram.
        var refs = TelegramPublicationDiscovery.DiscoverFromText(
            $"veja aqui {PubUrl}",
            source: "canal-principal");
        Assert.Single(refs);
        Assert.Equal("telegram message link", refs[0].Kind);

        // 2. Resolve via fetcher mock -> HTML attachment.
        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult<ResolvedPublication?>(new ResolvedPublication
            {
                Text = "",
                Filename = "m3u@shared.example_07-09-2026.html",
                MediaContent = Encoding.UTF8.GetBytes(html),
                Kind = "html attachment",
                ChannelId = 1635952193L,
                MessageId = 110637
            });

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolved);
        Assert.Equal(PublicationState.Resolved, resolved[0].State);
        Assert.Equal(3, resolved[0].XtreamAccounts.Count);

        // 3. Cada conta -> CandidatePlaylist independente via PromoteXtreamAccount.
        var promoted = resolved[0].XtreamAccounts
            .Select(a =>
            {
                var url = a.M3uUrl ?? TelegramScraperService.BuildPlaylistUrlForTest(a);
                return TelegramScraperService.PromoteXtreamAccount(url, PubUrl);
            })
            .Where(c => c != null)
            .Select(c => c!)
            .ToList();
        Assert.Equal(3, promoted.Count);
        foreach (var p in promoted)
        {
            Assert.Equal("xtream publication", p.DetectedFrom);
            Assert.True(p.RequiresContentVerification);
            Assert.Contains("get.php?username=", p.Url!);
        }

        // Identidades distintas (alice, bob, carol).
        var users = promoted.Select(p => ExtractUsername(p.Url!)).OrderBy(u => u).ToList();
        Assert.Equal(new[] { "alice", "bob", "carol" }, users);
    }

    [Fact]
    public async Task EndToEnd_tme_reference_resolved_to_html_without_accounts_marks_RequiresReview()
    {
        var refs = TelegramPublicationDiscovery.DiscoverFromText(
            $"veja {PubUrl}",
            source: "canal-principal");
        Assert.Single(refs);

        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult<ResolvedPublication?>(new ResolvedPublication
            {
                Text = "",
                Filename = "page.html",
                MediaContent = Encoding.UTF8.GetBytes("<html><body>nothing here</body></html>"),
                Kind = "html attachment",
                ChannelId = 1635952193L,
                MessageId = 110637
            });

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolved);
        Assert.Equal(PublicationState.RequiresReview, resolved[0].State); // nao falha, nao some
        Assert.Equal(0, resolved[0].XtreamAccounts.Count);
        Assert.NotNull(resolved[0].Reason);
        Assert.DoesNotContain("secret", resolved[0].Reason ?? "");
    }

    [Fact]
    public async Task EndToEnd_tme_reference_resolution_failure_is_recorded()
    {
        var refs = TelegramPublicationDiscovery.DiscoverFromText(
            $"veja {PubUrl}",
            source: "canal-principal");
        Assert.Single(refs);

        TelegramMessageFetcher fetcher = (_, _, _) =>
            throw new WTelegram.WTException("CHANNEL_INVALID");

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolved);
        Assert.Equal(PublicationState.ResolutionFailed, resolved[0].State);
        Assert.Contains("CHANNEL", resolved[0].Reason ?? "");
    }

    [Fact]
    public async Task EndToEnd_with_repeated_card_dedupes_to_one_source()
    {
        var html = @"<!DOCTYPE html><html><body>
            <div>Host: same.example:80</div><div>User: sameuser</div><div>Pass: pw1</div>
            <hr>
            <div>Host: same.example:80</div><div>User: sameuser</div><div>Pass: pwDifferent</div>
        </body></html>";

        var refs = TelegramPublicationDiscovery.DiscoverFromText(
            PubUrl, source: "x");
        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult<ResolvedPublication?>(new ResolvedPublication
            {
                Filename = "p.html",
                MediaContent = Encoding.UTF8.GetBytes(html),
                Kind = "html attachment",
                ChannelId = 1635952193L,
                MessageId = 110637
            });

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolved[0].XtreamAccounts); // dedup mantem uma so
    }

    [Fact]
    public async Task EndToEnd_media_list_in_html_does_not_create_accounts()
    {
        var html = @"<!DOCTYPE html><html><body>
            <h1>MEDIA LIST</h1>
            <ul><li>SIC</li><li>TVI</li><li>RTP</li></ul>
        </body></html>";

        var refs = TelegramPublicationDiscovery.DiscoverFromText(PubUrl, source: "x");
        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult<ResolvedPublication?>(new ResolvedPublication
            {
                Filename = "p.html",
                MediaContent = Encoding.UTF8.GetBytes(html),
                Kind = "html attachment",
                ChannelId = 1635952193L,
                MessageId = 110637
            });

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolved);
        Assert.Equal(0, resolved[0].XtreamAccounts.Count);
        Assert.DoesNotContain("SIC", string.Join(",", resolved[0].XtreamAccounts.Select(a => a.ToString())));
    }

    private static string ExtractUsername(string playlistUrl)
    {
        var qs = playlistUrl[(playlistUrl.IndexOf('?') + 1)..];
        foreach (var part in qs.Split('&'))
        {
            if (part.StartsWith("username=", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part["username=".Length..]);
        }
        return string.Empty;
    }
}

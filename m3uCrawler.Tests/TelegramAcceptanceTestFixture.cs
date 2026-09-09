using System.Text;
using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Teste de aceitacao para o caso de uso do projecto:
///   "Mensagem Telegram com texto contendo https://t.me/c/&lt;channel&gt;/&lt;message&gt;
///    E attachment .html com cards Xtream"
///
/// Cobre o caminho real do user na TASK-3:
///   Discovery -> Resolver (resolves -> descobre o texto, nao a HTML)
///             -> para HTML attachment no nivel do message processing:
///                XtreamPublicationResolver.ResolveFromHtml
///             -> fan-out para N CandidatePlaylists
/// </summary>
public class TelegramAcceptanceTestFixture
{
    [Fact]
    public async Task User_case_message_with_html_attachment_and_tme_reference()
    {
        // ---- INPUT ----
        // 1. Texto da mensagem com t.me/c/ + 2. HTML attachment com cards Xtream
        var messageText = "m3u@iptvgold.online_07-09-2026.html";
        var htmlContent = @"<!DOCTYPE html><html><body>
            <div class='card'>
                <span>Host</span>: iptvgold.online:80<br>
                <span>User</span>: alice<br>
                <span>Pass</span>: fictionalSecret99<br>
                <span>M3U</span>: <a href='http://iptvgold.online:80/get.php?username=alice&password=fictionalSecret99&type=m3u_plus'>link</a>
            </div>
            <hr>
            <div class='card'>
                <span>Host</span>: iptvgold.online:80<br>
                <span>User</span>: bob<br>
                <span>Pass</span>: fictionalSecret100<br>
            </div>
            <hr>
            <div class='card'>
                <span>Host</span>: iptvgold.online:80<br>
                <span>User</span>: carol<br>
                <span>Pass</span>: fictionalSecret101<br>
            </div>
        </body></html>";

        // ---- STEP 1: discovery encontra o t.me/c/ no texto ----
        var refs = TelegramPublicationDiscovery.DiscoverFromText(
            "https://t.me/c/1635952193/110658 " + messageText,
            source: "canal-original");
        Assert.Single(refs);
        Assert.Equal("telegram message link", refs[0].Kind);
        Assert.Equal(1635952193L, refs[0].ChannelId);
        Assert.Equal(110658, refs[0].MessageId);

        // ---- STEP 2: HTML attachment no nivel do message processing e' resolvido pelo
        // XtreamPublicationResolver (este e' o caminho que o user descreveu). ----
        var accounts = XtreamPublicationResolver.ResolveFromHtml(htmlContent, messageText);
        Assert.Equal(3, accounts.Count);

        // ---- STEP 3: cada conta -> CandidatePlaylist (fan-out, como no loop principal). ----
        var promoted = accounts.Select(a =>
        {
            var url = a.M3uUrl ?? TelegramScraperService.BuildPlaylistUrlForTest(a);
            return TelegramScraperService.PromoteXtreamAccount(url, messageText);
        }).Where(c => c != null).Select(c => c!).ToList();
        Assert.Equal(3, promoted.Count);

        // ---- STEP 4: identidades independentes (3 utilizadores distintos). ----
        var users = promoted.Select(p => ExtractUsername(p.Url!)).OrderBy(u => u).ToList();
        Assert.Equal(new[] { "alice", "bob", "carol" }, users);

        // ---- STEP 5: resolucao do t.me/c/ e' simulada (canal inacessivel por outro motivo). ----
        // Isto prova que o sistema reporta o estado em vez de desaparecer.
        TelegramMessageFetcher fetcher = (_, _, _) =>
            throw new WTelegram.WTException("CHANNEL_INVALID");
        var resolutions = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolutions);
        Assert.Equal(PublicationState.ResolutionFailed, resolutions[0].State);
        Assert.Contains("CHANNEL", resolutions[0].Reason ?? "");
    }

    [Fact]
    public async Task User_case_resolved_tme_publication_with_html_attachment_in_target_message()
    {
        // Variante: o t.me/c/ e' resolvido com sucesso e a mensagem apontada tem
        // um HTML attachment com cards Xtream. Este e' o caso completo de fan-out
        // extremo.
        var htmlContent = @"<!DOCTYPE html><html><body>
            <div>Host: shared.example:80</div><div>User: u1</div><div>Pass: p1</div>
            <hr>
            <div>Host: shared.example:80</div><div>User: u2</div><div>Pass: p2</div>
        </body></html>";

        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/1635952193/110658",
            Kind = "telegram message link",
            ChannelId = 1635952193L,
            MessageId = 110658,
            DiscoveredFromText = "m3u@iptvgold.online_07-09-2026.html",
            OriginalSource = "canal-original",
            State = PublicationState.Discovered
        }};

        TelegramMessageFetcher fetcher = (channelId, messageId, _) =>
        {
            Assert.Equal(1635952193L, channelId);
            Assert.Equal(110658, messageId);
            return Task.FromResult<ResolvedPublication?>(new ResolvedPublication
            {
                Text = "m3u@iptvgold.online_07-09-2026.html",
                Filename = "m3u@iptvgold.online_07-09-2026.html",
                MediaContent = Encoding.UTF8.GetBytes(htmlContent),
                Kind = "html attachment",
                ChannelId = channelId,
                MessageId = messageId
            });
        };

        var resolutions = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolutions);
        Assert.Equal(PublicationState.Resolved, resolutions[0].State);
        Assert.Equal(2, resolutions[0].XtreamAccounts.Count);

        // Cada conta -> CandidatePlaylist independente.
        var promoted = resolutions[0].XtreamAccounts.Select(a =>
        {
            var url = a.M3uUrl ?? TelegramScraperService.BuildPlaylistUrlForTest(a);
            return TelegramScraperService.PromoteXtreamAccount(url, "https://t.me/c/1635952193/110658");
        }).Where(c => c != null).Select(c => c!).ToList();
        Assert.Equal(2, promoted.Count);
        Assert.All(promoted, p => Assert.Equal("xtream publication", p.DetectedFrom));
    }

    [Fact]
    public async Task Discovery_integration_with_message_processing_for_tme_refs()
    {
        // Prova que o TelegramPublicationDiscovery identifica correctamente um
        // t.me/c/ no texto e o anexa a TelegramPublicationRef com o canal e o
        // message id correctos, pronto a ser passado ao resolver.
        var text = "olha este link https://t.me/c/1635952193/110658 e tambem este https://t.me/c/1635952193/110659";
        var refs = TelegramPublicationDiscovery.DiscoverFromText(text, source: "canal-x");
        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.Equal(1635952193L, r.ChannelId));
        Assert.Equal(110658, refs[0].MessageId);
        Assert.Equal(110659, refs[1].MessageId);
        Assert.All(refs, r => Assert.Equal("telegram message link", r.Kind));
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

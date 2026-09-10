using System.Text;
using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes do TelegramPublicationResolver. A resolucao de mensagem via WTelegram
/// e' injetada atraves de um delegate TelegramMessageFetcher, permitindo testes
/// deterministicos sem sessao Telegram real.
/// </summary>
public class TelegramPublicationResolverTests
{
    // ---- Fetchers falsos (substituem WTelegram) ----

    private static ResolvedPublication FakeText(string text, long? channelId = 100, int? messageId = 1)
    {
        return new ResolvedPublication
        {
            Text = text,
            Filename = null,
            MediaContent = null,
            Kind = "text",
            ChannelId = channelId,
            MessageId = messageId
        };
    }

    private static ResolvedPublication FakeHtmlAttachment(string filename, string html, long? channelId = 100, int? messageId = 2)
    {
        return new ResolvedPublication
        {
            Text = "",
            Filename = filename,
            MediaContent = Encoding.UTF8.GetBytes(html),
            Kind = "html attachment",
            ChannelId = channelId,
            MessageId = messageId
        };
    }

    [Fact]
    public async Task Resolves_reference_to_text_returns_Unsupported_when_no_useful_content()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/1",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 1,
            DiscoveredFromText = "https://t.me/c/100/1",
            OriginalSource = "channel-x",
            State = PublicationState.Discovered
        }};

        var fetcherCalls = new List<(long ch, int msg)>();
        TelegramMessageFetcher fetcher = (ch, msg, ct) =>
        {
            fetcherCalls.Add((ch, msg));
            return Task.FromResult(FakeText("olá mundo sem urls"));
        };

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);

        // Conteudo obtido mas sem nada util -> Unsupported, NAO Resolved.
        Assert.Single(resolved);
        Assert.Equal(PublicationState.Unsupported, resolved[0].State);
        Assert.Single(fetcherCalls);
    }

    [Fact]
    public async Task Resolves_reference_to_text_with_child_url_returns_Resolved()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/1",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 1,
            DiscoveredFromText = "https://t.me/c/100/1",
            OriginalSource = "channel-x",
            State = PublicationState.Discovered
        }};

        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult(FakeText("veja https://example.com/page.html para mais"));

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);

        Assert.Single(resolved);
        Assert.Equal(PublicationState.Resolved, resolved[0].State);
        Assert.Single(resolved[0].ChildPublications);
    }

    [Fact]
    public async Task Resolves_reference_to_html_attachment_and_extracts_accounts()
    {
        var html = @"<!DOCTYPE html><html><body>
            <div>Host: srv.example:80</div><div>User: u1</div><div>Pass: p1</div>
            <hr>
            <div>Host: srv.example:80</div><div>User: u2</div><div>Pass: p2</div>
        </body></html>";

        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/2",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 2,
            DiscoveredFromText = "https://t.me/c/100/2",
            OriginalSource = "channel-x",
            State = PublicationState.Discovered
        }};

        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult(FakeHtmlAttachment("m3u@host_07-09-2026.html", html));

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);

        Assert.Single(resolved);
        Assert.Equal("m3u@host_07-09-2026.html", resolved[0].Filename);
        Assert.Equal("html attachment", resolved[0].AttachmentKind);
        Assert.Equal(PublicationState.Resolved, resolved[0].State);
        Assert.Equal(2, resolved[0].XtreamAccounts.Count);
    }

    [Fact]
    public async Task Resolved_with_zero_accounts_and_html_is_RequiresReview()
    {
        // HTML sem cards Xtream -> nao e' "failed", mas requer revisao.
        var html = @"<!DOCTYPE html><html><body>random page</body></html>";
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/3",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 3,
            DiscoveredFromText = "https://t.me/c/100/3",
            OriginalSource = "channel-x",
            State = PublicationState.Discovered
        }};

        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult(FakeHtmlAttachment("page.html", html));

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);

        Assert.Single(resolved);
        Assert.Equal(PublicationState.RequiresReview, resolved[0].State);
        Assert.Equal(0, resolved[0].XtreamAccounts.Count);
        Assert.NotNull(resolved[0].Reason);
        Assert.DoesNotContain("p1", resolved[0].Reason ?? "");
    }

    [Fact]
    public async Task Resolved_text_with_http_url_creates_child_reference()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/10",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 10,
            DiscoveredFromText = "https://t.me/c/100/10",
            OriginalSource = "channel-x",
            State = PublicationState.Discovered
        }};

        // texto da mensagem resolvida contem uma URL HTTP para publicacao HTML.
        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult(FakeText("veja https://example.com/page.html para mais"));

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);

        Assert.Single(resolved);
        Assert.Equal(PublicationState.Resolved, resolved[0].State);
        Assert.Single(resolved[0].ChildPublications);
        Assert.Equal("http publication url", resolved[0].ChildPublications[0].Kind);
    }

    [Fact]
    public async Task Resolved_text_with_nested_tme_reference_creates_child()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/10",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 10,
            DiscoveredFromText = "https://t.me/c/100/10",
            OriginalSource = "channel-x",
            State = PublicationState.Discovered
        }};

        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult(FakeText("veja https://t.me/c/200/30"));

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);

        // A entrada produz um filho Telegram; o resolver recursa ate'
        // MaxResolutionDepth. Aqui a profundidade e' 1 (depth 0 = entrada,
        // recursao para o filho).
        Assert.True(resolved.Count >= 1);
        var first = resolved[0];
        Assert.Single(first.ChildPublications);
        Assert.Equal("telegram message link", first.ChildPublications[0].Kind);
        Assert.Equal(200L, first.ChildPublications[0].ChannelId);
        Assert.Equal(30, first.ChildPublications[0].MessageId);
    }

    [Fact]
    public async Task Recursive_chain_A_to_B_to_C_resolves()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/1",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 1,
            DiscoveredFromText = "https://t.me/c/100/1",
            OriginalSource = "src",
            State = PublicationState.Discovered
        }};

        TelegramMessageFetcher fetcher = (ch, msg, _) =>
        {
            // Cada mensagem aponta para a proxima via t.me/c/. O "fim da cadeia"
            // tem uma URL HTTP que produz uma sub-publicacao "http publication url"
            // para garantir estado Resolved (sub-refs > 0).
            if (ch == 100 && msg == 1) return Task.FromResult(FakeText("vai para https://t.me/c/200/2"));
            if (ch == 200 && msg == 2) return Task.FromResult(FakeText("vai para https://t.me/c/300/3"));
            if (ch == 300 && msg == 3) return Task.FromResult(FakeText("veja https://example.com/page.html"));
            return Task.FromResult<ResolvedPublication?>(null);
        };

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Equal(3, resolved.Count);
        Assert.All(resolved, r => Assert.Equal(PublicationState.Resolved, r.State));
    }

    [Fact]
    public async Task Cycle_A_to_B_to_A_does_not_loop()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/1",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 1,
            DiscoveredFromText = "https://t.me/c/100/1",
            OriginalSource = "src",
            State = PublicationState.Discovered
        }};

        TelegramMessageFetcher fetcher = (ch, msg, _) =>
        {
            if (ch == 100 && msg == 1) return Task.FromResult(FakeText("volta: https://t.me/c/100/1"));
            return Task.FromResult<ResolvedPublication?>(null);
        };

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        // So 1 mensagem resolvida (a entrada); o ciclo e' cortado pelo seen set.
        Assert.Single(resolved);
    }

    [Fact]
    public async Task Depth_limit_prevents_infinite_chain()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/1/1",
            Kind = "telegram message link",
            ChannelId = 1,
            MessageId = 1,
            DiscoveredFromText = "https://t.me/c/1/1",
            OriginalSource = "src",
            State = PublicationState.Discovered
        }};

        // Cadeia infinita: cada mensagem aponta para a proxima.
        TelegramMessageFetcher fetcher = (ch, msg, _) =>
        {
            int next = msg + 1;
            return Task.FromResult(FakeText($"next https://t.me/c/{ch}/{next}"));
        };

        var resolved = await TelegramPublicationResolver.ResolveAsync(
            refs, fetcher, maxDepth: TelegramPublicationResolver.MaxResolutionDepth);

        // MaxResolutionDepth + 1 entrada (a inicial)
        Assert.True(resolved.Count <= TelegramPublicationResolver.MaxResolutionDepth + 1,
            $"Resolved count {resolved.Count} > MaxResolutionDepth+1");
    }

    [Fact]
    public async Task Same_reference_yields_single_resolution()
    {
        // Duas refs para o mesmo t.me/c/X/Y sao deduplicadas.
        var refs = new[]
        {
            new TelegramPublicationRef
            {
                ReferenceUrl = "https://t.me/c/100/1",
                Kind = "telegram message link",
                ChannelId = 100,
                MessageId = 1,
                DiscoveredFromText = "https://t.me/c/100/1",
                OriginalSource = "src",
                State = PublicationState.Discovered
            },
            new TelegramPublicationRef
            {
                ReferenceUrl = "https://t.me/c/100/1",
                Kind = "telegram message link",
                ChannelId = 100,
                MessageId = 1,
                DiscoveredFromText = "https://t.me/c/100/1",
                OriginalSource = "src",
                State = PublicationState.Discovered
            }
        };

        var fetcherCalls = 0;
        TelegramMessageFetcher fetcher = (_, _, _) =>
        {
            fetcherCalls++;
            return Task.FromResult(FakeText("texto"));
        };

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolved);
        Assert.Equal(1, fetcherCalls); // dedup antes de chamar o fetcher
    }

    [Fact]
    public async Task Fetcher_returns_null_marks_publication_ResolutionFailed()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/1",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 1,
            DiscoveredFromText = "https://t.me/c/100/1",
            OriginalSource = "src",
            State = PublicationState.Discovered
        }};

        TelegramMessageFetcher fetcher = (_, _, _) => Task.FromResult<ResolvedPublication?>(null);

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolved);
        Assert.Equal(PublicationState.ResolutionFailed, resolved[0].State);
        Assert.NotNull(resolved[0].Reason);
    }

    [Fact]
    public async Task Fetcher_throws_RpcException_for_ChannelInvalid_marks_ResolutionFailed()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/9999/9999",
            Kind = "telegram message link",
            ChannelId = 9999,
            MessageId = 9999,
            DiscoveredFromText = "https://t.me/c/9999/9999",
            OriginalSource = "src",
            State = PublicationState.Discovered
        }};

        // WTelegram.WTException tipo generico.
        WTelegram.WTException MakeEx(string m) => new WTelegram.WTException(m);
        TelegramMessageFetcher fetcher = (_, _, _) =>
            throw MakeEx("CHANNEL_INVALID");

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolved);
        Assert.Equal(PublicationState.ResolutionFailed, resolved[0].State);
    }

    [Fact]
    public async Task Fetcher_throws_FLOOD_WAIT_is_handled_and_retried()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/1",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 1,
            DiscoveredFromText = "https://t.me/c/100/1",
            OriginalSource = "src",
            State = PublicationState.Discovered
        }};

        int callCount = 0;
        TelegramMessageFetcher fetcher = (_, _, _) =>
        {
            callCount++;
            if (callCount == 1) throw new WTelegram.WTException("FLOOD_WAIT_5");
            // No retry devolvemos algo que produz conteudo util (sub-ref).
            return Task.FromResult(FakeText("veja https://t.me/c/200/30"));
        };

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher, floodWaitRetryCount: 1);
        Assert.True(resolved.Count >= 1);
        Assert.Equal(PublicationState.Resolved, resolved[0].State);
        // 1 (initial) + 1 (retry) + 1 (recursao para (200,30) filho da mensagem
        // resolvida com sucesso) = 3.
        Assert.True(callCount >= 2, $"fetcher foi chamado {callCount} vezes; esperado >=2");
    }

    [Fact]
    public async Task Resolved_text_with_no_urls_and_no_attachment_marks_Unsupported()
    {
        var refs = new[] { new TelegramPublicationRef
        {
            ReferenceUrl = "https://t.me/c/100/1",
            Kind = "telegram message link",
            ChannelId = 100,
            MessageId = 1,
            DiscoveredFromText = "https://t.me/c/100/1",
            OriginalSource = "src",
            State = PublicationState.Discovered
        }};

        TelegramMessageFetcher fetcher = (_, _, _) =>
            Task.FromResult(FakeText("apenas texto sem URLs ou anexos"));

        var resolved = await TelegramPublicationResolver.ResolveAsync(refs, fetcher);
        Assert.Single(resolved);
        Assert.Equal(PublicationState.Unsupported, resolved[0].State);
    }
}

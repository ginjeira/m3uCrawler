using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using m3uCrawler.Services;
using TL;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-HISTWIN — Janela de histórico Min/Max na enumeração Telegram.
///
/// <para>
/// Exercita <see cref="TelegramScraperService.EnumerateDialogHistoryAsync"/>
/// com épocas fixas (UTC) e delegates em memória: nada de rede. A janela
/// efectiva é <c>Min &lt;= idade &lt;= Max</c> com limites inclusivos;
/// mensagens mais recentes que o Min são saltadas sem terminar a
/// paginação; mensagens mais antigas que o Max terminam a iteração.
/// </para>
/// </summary>
public class TelegramHistoryWindowBandTests
{
    // Epoch fixo (UTC) — determinístico, sem DateTime.UtcNow nos asserts.
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Messages_Messages MakePage(params Message[] msgs)
        => new() { messages = msgs };

    private static Message MakeMessage(int id, DateTime utc)
        => new() { id = id, date = DateTime.SpecifyKind(utc, DateTimeKind.Utc) };

    private static Task<List<Message>> RunBandAsync(
        DateTime now, double maxHours, double minHours,
        Func<int, Task<Messages_MessagesBase?>> pageFetcher)
        => RunCoreAsync(
            now, maxHours,
            minHours > 0 ? now.AddHours(-minHours) : null,
            pageFetcher);

    private static Task<List<Message>> RunLegacyAsync(
        DateTime now, double maxHours,
        Func<int, Task<Messages_MessagesBase?>> pageFetcher)
        => RunCoreAsync(now, maxHours, null, pageFetcher);

    private static async Task<List<Message>> RunCoreAsync(
        DateTime now, double maxHours, DateTime? minCutoff,
        Func<int, Task<Messages_MessagesBase?>> pageFetcher)
    {
        var processed = new List<Message>();
        await TelegramScraperService.EnumerateDialogHistoryAsync(
            resolvedPeer: new object(),
            chatTitle: "test",
            cutoffDate: now.AddHours(-maxHours),
            pageFetcher: pageFetcher,
            onMessage: m => { processed.Add(m); return Task.CompletedTask; },
            minCutoffDate: minCutoff);
        return processed;
    }

    [Fact]
    public async Task Min0_Max384_processes_all_messages_up_to_384h()
    {
        var page = MakePage(
            MakeMessage(1, Now.AddHours(-1)),
            MakeMessage(2, Now.AddHours(-200)),
            MakeMessage(3, Now.AddHours(-384)),
            MakeMessage(4, Now.AddHours(-384).AddSeconds(-1)));

        var banded = await RunBandAsync(
            Now, 384, 0, _ => Task.FromResult<Messages_MessagesBase?>(page));

        Assert.Equal(new[] { 1, 2, 3 }, banded.Select(m => m.ID).ToArray());

        // min = 0 tem de ser IDÊNTICO ao legacy (minCutoffDate: null).
        var legacy = await RunLegacyAsync(
            Now, 384, _ => Task.FromResult<Messages_MessagesBase?>(page));

        Assert.Equal(banded.Select(m => m.ID), legacy.Select(m => m.ID));
    }

    [Fact]
    public async Task Min384_Max720_processes_only_messages_inside_the_band()
    {
        var page = MakePage(
            MakeMessage(1, Now.AddHours(-100)),
            MakeMessage(2, Now.AddHours(-383).AddMinutes(-59)),
            MakeMessage(3, Now.AddHours(-384)),
            MakeMessage(4, Now.AddHours(-500)),
            MakeMessage(5, Now.AddHours(-720)),
            MakeMessage(6, Now.AddHours(-720).AddSeconds(-1)));

        var processed = await RunBandAsync(
            Now, 720, 384, _ => Task.FromResult<Messages_MessagesBase?>(page));

        Assert.Equal(new[] { 3, 4, 5 }, processed.Select(m => m.ID).ToArray());
    }

    [Fact]
    public async Task Min720_Max1000_processes_only_messages_inside_the_band()
    {
        var page = MakePage(
            MakeMessage(1, Now.AddHours(-500)),
            MakeMessage(2, Now.AddHours(-720)),
            MakeMessage(3, Now.AddHours(-900)),
            MakeMessage(4, Now.AddHours(-1000)),
            MakeMessage(5, Now.AddHours(-1000).AddSeconds(-1)));

        var processed = await RunBandAsync(
            Now, 1000, 720, _ => Task.FromResult<Messages_MessagesBase?>(page));

        Assert.Equal(new[] { 2, 3, 4 }, processed.Select(m => m.ID).ToArray());
    }

    [Fact]
    public async Task Messages_newer_than_min_are_skipped_without_stopping_pagination()
    {
        // Página 1 com 100 mensagens (força paginação), todas mais
        // recentes que o Min → skip-continue, sem terminar.
        var page1Messages = new Message[100];
        for (int i = 0; i < 99; i++)
            page1Messages[i] = MakeMessage(1000 + i, Now.AddHours(-10));
        page1Messages[99] = MakeMessage(2000, Now.AddHours(-12));

        var page1 = MakePage(page1Messages);
        var page2 = MakePage(
            MakeMessage(3001, Now.AddHours(-400)),
            MakeMessage(3002, Now.AddHours(-800)));

        int fetches = 0;
        var processed = await RunBandAsync(Now, 720, 384, _ =>
        {
            fetches++;
            return Task.FromResult<Messages_MessagesBase?>(fetches == 1 ? page1 : page2);
        });

        Assert.Equal(2, fetches);
        Assert.Equal(new[] { 3001 }, processed.Select(m => m.ID).ToArray());
    }

    [Fact]
    public async Task Age_equal_to_min_and_max_boundaries_are_accepted()
    {
        var page = MakePage(
            MakeMessage(1, Now.AddHours(-384)),
            MakeMessage(2, Now.AddHours(-720)));

        var processed = await RunBandAsync(
            Now, 720, 384, _ => Task.FromResult<Messages_MessagesBase?>(page));

        Assert.Equal(new[] { 1, 2 }, processed.Select(m => m.ID).ToArray());
    }

    [Fact]
    public async Task Message_older_than_max_stops_iteration_and_no_further_pages_are_fetched()
    {
        var page = MakePage(
            MakeMessage(1, Now.AddHours(-721)),
            MakeMessage(2, Now.AddHours(-722)));

        int fetches = 0;
        var processed = await RunBandAsync(Now, 720, 384, _ =>
        {
            fetches++;
            return Task.FromResult<Messages_MessagesBase?>(page);
        });

        Assert.Equal(1, fetches);
        Assert.Empty(processed);
    }

    [Fact]
    public void Search_methods_expose_minHistoryHours_default_zero()
    {
        var type = typeof(TelegramScraperService);

        var asyncMethod = type.GetMethod(
            "SearchAndTestM3UInTelegramAsync",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(asyncMethod);
        var asyncParam = asyncMethod!.GetParameters().Single(p => p.Name == "minHistoryHours");
        Assert.Equal(0, asyncParam.DefaultValue);

        var internalMethod = type.GetMethod(
            "SearchM3UInTelegramInternal",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(internalMethod);
        var internalParam = internalMethod!.GetParameters().Single(p => p.Name == "minHistoryHours");
        Assert.Equal(0, internalParam.DefaultValue);
    }
}

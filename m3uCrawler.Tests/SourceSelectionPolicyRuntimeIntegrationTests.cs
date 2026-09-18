using System;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.SourceSelection;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-4) — Integração runtime: a política global persistida
/// é resolvida pelo <see cref="SourceSelectionPolicyResolver"/> e aplicada
/// pelo <see cref="SourceSelectionStage"/> tal como em <c>Program.cs</c>.
///
/// <para>
/// <b>Limitação declarada:</b> o wiring exacto de <c>Program.cs</c>
/// (ciclo Telegram ao vivo) não é exercitável sem uma execução Telegram
/// real, pelo que não é simulado. Em vez disso, estes testes replicam a
/// sequência exacta que <c>Program.cs</c> usa — resolver a política global
/// persistida e aplicá-la via <see cref="SourceSelectionStage"/> sobre
/// <c>M3uStream</c>s reais — para validar o comportamento observável da
/// imposição de limites.
/// </para>
/// </summary>
public class SourceSelectionPolicyRuntimeIntegrationTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private CanonicalChannelEntity _channel = null!;

    public SourceSelectionPolicyRuntimeIntegrationTests()
    {
        _dbPath = TestTempDb.SuitePath($"source-selection-policy-runtime-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);

        var channels = await _resolver.ListCanonicalChannelsAsync();
        Assert.NotEmpty(channels);
        _channel = channels[0];
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    private async Task SeedMatchedSourcesAsync(int count)
    {
        var source = await _resolver.EnsureSourceAsync(
            "runtime-policy", "Runtime Policy", SourceKind.Telegram,
            "telegram://runtime-policy", priority: 0);
        for (var i = 0; i < count; i++)
        {
            await _resolver.RecordChannelSourceAsync(
                _channel.Id, source.Id, $"http://host{i}.example/live.ts",
                matchMethod: "test");
        }
    }

    private static M3uStream Stream(string url) => new()
    {
        Url = url,
        Title = "Canal",
        Group = "PT",
        IsWorking = true,
        ResponseTime = 100,
    };

    private Task<SourceSelectionPolicy> ResolvePolicyAsync()
        => new SourceSelectionPolicyResolver(_resolver).ResolveGlobalAsync();

    private async Task<CanonicalChannelEntity> CreateChannelAsync(string prefix)
        => await _resolver.CreateCanonicalChannelAsync(
            $"{prefix}-{Guid.NewGuid():N}",
            prefix,
            EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible,
            isEnabled: true,
            normalizedAliases: Array.Empty<string>());

    private async Task SeedChannelSourcesAsync(
        CanonicalChannelEntity channel, string sourceKey, string[] urls)
    {
        var source = await _resolver.EnsureSourceAsync(
            sourceKey, sourceKey, SourceKind.Telegram, $"telegram://{sourceKey}", priority: 0);
        foreach (var url in urls)
        {
            await _resolver.RecordChannelSourceAsync(
                channel.Id, source.Id, url, matchMethod: "test");
        }
    }

    private async Task<SourceSelectionStageResult> ApplyWithLoadedPoliciesAsync(
        CanonicalChannelEntity channel, string[] urls)
    {
        var inputs = urls.Select(Stream).ToArray();
        var set = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();
        return await new SourceSelectionStage(_resolver).ApplyAsync(inputs, set);
    }

    [Fact]
    public async Task Persisted_limit_changes_how_many_sources_are_selected_and_published()
    {
        await SeedMatchedSourcesAsync(4);
        var inputs = Enumerable.Range(0, 4)
            .Select(i => Stream($"http://host{i}.example/live.ts"))
            .ToArray();

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);
        var permissive = await new SourceSelectionStage(_resolver)
            .ApplyAsync(inputs, await ResolvePolicyAsync());
        Assert.Equal(4, permissive.Selected.Count);
        Assert.Equal(4, permissive.Published.Count);

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(2, true, null, true);
        var restricted = await new SourceSelectionStage(_resolver)
            .ApplyAsync(inputs, await ResolvePolicyAsync());
        Assert.Equal(2, restricted.Selected.Count);
        Assert.Equal(2, restricted.Published.Count);
        Assert.Equal(2, restricted.Rejected.Count);
        Assert.True(restricted.Published.Count < permissive.Published.Count);
    }

    [Fact]
    public async Task Persisted_zero_limit_selects_none_and_does_not_publish_matched_sources()
    {
        await SeedMatchedSourcesAsync(3);
        var matched = Enumerable.Range(0, 3)
            .Select(i => Stream($"http://host{i}.example/live.ts"))
            .ToArray();
        var unmatched = Stream("http://other.example/unknown.ts");
        var inputs = matched.Append(unmatched).ToArray();

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(0, true, null, true);
        var policy = await ResolvePolicyAsync();
        Assert.Equal(0, policy.MaxSourcesPerChannel);

        var result = await new SourceSelectionStage(_resolver).ApplyAsync(inputs, policy);

        Assert.True(result.Applied);
        // MaxSourcesPerChannel = 0 → nenhuma fonte do canal é seleccionada.
        Assert.Empty(result.Selected);
        Assert.Equal(3, result.Rejected.Count);
        Assert.All(result.Rejected, r => Assert.Equal(SelectionReasons.LimitReached, r.Reason));

        // As streams correspondidas não são publicadas; a não correspondida
        // mantém o pass-through da Wave 13-3.
        Assert.Single(result.Published);
        Assert.Same(unmatched, result.Published[0]);
        Assert.All(matched, m => Assert.DoesNotContain(m, result.Published));
    }

    [Fact]
    public async Task Program_wiring_sequence_resolves_persisted_policy_and_publishes_selection()
    {
        // Espelha Program.cs:
        //   policy = catalog is null ? SourceSelectionDefaults.DefaultPolicy
        //                            : resolver.ResolveGlobalAsync()
        //   selection = new SourceSelectionStage(catalog).ApplyAsync(streams, policy)
        //   finalStreams = selection.Published
        await SeedMatchedSourcesAsync(3);
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(1, true, null, true);

        var inputs = Enumerable.Range(0, 3)
            .Select(i => Stream($"http://host{i}.example/live.ts"))
            .ToArray();

        var policy = await new SourceSelectionPolicyResolver(_resolver).ResolveGlobalAsync();
        var selection = await new SourceSelectionStage(_resolver).ApplyAsync(inputs, policy);
        var finalStreams = selection.Published.ToList();

        Assert.True(selection.Applied);
        Assert.Equal(1, policy.MaxSourcesPerChannel);
        Assert.Single(selection.Selected);
        Assert.Equal(2, selection.Rejected.Count);
        Assert.Single(finalStreams);
    }

    [Fact]
    public async Task Channel_override_limit_wins_over_the_global_limit()
    {
        var channel = await CreateChannelAsync("rt-override");
        var urls = Enumerable.Range(0, 100)
            .Select(i => $"http://rt{i}.example/live.ts")
            .ToArray();
        await SeedChannelSourcesAsync(channel, "rt-override-src", urls);

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync(channel.Key, 3, true, null, true);

        var set = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();
        Assert.Equal(1, set.OverrideCount);

        var result = await ApplyWithLoadedPoliciesAsync(channel, urls);

        Assert.True(result.Applied);
        Assert.Equal(3, result.Selected.Count);
        Assert.Equal(3, result.Published.Count);
        Assert.Equal(97, result.Rejected.Count(r => r.Reason == SelectionReasons.LimitReached));
    }

    [Fact]
    public async Task Channel_override_zero_limit_selects_none_and_does_not_publish_matched_sources()
    {
        var channel = await CreateChannelAsync("rt-zero");
        var urls = Enumerable.Range(0, 3)
            .Select(i => $"http://zero{i}.example/live.ts")
            .ToArray();
        await SeedChannelSourcesAsync(channel, "rt-zero-src", urls);

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync(channel.Key, 0, true, null, true);

        var matched = urls.Select(Stream).ToArray();
        var unmatched = Stream("http://not-in-catalog.example/unknown.ts");
        var inputs = matched.Append(unmatched).ToArray();

        var set = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();
        var result = await new SourceSelectionStage(_resolver).ApplyAsync(inputs, set);

        Assert.True(result.Applied);
        Assert.Empty(result.Selected);
        Assert.Equal(3, result.Rejected.Count);
        Assert.All(result.Rejected, r => Assert.Equal(SelectionReasons.LimitReached, r.Reason));

        // Nenhuma das streams correspondidas é publicada; a não correspondida
        // mantém o pass-through.
        Assert.Single(result.Published);
        Assert.Same(unmatched, result.Published[0]);
        Assert.All(matched, m => Assert.DoesNotContain(m, result.Published));
    }

    [Fact]
    public async Task Two_channels_in_the_same_run_receive_their_own_overrides()
    {
        var channelA = await CreateChannelAsync("rt-two-a");
        var channelB = await CreateChannelAsync("rt-two-b");
        var urlsA = Enumerable.Range(0, 4)
            .Select(i => $"http://two-a{i}.example/live.ts")
            .ToArray();
        var urlsB = Enumerable.Range(0, 8)
            .Select(i => $"http://two-b{i}.example/live.ts")
            .ToArray();
        await SeedChannelSourcesAsync(channelA, "rt-two-a-src", urlsA);
        await SeedChannelSourcesAsync(channelB, "rt-two-b-src", urlsB);

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync(channelA.Key, 2, true, null, true);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync(channelB.Key, 5, true, null, true);

        var set = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();
        Assert.Equal(2, set.OverrideCount);

        var inputs = urlsA.Concat(urlsB).Select(Stream).ToArray();
        var result = await new SourceSelectionStage(_resolver).ApplyAsync(inputs, set);

        Assert.Equal(2, result.MatchedChannelCount);
        Assert.Equal(7, result.Selected.Count);

        var selectedUrls = result.Selected
            .Select(s => s.Candidate.StreamUrl)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(2, urlsA.Count(selectedUrls.Contains));
        Assert.Equal(5, urlsB.Count(selectedUrls.Contains));
    }

    [Fact]
    public async Task Channel_override_max_sources_per_provider_is_applied()
    {
        var channel = await CreateChannelAsync("rt-provider");
        // Todas as streams no mesmo host → mesma identidade de fornecedor.
        var urls = Enumerable.Range(0, 4)
            .Select(i => $"http://same-provider.example/live{i}.ts")
            .ToArray();
        await SeedChannelSourcesAsync(channel, "rt-provider-src", urls);
        var inputs = urls.Select(Stream).ToArray();

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);
        var globalSet = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();
        var globalResult = await new SourceSelectionStage(_resolver).ApplyAsync(inputs, globalSet);
        // Global: diversidade + fallback permitido → as 4 do mesmo fornecedor.
        Assert.Equal(4, globalResult.Selected.Count);

        await _resolver.UpsertChannelSourceSelectionPolicyAsync(channel.Key, 10, false, 1, true);
        var overrideSet = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();
        var overrideResult = await new SourceSelectionStage(_resolver).ApplyAsync(inputs, overrideSet);

        Assert.Single(overrideResult.Selected);
        Assert.Equal(3, overrideResult.Rejected.Count(r => r.Reason == SelectionReasons.ProviderLimit));
    }

    [Fact]
    public async Task Channel_override_fallback_flag_is_applied()
    {
        var channel = await CreateChannelAsync("rt-fallback");
        var urls = Enumerable.Range(0, 3)
            .Select(i => $"http://same-fallback.example/live{i}.ts")
            .ToArray();
        await SeedChannelSourcesAsync(channel, "rt-fallback-src", urls);
        var inputs = urls.Select(Stream).ToArray();

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);
        var globalSet = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();
        var globalResult = await new SourceSelectionStage(_resolver).ApplyAsync(inputs, globalSet);
        Assert.Equal(3, globalResult.Selected.Count);

        await _resolver.UpsertChannelSourceSelectionPolicyAsync(channel.Key, 10, true, null, false);
        var overrideSet = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();
        var overrideResult = await new SourceSelectionStage(_resolver).ApplyAsync(inputs, overrideSet);

        Assert.Single(overrideResult.Selected);
        Assert.Equal(2, overrideResult.Rejected.Count(r => r.Reason == SelectionReasons.FallbackDisabled));
    }
}

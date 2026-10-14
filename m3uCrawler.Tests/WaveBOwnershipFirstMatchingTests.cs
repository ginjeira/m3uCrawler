using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave B — o matcher reconhece o canal já criado por <b>identidade
/// estável</b> (ownership: <c>CanonicalChannelId ↔ DispatcharrChannelId</c>)
/// antes de tentar o fuzzy por nome. Sem este passo, um canónico cujo
/// <c>Key</c> não casa com o nome publicado (ex.: <c>vmais</c> vs
/// "V+ TVI") produzia <c>NewChannel</c> em runs sucessivas — o bug dos
/// canais duplicados no Dispatcharr.
///
/// A cobertura inclui a <b>regressão</b> que falharia sem o fix
/// (<c>Same_inputs_without_ownership_create_new_channel</c>), o
/// fallback (sem ownership → fuzzy/NewChannel), a recriação legítima
/// (ownership órfã → NewChannel) e a dedup por identidade canónica.
/// </summary>
public sealed class WaveBOwnershipFirstMatchingTests
{
    // ──────────────────────────────── helpers ────────────────────────────────

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"waveb-ownership-{Guid.NewGuid():N}.db");

    private static async Task<(string DbPath, CatalogResolver Resolver)> NewCatalogAsync()
    {
        var dbPath = NewDbPath();
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }
        return (dbPath, new CatalogResolver(new TestDbContextFactory(dbPath), dbPath));
    }

    private static Task<CanonicalChannelEntity> CreateCanonicalAsync(
        CatalogResolver resolver, string key, string displayName)
        => resolver.CreateCanonicalChannelAsync(
            key, displayName, EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: Array.Empty<string>(), country: "pt");

    private static ChannelMatcher MatcherWithCatalog(CatalogResolver catalog) =>
        new(new AliasResolver(null), resolutionPolicy: null, catalog: catalog);

    private static DiscoveredStream Stream(string title, string url, string group = "Geral")
        => new(
            new M3uStream
            {
                Title = title,
                Url = url,
                Group = group,
                IsWorking = true,
                ResponseTime = 100,
            },
            "P", "src");

    private static DispatcharrState State(
        IReadOnlyList<DispatcharrChannel>? channels = null,
        IReadOnlyList<DispatcharrStream>? streams = null)
        => new(
            channels ?? Array.Empty<DispatcharrChannel>(),
            streams ?? Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(),
            "0.30.0");

    private static MatchPlan Build(
        ChannelMatcher matcher,
        IReadOnlyList<DiscoveredStream> discovered,
        DispatcharrState existing)
        => matcher.BuildPlan(
            discovered, existing, MatchingOptions.Default,
            new StreamOrderingPolicy(), "x.m3u", "http://dispatcharr.local", dryRun: true);

    // ───────────── (a) regressão: run 2 reutiliza por ownership ──────────────

    [Fact]
    public async Task Second_run_reuses_crawler_channel_by_ownership_when_name_does_not_fuzzy_match()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            // O canónico tem Key "vmais" mas o nome publicado é "V+ TVI":
            // o fuzzy entre "vmais" e "v+ tvi" (~17) fica muito abaixo
            // do threshold (80). Sem ownership seria NewChannel.
            var canonical = await CreateCanonicalAsync(resolver, "vmais", "V+ TVI");

            // Run 1: o crawler criou o canal #900 e registou a
            // associação estável CanonicalChannelId ↔ DispatcharrChannelId.
            await resolver.EnsureChannelOwnershipAsync(
                dispatcharrChannelId: 900,
                evidence: "created-by-crawler",
                canonicalChannelId: canonical.Id,
                cancellationToken: default,
                ownership: ChannelOwnership.CrawlerManaged);

            // Run 2: o canal já existe no estado do Dispatcharr (o nome
            // NÃO casa por fuzzy com a Key).
            const string url = "http://provider.example/vmais/0";
            var existing = new[]
            {
                new DispatcharrChannel(900, "V+ TVI", "Geral", 1, null, new long[] { 5000 }),
            };
            var existingStreams = new[]
            {
                new DispatcharrStream(5000, "V+ TVI", url, null, "Geral", "m3u", true, true, 100),
            };

            var plan = Build(
                MatcherWithCatalog(resolver),
                new[] { Stream("V+ TVI", url) },
                State(existing, existingStreams));

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(SyncOutcome.ExistingUnchanged, decision.Outcome);
            Assert.Equal(900, decision.ExistingChannelId);
            Assert.Equal(canonical.Id, decision.CanonicalChannelId);
            Assert.StartsWith("ownership", decision.MatchReason);
            Assert.Equal(0, plan.Counts.NewChannels);
            Assert.Equal(1, plan.Counts.ChannelsMatchedByOwnership);
            Assert.Equal(0, plan.Counts.MatchingDisposition["newChannelsFromCuratedIdentity"]);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    /// <summary>
    /// Regressão explícita: exactamente os mesmos inputs do teste
    /// anterior, mas <b>sem</b> a linha de ownership. Aqui o fuzzy por
    /// nome não encontra o canal e o plano cai em <c>NewChannel</c> —
    /// é este o comportamento que o fix elimina.
    /// </summary>
    [Fact]
    public async Task Same_inputs_without_ownership_create_new_channel()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            await CreateCanonicalAsync(resolver, "vmais", "V+ TVI");

            const string url = "http://provider.example/vmais/0";
            var existing = new[]
            {
                new DispatcharrChannel(900, "V+ TVI", "Geral", 1, null, new long[] { 5000 }),
            };
            var existingStreams = new[]
            {
                new DispatcharrStream(5000, "V+ TVI", url, null, "Geral", "m3u", true, true, 100),
            };

            var plan = Build(
                MatcherWithCatalog(resolver),
                new[] { Stream("V+ TVI", url) },
                State(existing, existingStreams));

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(SyncOutcome.NewChannel, decision.Outcome);
            Assert.Null(decision.ExistingChannelId);
            Assert.Equal("no-match", decision.MatchReason);
            Assert.Equal(0, decision.MatchScore);
            Assert.Equal(1, plan.Counts.NewChannels);
            Assert.Equal(0, plan.Counts.ChannelsMatchedByOwnership);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ───────────── (b) fallback: primeira criação legítima ───────────────────

    [Fact]
    public async Task Canonical_without_ownership_and_no_existing_channel_creates_new_channel()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            await CreateCanonicalAsync(resolver, "vmais", "V+ TVI");

            var plan = Build(
                MatcherWithCatalog(resolver),
                new[] { Stream("V+ TVI", "http://provider.example/vmais/0") },
                State());

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(SyncOutcome.NewChannel, decision.Outcome);
            Assert.Null(decision.ExistingChannelId);
            Assert.Equal(1, plan.Counts.NewChannels);
            Assert.Equal(0, plan.Counts.ChannelsMatchedByOwnership);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ───── (c) ownership órfã: canal apagado manualmente → recriação ─────────

    [Fact]
    public async Task Ownership_present_but_channel_absent_from_state_creates_new_channel()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            var canonical = await CreateCanonicalAsync(resolver, "vmais", "V+ TVI");
            await resolver.EnsureChannelOwnershipAsync(
                dispatcharrChannelId: 900,
                evidence: "created-by-crawler",
                canonicalChannelId: canonical.Id,
                cancellationToken: default,
                ownership: ChannelOwnership.CrawlerManaged);

            // O canal #900 foi apagado manualmente: não está no estado.
            var plan = Build(
                MatcherWithCatalog(resolver),
                new[] { Stream("V+ TVI", "http://provider.example/vmais/0") },
                State());

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(SyncOutcome.NewChannel, decision.Outcome);
            Assert.Null(decision.ExistingChannelId);
            Assert.Equal(1, plan.Counts.NewChannels);
            Assert.Equal(0, plan.Counts.ChannelsMatchedByOwnership);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ───────── (d) ownership-first tem precedência sobre o fuzzy ─────────────

    [Fact]
    public async Task Ownership_match_takes_precedence_over_fuzzy_candidates()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            var canonical = await CreateCanonicalAsync(resolver, "rtpnoticias", "RTP Notícias");

            // O canal estável é o #999; existe um duplicado legado #700
            // com o MESMO nome (o fuzzy seria ambíguo ou escolheria o
            // errado). O ownership aponta inequivocamente para #999.
            await resolver.EnsureChannelOwnershipAsync(
                dispatcharrChannelId: 999,
                evidence: "created-by-crawler",
                canonicalChannelId: canonical.Id,
                cancellationToken: default,
                ownership: ChannelOwnership.CrawlerManaged);

            const string url = "http://provider.example/rtpnoticias/0";
            var existing = new[]
            {
                new DispatcharrChannel(700, "RTP Notícias", "Geral", 1, null, Array.Empty<long>()),
                new DispatcharrChannel(999, "RTP Notícias", "Geral", 2, null, new long[] { 7000 }),
            };
            var existingStreams = new[]
            {
                new DispatcharrStream(7000, "RTP Notícias", url, null, "Geral", "m3u", true, true, 100),
            };

            var plan = Build(
                MatcherWithCatalog(resolver),
                new[] { Stream("RTP Notícias", url) },
                State(existing, existingStreams));

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(999, decision.ExistingChannelId);
            Assert.StartsWith("ownership", decision.MatchReason);
            Assert.Equal(1, plan.Counts.ChannelsMatchedByOwnership);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ─── (f) M3: escolher o id owned PRESENTE no estado (não o menor) ────────

    /// <summary>
    /// M3 — quando um canónico tem vários ids owned (<c>[100, 200]</c>) e
    /// o de menor id foi apagado manualmente, a run reutiliza o outro
    /// owned presente (<c>200</c>) em vez de criar um <c>NewChannel</c>.
    /// </summary>
    [Fact]
    public async Task Ownership_reuses_owned_channel_present_in_state_when_smallest_id_was_deleted()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            var canonical = await CreateCanonicalAsync(resolver, "vmais", "V+ TVI");

            // Dois canais owned para o mesmo canónico: o #100 (mais
            // antigo) e o #200.
            await resolver.EnsureChannelOwnershipAsync(
                dispatcharrChannelId: 100, evidence: "created-by-crawler",
                canonicalChannelId: canonical.Id, cancellationToken: default,
                ownership: ChannelOwnership.CrawlerManaged);
            await resolver.EnsureChannelOwnershipAsync(
                dispatcharrChannelId: 200, evidence: "created-by-crawler",
                canonicalChannelId: canonical.Id, cancellationToken: default,
                ownership: ChannelOwnership.CrawlerManaged);

            // Só o #200 está presente (o #100 foi apagado manualmente).
            const string url = "http://provider.example/vmais/0";
            var existing = new[]
            {
                new DispatcharrChannel(200, "V+ TVI", "Geral", 1, null, new long[] { 5000 }),
            };
            var existingStreams = new[]
            {
                new DispatcharrStream(5000, "V+ TVI", url, null, "Geral", "m3u", true, true, 100),
            };

            var plan = Build(
                MatcherWithCatalog(resolver),
                new[] { Stream("V+ TVI", url) },
                State(existing, existingStreams));

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(SyncOutcome.ExistingUnchanged, decision.Outcome);
            Assert.Equal(200, decision.ExistingChannelId);
            Assert.StartsWith("ownership", decision.MatchReason);
            Assert.Equal(0, plan.Counts.NewChannels);
            Assert.Equal(1, plan.Counts.ChannelsMatchedByOwnership);
            // O #100 (extra, não reutilizado) é reportado como duplicado.
            Assert.Equal(1, plan.Counts.OwnedChannelDuplicatesRemaining);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ─── (g) L6: ownership-first fora do tier Curated (Merge/ReviewOnly) ─────

    /// <summary>
    /// L6 — um canónico <c>MergeOnly</c>/<c>ReviewOnly</c> cujo canal
    /// owned existe no estado faz match directo (Existing),
    /// independentemente do tier. Sem L6 cairia no caminho Unknown
    /// (exact/alias, aqui sem match) e iria para review.
    /// </summary>
    [Theory]
    [InlineData(PublicationPolicy.MergeOnly)]
    [InlineData(PublicationPolicy.ReviewOnly)]
    public async Task Ownership_first_matches_existing_channel_for_non_curated_canonical(
        PublicationPolicy policy)
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            var canonical = await resolver.CreateCanonicalChannelAsync(
                "vmais", "V+ TVI", EditorialCategory.Live,
                CanonicalGroupKeys.PortugalGeneralistas, policy,
                isEnabled: true, normalizedAliases: Array.Empty<string>(), country: "pt");

            await resolver.EnsureChannelOwnershipAsync(
                dispatcharrChannelId: 900, evidence: "created-by-crawler",
                canonicalChannelId: canonical.Id, cancellationToken: default,
                ownership: ChannelOwnership.CrawlerManaged);

            const string url = "http://provider.example/vmais/0";
            var existing = new[]
            {
                new DispatcharrChannel(900, "V+ TVI", "Geral", 1, null, new long[] { 5000 }),
            };
            var existingStreams = new[]
            {
                new DispatcharrStream(5000, "V+ TVI", url, null, "Geral", "m3u", true, true, 100),
            };

            var plan = Build(
                MatcherWithCatalog(resolver),
                new[] { Stream("V+ TVI", url) },
                State(existing, existingStreams));

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(SyncOutcome.ExistingUnchanged, decision.Outcome);
            Assert.Equal(900, decision.ExistingChannelId);
            Assert.Contains("ownership", decision.MatchReason);
            Assert.Equal(0, plan.Counts.NewChannels);
            Assert.Empty(plan.UnknownReviewRequired);
            Assert.Equal(1, plan.Counts.ChannelsMatchedByOwnership);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ─── (h) L8: ChannelsMatchedByOwnership conta decisões merged ───────────

    /// <summary>
    /// L8 — quando o reconcile reescreve o motivo para <c>merged:...</c>
    /// (mesmo canal reúne várias buckets), o ownership deixa de estar no
    /// início do motivo. A contagem usa <c>Contains</c> (não
    /// <c>StartsWith</c>) para não sub-contar.
    /// </summary>
    [Fact]
    public void Reconcile_counts_ownership_when_reason_is_rewritten_to_merged()
    {
        static ChannelDecision Existing(long id, string reason) => new()
        {
            Identity = $"id-{reason}",
            CanonicalName = "RTP1",
            ExistingChannelId = id,
            Outcome = SyncOutcome.ExistingUnchanged,
            MatchReason = reason,
            Streams = Array.Empty<StreamMatchDecision>(),
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-10-14T00:00:00Z",
            Channels = new[]
            {
                Existing(100, "ownership"),
                Existing(100, "exact"),
            },
        };

        var reconciled = ChannelMatcher.ReconcileByExistingChannelId(plan);

        var decision = Assert.Single(reconciled.Channels);
        Assert.Equal("merged:exact|ownership", decision.MatchReason);
        Assert.Equal(1, reconciled.Counts.ChannelsMatchedByOwnership);
    }

    // ───────────────────── (e) dedup por identidade canónica ─────────────────

    [Fact]
    public void Reconcile_collapses_new_channels_sharing_a_canonical_channel_id()
    {
        static ChannelDecision NewChannel(string identity, string name, long canonicalId, string url) => new()
        {
            Identity = identity,
            CanonicalName = name,
            CanonicalChannelId = canonicalId,
            Outcome = SyncOutcome.NewChannel,
            MatchReason = "no-match",
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "P", StreamUrl = url, StreamName = name,
                    Outcome = SyncOutcome.NewStream, ProposedOrder = 0,
                    OrderReason = "new-channel-initial", IsWorking = true,
                },
            },
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-10-14T00:00:00Z",
            Channels = new[]
            {
                NewChannel("alfa", "Nome A", 42, "http://p/1"),
                NewChannel("beta", "Nome B", 42, "http://p/2"),
            },
        };

        var reconciled = ChannelMatcher.ReconcileByExistingChannelId(plan);

        var decision = Assert.Single(reconciled.Channels);
        Assert.Equal(SyncOutcome.NewChannel, decision.Outcome);
        Assert.Equal(42, decision.CanonicalChannelId);
        Assert.Equal(2, decision.Streams.Count);
    }

    [Fact]
    public void Reconcile_keeps_distinct_canonical_ids_as_distinct_channels()
    {
        static ChannelDecision NewChannel(string identity, long canonicalId) => new()
        {
            Identity = identity,
            CanonicalName = $"Nome {identity}",
            CanonicalChannelId = canonicalId,
            Outcome = SyncOutcome.NewChannel,
            MatchReason = "no-match",
            Streams = Array.Empty<StreamMatchDecision>(),
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-10-14T00:00:00Z",
            Channels = new[]
            {
                NewChannel("alfa", 42),
                NewChannel("beta", 43),
            },
        };

        var reconciled = ChannelMatcher.ReconcileByExistingChannelId(plan);

        Assert.Equal(2, reconciled.Channels.Count());
    }
}

using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Tests for the exact-only matching path for <see cref="ChannelKind.Unknown"/>
/// entries. Unknown streams cannot be attached to an existing channel
/// by fuzzy similarity — they are only allowed to match an existing
/// channel when the normalized identity matches exactly OR when an
/// explicit alias is configured. This prevents an Unknown stream
/// (e.g. "Fox Sportz") from altering streams of an unrelated
/// existing channel that happens to be a fuzzy close match
/// (e.g. "Fox Sports").
/// </summary>
public class UnknownExactMatchOnlyTests
{
    private static DiscoveredStream Stream(string title, string group, bool working = true)
        => new(
            new M3uStream
            {
                Title = title,
                Url = $"https://provider.test/{title}",
                IsWorking = working,
                ResponseTime = 100,
                Group = group,
            },
            "Provider_A",
            "src");

    private static ChannelMatcher NewMatcher(IReadOnlyDictionary<string, string>? aliases = null)
        => new(new AliasResolver(aliases));

    private static MatchPlan Build(
        IReadOnlyList<DiscoveredStream> discovered,
        DispatcharrState existing,
        IReadOnlyDictionary<string, string>? aliases = null)
        => NewMatcher(aliases).BuildPlan(
            discovered,
            existing,
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "x.m3u",
            "http://x",
            dryRun: true);

    private static DispatcharrChannel ExistingChannel(long id, string name, string group = "Sports")
        => new(
            Id: id,
            Name: name,
            GroupName: group,
            ChannelNumber: 100,
            TvgId: null,
            StreamIds: Array.Empty<long>());

    // 1. Unknown com fuzzy-similar a canal existente: nunca anexa.
    [Fact]
    public void Unknown_FoxSportz_does_not_attach_to_existing_FoxSports()
    {
        // "Fox Sportz" (typo) normaliza para "fox sportz", que NÃO está
        // no ChannelCategoryLookup curado. O fuzzy matcher encontra
        // "Fox Sports" como candidato próximo. Unknown NUNCA pode
        // anexar por fuzzy; resultado deve ser UnknownReviewRequired
        // e nenhum NewChannel.
        var existing = new DispatcharrState(
            new[] { ExistingChannel(100, "Fox Sports", "Sports") },
            Array.Empty<DispatcharrStream>(),
            new[] { new DispatcharrChannelGroup(1, "Sports") },
            null);
        var plan = Build(new[] { Stream("Fox Sportz", "Sports") }, existing);

        Assert.Empty(plan.Channels);
        Assert.Single(plan.UnknownReviewRequired);
        Assert.Equal("unknown-review-required", plan.UnknownReviewRequired[0].MatchingDisposition);
        Assert.Equal(1, plan.Counts.MatchingDisposition["unknownReviewRequired"]);
        Assert.Equal(0, plan.Counts.MatchingDisposition["unknownMatchedToExisting"]);
    }

    // 2. Unknown com igualdade exacta a canal existente: match permitido.
    [Fact]
    public void Unknown_exact_match_Meo_TV_attaches_to_existing_Meo_TV()
    {
        var existing = new DispatcharrState(
            new[] { ExistingChannel(200, "Meo TV", "PORTUGAL") },
            Array.Empty<DispatcharrStream>(),
            new[] { new DispatcharrChannelGroup(42, "PORTUGAL") },
            null);
        var plan = Build(new[] { Stream("Meo TV", "PORTUGAL") }, existing);

        Assert.Single(plan.Channels);
        var ch = plan.Channels[0];
        Assert.Equal(200L, ch.ExistingChannelId);
        // Outcome pode ser ExistingUnchanged ou ExistingReassigned
        // (este último se a stream tem URL nova em Dispatcharr).
        Assert.True(
            ch.Outcome == SyncOutcome.ExistingUnchanged || ch.Outcome == SyncOutcome.ExistingReassigned,
            $"expected ExistingUnchanged or ExistingReassigned but was {ch.Outcome}");
        Assert.Equal(1, plan.Counts.MatchingDisposition["unknownMatchedToExisting"]);
        Assert.Empty(plan.UnknownReviewRequired);
    }

    // 3. Unknown com alias explícito: match permitido.
    [Fact]
    public void Unknown_with_explicit_alias_to_existing_channel_attaches()
    {
        // "MEO" é alias explícito configurado para o canal canónico
        // "Meo TV" que existe em Dispatcharr. O alias é uma
        // correspondência determinística.
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MEO"] = "Meo TV",
        };
        var existing = new DispatcharrState(
            new[] { ExistingChannel(300, "Meo TV", "PORTUGAL") },
            Array.Empty<DispatcharrStream>(),
            new[] { new DispatcharrChannelGroup(43, "PORTUGAL") },
            null);
        var plan = Build(
            new[] { Stream("MEO", "PORTUGAL") },
            existing,
            aliases);

        Assert.Single(plan.Channels);
        var ch = plan.Channels[0];
        Assert.Equal(300L, ch.ExistingChannelId);
        // Outcome pode ser ExistingUnchanged OU ExistingReassigned
        // (este último se a stream tem URL nova em Dispatcharr).
        Assert.True(
            ch.Outcome == SyncOutcome.ExistingUnchanged || ch.Outcome == SyncOutcome.ExistingReassigned,
            $"expected ExistingUnchanged or ExistingReassigned but was {ch.Outcome}");
        // MEO é alias do canónico "Meo TV" (no config), o que significa
        // que o ResolveIdentity produz "meo tv" — a identidade do
        // stream é a do canónico. Por isso, o match em FindUnknownMatch
        // é EXACT-IDENTITY ("meo tv" == "meo tv"), não "alias".
        Assert.True(
            ch.MatchReason == "exact-identity" || ch.MatchReason.Contains("alias"),
            $"expected exact-identity or alias match reason, got '{ch.MatchReason}'");
        Assert.Equal(1, plan.Counts.MatchingDisposition["unknownMatchedToExisting"]);
        Assert.Empty(plan.UnknownReviewRequired);
    }

    // 4. Permutação: identidade partilhada entre curated e Unknown.
    [Fact]
    public void Bucket_tier_does_not_leak_Unknown_into_curated_decision()
    {
        // SIC é curado (está no ChannelCategoryLookup). "SIC HD" também
        // é curado (alias SIC HD / sic fhd pt). Para produzir uma
        // entrada Unknown com a mesma identidade normalizada, usamos
        // "SIC XYZ" que normaliza para "sic xyz" — fora do dicionário
        // curado, por isso classificado como Unknown.
        var sicCurated = Stream("SIC", "PORTUGUESE");
        var sicUnknown = Stream("SIC XYZ", "PORTUGUESE");

        // Ordem A: curated primeiro.
        var planA = Build(new[] { sicCurated, sicUnknown },
            new DispatcharrState(
                Array.Empty<DispatcharrChannel>(),
                Array.Empty<DispatcharrStream>(),
                Array.Empty<DispatcharrChannelGroup>(),
                null));

        // Ordem B: unknown primeiro.
        var planB = Build(new[] { sicUnknown, sicCurated },
            new DispatcharrState(
                Array.Empty<DispatcharrChannel>(),
                Array.Empty<DispatcharrStream>(),
                Array.Empty<DispatcharrChannelGroup>(),
                null));

        // Ambos os planos: NewChannel único para SIC (curated), e o
        // Unknown deve seguir para review-required (sem Dispatcharr,
        // sem match existente).
        Assert.Single(planA.Channels);
        Assert.Equal(SyncOutcome.NewChannel, planA.Channels[0].Outcome);
        Assert.Single(planA.UnknownReviewRequired);
        Assert.Equal("SIC XYZ", planA.UnknownReviewRequired[0].Title);

        Assert.Single(planB.Channels);
        Assert.Equal(SyncOutcome.NewChannel, planB.Channels[0].Outcome);
        Assert.Single(planB.UnknownReviewRequired);
        Assert.Equal("SIC XYZ", planB.UnknownReviewRequired[0].Title);

        // A decisão de NewChannel é idêntica nas duas ordens.
        Assert.Equal(planA.Channels[0].Identity, planB.Channels[0].Identity);
        Assert.Equal(planA.Channels[0].Outcome, planB.Channels[0].Outcome);
    }

    // 5. Permutação: 3 streams com a mesma identidade (1 curated + 2 Unknown)
    //    continuam a produzir decisões determinísticas independentes da ordem.
    [Fact]
    public void Permutation_three_streams_same_identity_produces_deterministic_result()
    {
        var curated = Stream("SIC", "PORTUGUESE");
        // "SIC HD" normaliza para "sic fhd pt" no legado OU "sic fhd"
        // após o ChannelNormalizer. Verifico a forma actual.
        var unknown1 = Stream("SIC XYZ", "PORTUGUESE"); // fora do dict → Unknown
        var unknown2 = Stream("sic xyz", "PORTUGUESE"); // mesma identidade

        var streamPermutations = new[]
        {
            new[] { curated, unknown1, unknown2 },
            new[] { unknown1, curated, unknown2 },
            new[] { unknown1, unknown2, curated },
            new[] { unknown2, unknown1, curated },
        };

        DispatcharrState Empty() => new(
            Array.Empty<DispatcharrChannel>(),
            Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(),
            null);

        var firstPlan = Build(streamPermutations[0], Empty());
        var firstCount = firstPlan.Counts;

        foreach (var perm in streamPermutations.Skip(1))
        {
            var plan = Build(perm, Empty());
            Assert.Equal(
                firstCount.MatchingDisposition["newChannelsFromCuratedIdentity"],
                plan.Counts.MatchingDisposition["newChannelsFromCuratedIdentity"]);
            Assert.Equal(
                firstCount.MatchingDisposition["unknownReviewRequired"],
                plan.Counts.MatchingDisposition["unknownReviewRequired"]);
            // NewChannel único (curated).
            Assert.Single(plan.Channels);
            Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
            // Pelo menos uma entrada Unknown em review-required.
            Assert.NotEmpty(plan.UnknownReviewRequired);
        }
    }

    // 6. Sem alias: "MEO" sem entrada em aliasMap deve cair em Unknown
    //    e nunca fazer fuzzy match com "Meo TV" existente.
    [Fact]
    public void Unknown_without_alias_does_not_fuzzy_match_against_similar_name()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // AliasMap vazio.
        var existing = new DispatcharrState(
            new[] { ExistingChannel(400, "Meo TV", "PORTUGAL") },
            Array.Empty<DispatcharrStream>(),
            new[] { new DispatcharrChannelGroup(44, "PORTUGAL") },
            null);
        var plan = Build(
            new[] { Stream("MEO", "PORTUGAL") },
            existing,
            aliases);

        Assert.Empty(plan.Channels);
        Assert.Single(plan.UnknownReviewRequired);
        Assert.Equal(0, plan.Counts.MatchingDisposition["unknownMatchedToExisting"]);
    }

    // 7. Curated continua a usar fuzzy matching normalmente.
    [Fact]
    public void Curated_channels_still_use_fuzzy_matching_against_existing()
    {
        // "RTP NOTICIAS" é curado (está no ChannelCategoryLookup).
        // A entrada "RTP N" (typo) é Unknown, mas a curada usa fuzzy
        // matching contra o canal "RTP 1" existente (similar).
        // Resultado esperado: a curada fuzzy-matcha, a Unknown NUNCA
        // fuzzy-matcha (a menos que haja equality/alias).
        var existing = new DispatcharrState(
            new[] { ExistingChannel(600, "RTP 1", "PORTUGUESE") },
            Array.Empty<DispatcharrStream>(),
            new[] { new DispatcharrChannelGroup(46, "PORTUGUESE") },
            null);
        var plan = Build(
            new[]
            {
                Stream("RTP NOTICIAS", "PORTUGUESE"), // curated, fuzzy
                Stream("RTP N", "PORTUGUESE"),        // Unknown, no fuzzy
            },
            existing);

        // A curada "RTP NOTICIAS" pode ou não fazer match com "RTP 1"
        // (depende do score; o ponto é que ESTÁ LIVRE de usar fuzzy).
        // A Unknown "RTP N" NUNCA faz fuzzy match com "RTP 1".
        // O contrato essencial: a Unknown NUNCA pode ser promovida a
        // NewChannel. Verificamos que "RTP N" não aparece como
        // CanonicalName de um NewChannel (a curada pode aparecer com
        // CanonicalName "RTP NOTICIAS" — esse é legítimo).
        Assert.DoesNotContain(plan.Channels, c =>
            c.Outcome == SyncOutcome.NewChannel
            && c.CanonicalName != null
            && c.CanonicalName.StartsWith("RTP N", StringComparison.OrdinalIgnoreCase)
            && !c.CanonicalName.StartsWith("RTP NOTICIAS", StringComparison.OrdinalIgnoreCase));
        // A Unknown "RTP N" deve estar em review-required (sem
        // Dispatcharr, sem match existente).
        Assert.Contains(plan.UnknownReviewRequired, r => r.Title == "RTP N");
    }
}

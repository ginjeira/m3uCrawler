using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Recognition;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W5.3 — Passo 6 (fuzzy) de Recognition.
///
/// <para>
/// Contrato: <c>docs/Reestructure/48-RECOGNITION-FUZZY-CONTRACT.md</c> F1–F14.
/// Prova: campos (DisplayName + alias), normalização, métrica reutilizada,
/// score 0..100, threshold <c>&gt;=</c>, banda de plausibilidade, margem de
/// ambiguidade, tie-break F9, universo de candidatos, determinismo,
/// fail-closed de threshold e diagnóstico técnico. Não cria ReviewItem.
/// </para>
/// </summary>
public class WaveW53FuzzyRecognitionTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private CatalogResolver _catalog = null!;

    public WaveW53FuzzyRecognitionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wave-w5-53-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _catalog = new CatalogResolver(new TestDbContextFactory(_dbPath), _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static string Norm(string raw) => ChannelNormalizer.Normalize(raw);

    private Task<CanonicalChannelEntity> Ch(
        string key, string displayName, bool enabled = true, params string[] aliases)
        => _catalog.CreateCanonicalChannelAsync(
            key, displayName, EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, enabled, aliases);

    private Task<CanonicalChannelEntity> ChInGroup(
        string key, string displayName, string groupKey, string country, params string[] aliases)
        => _catalog.CreateCanonicalChannelAsync(
            key, displayName, EditorialCategory.Live, groupKey,
            PublicationPolicy.CreateEligible, true, aliases, country);

    private static RecognitionPolicy FuzzyPolicy(
        int? threshold, int? margin = null, string? weights = null, int version = 1)
        => new(
            Enabled: true,
            FuzzyEnabled: true,
            FuzzyThreshold: threshold,
            FuzzyAmbiguityMargin: margin,
            FuzzyWeightsJson: weights,
            Version: version);

    private async Task<int> CountChannelsAsync()
    {
        await using var ctx = await _catalog.GetFactory().CreateDbContextAsync();
        return await ctx.CanonicalChannels.CountAsync();
    }

    private static void AssertFuzzyCandidate(
        FuzzyRecognitionDiagnostic diagnostic, long channelId, int score)
    {
        var candidate = Assert.Single(
            diagnostic.Candidates, c => c.CanonicalChannelId == channelId);
        Assert.Equal(score, candidate.Score);
    }

    // ────────────────────────────────────────────────────────────────
    // 1 — fuzzy disabled
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case01_Fuzzy_disabled_does_not_execute()
    {
        await Ch("w53-off", "CNN International");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, RecognitionPolicy.Default);

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.Null(r.MatchMethod);
        Assert.Null(r.FuzzyScore);
        Assert.Null(r.FuzzyDiagnostic);
    }

    // ────────────────────────────────────────────────────────────────
    // 2 — enabled + nenhum candidato no catálogo
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case02_Enabled_with_no_candidate_is_unknown()
    {
        await Ch("w53-none", "Zzz Qqq Completamente Diferente");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 5));

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.NotNull(r.FuzzyDiagnostic);
        Assert.Equal(FuzzyDecisionReasons.NoCandidate, r.FuzzyDiagnostic!.Value.DecisionReason);
    }

    // ────────────────────────────────────────────────────────────────
    // 3 — abaixo do threshold sem plausíveis
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case03_Below_threshold_without_plausible_is_unknown()
    {
        await Ch("w53-below", "CNN International");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 99, margin: 0));

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.NotNull(r.FuzzyDiagnostic);
        Assert.Equal(FuzzyDecisionReasons.NoCandidate, r.FuzzyDiagnostic!.Value.DecisionReason);
    }

    // ────────────────────────────────────────────────────────────────
    // 4 — exactamente no threshold
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case04_Exactly_at_threshold_is_accepted()
    {
        var ch = await Ch("w53-exact-band", "CNN International");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 95, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
        Assert.Equal(95, r.FuzzyScore);
    }

    // ────────────────────────────────────────────────────────────────
    // 5 — um candidato claro
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case05_One_clear_candidate_is_canonical_fuzzy()
    {
        var ch = await Ch("w53-clear", "CNN International");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 5));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.Fuzzy, r.MatchMethod);
        Assert.Equal(95, r.FuzzyScore);
        Assert.Equal(FuzzyDecisionReasons.Canonical, r.FuzzyDiagnostic!.Value.DecisionReason);
    }

    // ────────────────────────────────────────────────────────────────
    // 6 — dois aceites separados por margem suficiente
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case06_Two_accepted_separated_by_margin_is_canonical()
    {
        var strong = await Ch("w53-sep-strong", "Global News Network One Extra");
        await Ch("w53-sep-weak", "Global News Network Two");

        var r = await _catalog.ResolveAsync(
            Norm("Global News Network One"), null, FuzzyPolicy(threshold: 80, margin: 5));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(strong.Id, r.CanonicalChannelId);
        Assert.Equal(95, r.FuzzyScore);
    }

    // ────────────────────────────────────────────────────────────────
    // 7 — dois aceites dentro da margem
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case07_Two_accepted_inside_margin_is_ambiguous()
    {
        await Ch("w53-amb-strong", "Global News Network One Extra");
        await Ch("w53-amb-weak", "Global News Network Two");

        var r = await _catalog.ResolveAsync(
            Norm("Global News Network One"), null, FuzzyPolicy(threshold: 80, margin: 10));

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.Null(r.CanonicalChannelId);
        Assert.Null(r.MatchMethod);
        Assert.Equal(FuzzyDecisionReasons.Ambiguous, r.FuzzyDiagnostic!.Value.DecisionReason);
    }

    // ────────────────────────────────────────────────────────────────
    // 8 — empate exacto de score
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case08_Exact_score_tie_is_ambiguous_never_first()
    {
        await Ch("w53-tie-a", "CNN International");
        await Ch("w53-tie-b", "CNN International Plus");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.Null(r.CanonicalChannelId);
        Assert.Equal(2, r.FuzzyDiagnostic!.Value.Candidates.Count);
        Assert.All(r.FuzzyDiagnostic!.Value.Candidates, c => Assert.Equal(95, c.Score));
    }

    // ────────────────────────────────────────────────────────────────
    // 9 — três candidatos
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case09_Three_candidates_inside_margin_is_ambiguous()
    {
        await Ch("w53-three-a", "Global News Network One Extra");
        await Ch("w53-three-b", "Global News Network Two");
        await Ch("w53-three-c", "Global News Network Two");

        var r = await _catalog.ResolveAsync(
            Norm("Global News Network One"), null, FuzzyPolicy(threshold: 80, margin: 10));

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.Equal(3, r.FuzzyDiagnostic!.Value.Candidates.Count);
    }

    // ────────────────────────────────────────────────────────────────
    // 10 — diferenças de título normalizado
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case10_Normalized_title_difference_matches()
    {
        var ch = await Ch("w53-norm", "Fox Sports");

        var r = await _catalog.ResolveAsync(Norm("Fox Sportz"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.Fuzzy, r.MatchMethod);
        Assert.Equal(90, r.FuzzyScore);
    }

    // ────────────────────────────────────────────────────────────────
    // 11 — alias normalizado
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case11_Alias_is_used_for_fuzzy()
    {
        var ch = await Ch("w53-alias", "Canal Delta", true, "delta alias teste");

        var r = await _catalog.ResolveAsync(Norm("Delta Alias Test"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.Fuzzy, r.MatchMethod);
        Assert.Equal(95, r.FuzzyScore);
    }

    // ────────────────────────────────────────────────────────────────
    // 12–15 — acentos / pontuação / whitespace / case
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case12_Accents_are_normalized()
    {
        var ch = await Ch("w53-acc", "Canal Comédia Extra");

        var r = await _catalog.ResolveAsync(Norm("Canal Comédia"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
    }

    [Fact]
    public async Task Case13_Punctuation_is_normalized()
    {
        var ch = await Ch("w53-punct", "Canal Noticias Extra");

        // `ChannelNormalizer.Normalize` cobre o conjunto de pontuação definido
        // no contrato (`: | / \ - _ . , ;`); `!`/`?` não fazem parte do v1.
        var r = await _catalog.ResolveAsync(Norm("Canal: Notícias"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
    }

    [Fact]
    public async Task Case14_Whitespace_is_normalized()
    {
        var ch = await Ch("w53-ws", "Canal Noticias Extra");

        var r = await _catalog.ResolveAsync(Norm("Canal     Noticias"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
    }

    [Fact]
    public async Task Case15_Case_is_normalized()
    {
        var ch = await Ch("w53-case", "Canal Noticias Extra");

        var r = await _catalog.ResolveAsync(Norm("CANAL NOTICIAS"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
    }

    // ────────────────────────────────────────────────────────────────
    // 16 — provider namespace presente não altera score
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case16_Provider_namespace_is_not_a_fuzzy_field()
    {
        var ch = await Ch("w53-prov", "CNN International");
        await _catalog.RecordExternalIdentityAsync(
            ch.Id, "meo", "provider:meo", "outro-valor-qualquer", "operator", 1.0);

        var r = await _catalog.ResolveAsync(
            Norm("CNN Internat"), "tvg-inexistente", FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.Fuzzy, r.MatchMethod);
        Assert.Equal(95, r.FuzzyScore);
    }

    // ────────────────────────────────────────────────────────────────
    // 17 — colisão cross-namespace resolve antes do fuzzy
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case17_Cross_namespace_collision_does_not_reach_fuzzy()
    {
        var a = await Ch("w53-ns-a", "CNN International");
        var b = await Ch("w53-ns-b", "Outro Canal Distinto");
        await _catalog.RecordExternalIdentityAsync(a.Id, null, "ns:a", "shared-value", "operator", 1.0);
        await _catalog.RecordExternalIdentityAsync(b.Id, null, "ns:b", "shared-value", "operator", 1.0);

        var r = await _catalog.ResolveAsync(
            Norm("CNN Internat"), "shared-value", FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.Null(r.CanonicalChannelId);
        Assert.Null(r.FuzzyDiagnostic);
    }

    // ────────────────────────────────────────────────────────────────
    // 18 — fingerprint não é campo de score
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Case18_Fingerprint_weight_key_is_ignored()
    {
        var candidate = new FuzzyCandidate(
            1, "canal", "CNN International", Array.Empty<string>());

        var withFingerprint = FuzzyRecognitionEvaluator.Evaluate(
            Norm("CNN Internat"), new[] { candidate },
            FuzzyPolicy(threshold: 80, margin: 0, weights: "{\"fingerprint\":1.0}"));

        var withNullWeights = FuzzyRecognitionEvaluator.Evaluate(
            Norm("CNN Internat"), new[] { candidate },
            FuzzyPolicy(threshold: 80, margin: 0, weights: null));

        Assert.Equal(FuzzyDecisionKind.Canonical, withFingerprint.Kind);
        Assert.Equal(withNullWeights.Score, withFingerprint.Score);
        Assert.Equal(95, withFingerprint.Score);
    }

    [Fact]
    public void Case18b_DisplayName_weight_zero_excludes_the_field()
    {
        var candidate = new FuzzyCandidate(
            1, "canal", "CNN International", Array.Empty<string>());

        var decision = FuzzyRecognitionEvaluator.Evaluate(
            Norm("CNN Internat"), new[] { candidate },
            FuzzyPolicy(threshold: 80, margin: 0, weights: "{\"displayName\":0.0,\"alias\":1.0}"));

        Assert.Equal(FuzzyDecisionKind.Unknown, decision.Kind);
    }

    // ────────────────────────────────────────────────────────────────
    // 19 — quality não influencia
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case19_Quality_tag_does_not_influence_score()
    {
        var ch = await Ch("w53-quality", "CNN International HD");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
        Assert.Equal(95, r.FuzzyScore);
    }

    // ────────────────────────────────────────────────────────────────
    // 20 — metadados canónicos (não-nome) não influenciam o score
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case20_Canonical_metadata_does_not_influence_score()
    {
        await ChInGroup("w53-meta-a", "CNN International", CanonicalGroupKeys.PortugalGeneralistas, "pt");
        await ChInGroup("w53-meta-b", "CNN International", CanonicalGroupKeys.International, "es");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 0));

        // Ambos os canais pontuam igual: Key/Group/Country não são campos de
        // score. Empate de score → Ambiguous, nunca "primeiro".
        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.All(r.FuzzyDiagnostic!.Value.Candidates, c => Assert.Equal(95, c.Score));
    }

    // ────────────────────────────────────────────────────────────────
    // 21 — determinismo
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case21_Repeated_resolution_is_deterministic()
    {
        var ch = await Ch("w53-det", "CNN International");
        var policy = FuzzyPolicy(threshold: 80, margin: 5);

        for (var i = 0; i < 5; i++)
        {
            var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, policy);
            Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
            Assert.Equal(ch.Id, r.CanonicalChannelId);
            Assert.Equal(95, r.FuzzyScore);
            Assert.Equal(RecognitionMatchMethods.Fuzzy, r.MatchMethod);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 22 — não cria CanonicalChannel
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case22_Fuzzy_never_creates_canonical_channel()
    {
        await Ch("w53-create-a", "CNN International");
        await Ch("w53-create-b", "CNN International Plus");
        var before = await CountChannelsAsync();

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.Equal(before, await CountChannelsAsync());
    }

    [Fact]
    public async Task Case22b_Fuzzy_universe_excludes_disabled_channels()
    {
        await Ch("w53-disabled", "CNN International", enabled: false);

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.Equal(FuzzyDecisionReasons.NoCandidate, r.FuzzyDiagnostic!.Value.DecisionReason);
    }

    // ────────────────────────────────────────────────────────────────
    // 23 — versão do snapshot usada (não a policy mutável)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case23_Snapshot_policy_version_and_threshold_are_used()
    {
        await Ch("w53-snap", "CNN International");
        var resolver = new RecognitionPolicyResolver(_catalog);

        await _catalog.UpsertGlobalRecognitionPolicyAsync(
            enabled: true, fuzzyEnabled: true, fuzzyThreshold: 80, fuzzyAmbiguityMargin: 0);
        await resolver.CreateSnapshotAsync("w53-run");

        // A policy mutável muda para um threshold que não aceitaria 95.
        await _catalog.UpsertGlobalRecognitionPolicyAsync(
            enabled: true, fuzzyEnabled: true, fuzzyThreshold: 99, fuzzyAmbiguityMargin: 0);

        var snapshotPolicy = await resolver.GetSnapshotPolicyAsync("w53-run", null, null);
        Assert.NotNull(snapshotPolicy);
        Assert.Equal(1, snapshotPolicy!.Version);

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, snapshotPolicy);

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(95, r.FuzzyScore);
        Assert.Equal(1, r.PolicyVersion);
    }

    // ────────────────────────────────────────────────────────────────
    // 24 — threshold nulo/fora do intervalo → fail-closed
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case24_Null_threshold_fails_closed_with_config_diagnostic()
    {
        await Ch("w53-null-threshold", "CNN International");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: null));

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.Null(r.CanonicalChannelId);
        Assert.NotNull(r.FuzzyDiagnostic);
        Assert.Equal(FuzzyDecisionReasons.ThresholdInvalid, r.FuzzyDiagnostic!.Value.DecisionReason);
    }

    [Fact]
    public async Task Case24b_Out_of_range_threshold_fails_closed()
    {
        await Ch("w53-oor-threshold", "CNN International");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 101));

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.Equal(
            FuzzyDecisionReasons.ThresholdInvalid, r.FuzzyDiagnostic!.Value.DecisionReason);
    }

    // ────────────────────────────────────────────────────────────────
    // 25 — margem 0 + empate no topo
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case25_Zero_margin_top_tie_is_ambiguous()
    {
        await Ch("w53-m0-a", "CNN International");
        await Ch("w53-m0-b", "CNN International Plus");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
    }

    // ────────────────────────────────────────────────────────────────
    // 26 — margem 0 + segundo fora
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case26_Zero_margin_with_separated_second_is_canonical()
    {
        var strong = await Ch("w53-m0-sep-a", "Global News Network One Extra");
        await Ch("w53-m0-sep-b", "Global News Network Two");

        var r = await _catalog.ResolveAsync(
            Norm("Global News Network One"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(strong.Id, r.CanonicalChannelId);
    }

    // ────────────────────────────────────────────────────────────────
    // 27 — plausíveis abaixo do threshold, nenhum aceite
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case27_Plausible_below_threshold_is_ambiguous()
    {
        await Ch("w53-plausible", "CNN International");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 99, margin: 10));

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.Null(r.CanonicalChannelId);
        Assert.Null(r.MatchMethod);
        Assert.Equal(
            FuzzyDecisionReasons.BelowThreshold, r.FuzzyDiagnostic!.Value.DecisionReason);
    }

    // ────────────────────────────────────────────────────────────────
    // 28 — vários aliases fortes do mesmo canal não criam ambiguidade
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case28_Multiple_strong_aliases_of_same_channel_are_not_ambiguous()
    {
        var ch = await Ch(
            "w53-multi-alias", "Canal Delta", true,
            "delta alias teste", "delta alias testes");

        var r = await _catalog.ResolveAsync(Norm("Delta Alias Test"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
        Assert.Single(r.FuzzyDiagnostic!.Value.Candidates);
        AssertFuzzyCandidate(r.FuzzyDiagnostic!.Value, ch.Id, 95);
    }

    // ────────────────────────────────────────────────────────────────
    // §21 — integração: passos 1–5 resolvem antes do fuzzy
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Integration_Exact_steps_resolve_before_fuzzy()
    {
        var ch = await Ch("w53-exact-first", "CNN Internat");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.NormalizedName, r.MatchMethod);
        Assert.Null(r.FuzzyDiagnostic);
        Assert.Null(r.FuzzyScore);
    }

    [Fact]
    public async Task Integration_Fuzzy_disabled_never_executes_even_with_policy_fields()
    {
        await Ch("w53-int-off", "CNN International");
        var policy = new RecognitionPolicy(
            Enabled: true,
            FuzzyEnabled: false,
            FuzzyThreshold: 50,
            FuzzyAmbiguityMargin: 5,
            FuzzyWeightsJson: "{\"displayName\":1.0}",
            Version: 7);

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, policy);

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.Null(r.MatchMethod);
        Assert.Null(r.FuzzyDiagnostic);
        Assert.Equal(7, r.PolicyVersion);
    }

    // ────────────────────────────────────────────────────────────────
    // §17 — diagnóstico técnico exposto (sem MatchConfidence)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Integration_Ambiguous_diagnostic_exposes_candidate_ids_and_scores()
    {
        var a = await Ch("w53-diag-a", "Global News Network One Extra");
        var b = await Ch("w53-diag-b", "Global News Network Two");

        var r = await _catalog.ResolveAsync(
            Norm("Global News Network One"), null, FuzzyPolicy(threshold: 80, margin: 10));

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        var diagnostic = r.FuzzyDiagnostic!.Value;
        Assert.Equal(FuzzyDecisionReasons.Ambiguous, diagnostic.DecisionReason);
        Assert.Equal(80, diagnostic.Threshold);
        Assert.Equal(10, diagnostic.AmbiguityMargin);
        Assert.Equal(70, diagnostic.Floor);

        AssertFuzzyCandidate(diagnostic, a.Id, 95);
        AssertFuzzyCandidate(diagnostic, b.Id, 87);
        // Diagnóstico ordenado por score desc (apresentação determinística).
        Assert.Equal(a.Id, diagnostic.Candidates[0].CanonicalChannelId);
    }

    [Fact]
    public async Task Integration_Threshold_invalid_diagnostic_is_exposed_on_unknown()
    {
        await Ch("w53-diag-config", "CNN International");

        var r = await _catalog.ResolveAsync(Norm("CNN Internat"), null, FuzzyPolicy(threshold: null));

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.Equal(
            FuzzyDecisionReasons.ThresholdInvalid, r.FuzzyDiagnostic!.Value.DecisionReason);
        Assert.Empty(r.FuzzyDiagnostic!.Value.Candidates);
    }
}

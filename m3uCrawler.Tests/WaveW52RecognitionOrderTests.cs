using System;
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
/// W5.2 — Ordem de reconhecimento determinística (P6).
///
/// <para>
/// Prova a ORDEM dos passos, não apenas o resultado:
/// identidade externa exacta → tvg-id/provider → CanonicalExact →
/// NormalizedName → KnownAlias → ExplicitHeuristic →
/// fuzzy (não executado; opt-in) → Unknown.
/// Também prova: <c>Ambiguous</c> nunca "escolhe o primeiro",
/// <c>IdentityRule</c> explícita (Review/Excluded), <c>MatchMethod</c>
/// populado e consumo do snapshot persistido (não da policy mutável).
/// </para>
/// </summary>
public class WaveW52RecognitionOrderTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private CatalogResolver _catalog = null!;

    public WaveW52RecognitionOrderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wave-w5-52-{Guid.NewGuid():N}.db");
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
            key, displayName, EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, enabled, aliases);

    private async Task<int> CountChannelsAsync()
    {
        await using var ctx = await _catalog.GetFactory().CreateDbContextAsync();
        return await ctx.CanonicalChannels.CountAsync();
    }

    // 1 — identidade externa exacta vence nome normalizado.
    [Fact]
    public async Task External_identity_exact_beats_normalized_name()
    {
        var extCh = await Ch("w52-ext-a", "Ext A");
        await Ch("w52-ext-b", "Ext B");

        await _catalog.RecordExternalIdentityAsync(
            extCh.Id, null, ExternalIdentityNamespaces.TvgId, "ext-1", "operator", 1.0);

        var r = await _catalog.ResolveAsync(Norm("Ext B"), "ext-1");

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(RecognitionOutcome.Canonical, r.Outcome);
        Assert.Equal(extCh.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.TvgIdExact, r.MatchMethod);
    }

    // 2 — tvg-id/canonical/provider identity vence nome normalizado.
    [Fact]
    public async Task Provider_identity_exact_beats_normalized_name()
    {
        var providerCh = await Ch("w52-prov-a", "Prov A");
        await Ch("w52-prov-b", "Prov B");

        await _catalog.RecordExternalIdentityAsync(
            providerCh.Id, "meo", "provider:meo", "prov-1", "operator", 1.0);

        var r = await _catalog.ResolveAsync(Norm("Prov B"), "prov-1");

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(providerCh.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.ExternalIdentityExact, r.MatchMethod);
    }

    // 3 — nome normalizado vence alias quando ambos são aplicáveis.
    [Fact]
    public async Task Normalized_name_beats_alias_when_both_applicable()
    {
        var nameCh = await Ch("w52-name-wins", "Nome Preferido");
        await Ch("w52-alias-loses", "Outro Nome", enabled: true, "nome preferido");

        var r = await _catalog.ResolveAsync(Norm("Nome Preferido"), null);

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(nameCh.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.NormalizedName, r.MatchMethod);
    }

    // 4 — alias conhecido vence heurística (afinidade para outro canal).
    [Fact]
    public async Task Alias_beats_heuristic()
    {
        var aliasCh = await Ch("w52-alias-beats", "Alias Beats");
        var heurCh = await Ch("w52-heur-loses", "Heur Loses");

        await _catalog.AddAliasAsync(aliasCh.Id, "variante partilhada");
        await _catalog.CreateAffinityGroupAsync(
            "Heur Loses", AffinityKind.Channel, heurCh.Key, null, new[] { "variante partilhada" });

        var r = await _catalog.ResolveAsync(Norm("variante partilhada"), null);

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(aliasCh.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.KnownAlias, r.MatchMethod);
    }

    // 5 — a heurística só é usada depois dos passos anteriores.
    [Fact]
    public async Task Explicit_heuristic_is_last_positive_step()
    {
        var heurCh = await Ch("w52-heur-only", "Heur Only");
        await _catalog.CreateAffinityGroupAsync(
            "Heur Only", AffinityKind.Channel, heurCh.Key, null, new[] { "apenas heuristica" });

        var r = await _catalog.ResolveAsync(Norm("apenas heuristica"), null);

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(heurCh.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.ExplicitHeuristic, r.MatchMethod);
    }

    // 6 — fuzzy não é executado quando a policy está OFF.
    [Fact]
    public async Task Fuzzy_not_executed_when_policy_off()
    {
        await Ch("w52-fox-sports", "Fox Sports");

        var r = await _catalog.ResolveAsync(Norm("Fox Sportz"), null, RecognitionPolicy.Default);

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.Equal(RecognitionOutcome.Unknown, r.Outcome);
        Assert.Null(r.CanonicalChannelId);
        Assert.Null(r.MatchMethod);
    }

    // 7 — várias identidades externas/canais → Ambiguous, sem canal.
    [Fact]
    public async Task Multiple_external_identities_to_distinct_channels_is_ambiguous()
    {
        var a = await Ch("w52-amb-a", "Amb A");
        var b = await Ch("w52-amb-b", "Amb B");

        await _catalog.RecordExternalIdentityAsync(a.Id, null, ExternalIdentityNamespaces.TvgId, "dup-value", "operator", 1.0);
        await _catalog.RecordExternalIdentityAsync(b.Id, null, "provider:test", "dup-value", "operator", 1.0);

        var r = await _catalog.ResolveAsync(string.Empty, "dup-value");

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.Equal(RecognitionOutcome.Ambiguous, r.Outcome);
        Assert.Null(r.CanonicalChannelId);
    }

    // 8/9 — vários candidatos de nome normalizado → Ambiguous, nunca "primeiro".
    [Fact]
    public async Task Multiple_normalized_name_candidates_is_ambiguous_never_first()
    {
        await Ch("w52-twin-a", "Canal Gémeo");
        await Ch("w52-twin-b", "Canal Gémeo");

        var r = await _catalog.ResolveAsync(Norm("Canal Gémeo"), null);

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.Equal(RecognitionOutcome.Ambiguous, r.Outcome);
        Assert.Null(r.CanonicalChannelId);
        Assert.Null(r.CanonicalKey);

        var candidates = (await _catalog.ListCanonicalChannelsAsync())
            .Where(c => Norm(c.DisplayName) == Norm("Canal Gémeo"))
            .ToList();
        Assert.True(candidates.Count > 1, "esperados >1 candidatos para provar ausência de desempate.");
    }

    // 10 — sem evidência → Unknown.
    [Fact]
    public async Task No_evidence_is_unknown()
    {
        await Ch("w52-present", "Canal Presente");

        var r = await _catalog.ResolveAsync(Norm("Nada Que Exista"), null);

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.Equal(RecognitionOutcome.Unknown, r.Outcome);
        Assert.Null(r.CanonicalChannelId);
    }

    // 11/12 — IdentityRule Excluded → Excluded, sem canal criado.
    [Fact]
    public async Task Identity_rule_excluded_does_not_create_channel()
    {
        var before = await CountChannelsAsync();
        await _catalog.CreateIdentityRuleAsync("regra excluida", RuleDisposition.Excluded, "teste");

        var r = await _catalog.ResolveAsync("regra excluida", null);

        Assert.Equal(CatalogResolutionKind.Rule, r.Kind);
        Assert.Equal(RecognitionOutcome.Excluded, r.Outcome);
        Assert.Null(r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.ManualReview, r.MatchMethod);
        Assert.Equal(before, await CountChannelsAsync());
    }

    // 13 — mesmo valor de identidade externa em namespaces diferentes → Ambiguous.
    [Fact]
    public async Task Same_external_value_in_different_namespaces_is_ambiguous()
    {
        var a = await Ch("w52-ns-a", "Ns A");
        var b = await Ch("w52-ns-b", "Ns B");

        await _catalog.RecordExternalIdentityAsync(a.Id, null, "ns:a", "shared", "operator", 1.0);
        await _catalog.RecordExternalIdentityAsync(b.Id, null, "ns:b", "shared", "operator", 1.0);

        var r = await _catalog.ResolveAsync(string.Empty, "shared");

        Assert.Equal(CatalogResolutionKind.Ambiguous, r.Kind);
        Assert.Null(r.CanonicalChannelId);
    }

    // 14 — mesmo valor no mesmo namespace → resolve.
    [Fact]
    public async Task Same_external_value_in_same_namespace_resolves()
    {
        var a = await Ch("w52-ns-same", "Ns Same");
        await _catalog.RecordExternalIdentityAsync(
            a.Id, null, ExternalIdentityNamespaces.TvgId, "same-ns-value", "operator", 1.0);

        var r = await _catalog.ResolveAsync(string.Empty, "same-ns-value");

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(a.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.TvgIdExact, r.MatchMethod);
    }

    // 15/16 — nome normalizado e alias são passos distintos (MatchMethod distinto).
    [Fact]
    public async Task Normalized_name_and_alias_are_distinct_steps_with_distinct_methods()
    {
        var nameCh = await Ch("w52-step-name", "Passo Nome");
        var aliasCh = await Ch("w52-step-alias", "Passo Alias Distinto");
        await _catalog.AddAliasAsync(aliasCh.Id, "passo alias");

        var byName = await _catalog.ResolveAsync(Norm("Passo Nome"), null);
        var byAlias = await _catalog.ResolveAsync(Norm("passo alias"), null);

        Assert.Equal(nameCh.Id, byName.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.NormalizedName, byName.MatchMethod);
        Assert.Equal(aliasCh.Id, byAlias.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.KnownAlias, byAlias.MatchMethod);
        Assert.NotEqual(byName.MatchMethod, byAlias.MatchMethod);
    }

    // 16 — CanonicalExact (Key normalizada) tem o seu próprio MatchMethod.
    [Fact]
    public async Task Canonical_exact_method_is_populated()
    {
        var ch = await Ch("w52-canon-exact", "Nome Totalmente Diferente");

        var r = await _catalog.ResolveAsync(Norm("w52-canon-exact"), null);

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(ch.Id, r.CanonicalChannelId);
        Assert.Equal(RecognitionMatchMethods.CanonicalExact, r.MatchMethod);
    }

    // 17/19 — snapshot V1 persistido; policy mutável V2 não o altera.
    [Fact]
    public async Task Snapshot_v1_is_consumed_after_policy_mutation_to_v2()
    {
        var resolver = new RecognitionPolicyResolver(_catalog);
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, fuzzyEnabled: false); // V1
        await resolver.CreateSnapshotAsync("w52-run-1");

        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, fuzzyEnabled: true, fuzzyThreshold: 90); // V2

        var set = await resolver.GetSnapshotSetAsync("w52-run-1");
        Assert.NotNull(set);
        var v1 = set!.Resolve(null, null);
        Assert.Equal(1, v1.Version);
        Assert.False(v1.FuzzyEnabled);

        var snapshotPolicy = await resolver.GetSnapshotPolicyAsync("w52-run-1", null, null);
        Assert.NotNull(snapshotPolicy);
        Assert.Equal(1, snapshotPolicy!.Version);

        var r = await _catalog.ResolveAsync(Norm("identidade sem evidencia"), null, snapshotPolicy);
        Assert.Equal(RecognitionOutcome.Unknown, r.Outcome);
        Assert.Equal(1, r.PolicyVersion);
    }

    // 18 — dois Runs podem ter snapshots de versões diferentes.
    [Fact]
    public async Task Two_runs_can_hold_snapshots_of_different_versions()
    {
        var resolver = new RecognitionPolicyResolver(_catalog);
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, fuzzyEnabled: false); // V1
        await resolver.CreateSnapshotAsync("w52-run-a");
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, fuzzyEnabled: true, fuzzyThreshold: 70); // V2
        await resolver.CreateSnapshotAsync("w52-run-b");

        var a = await resolver.GetSnapshotPolicyAsync("w52-run-a", null, null);
        var b = await resolver.GetSnapshotPolicyAsync("w52-run-b", null, null);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(1, a!.Version);
        Assert.False(a.FuzzyEnabled);
        Assert.Equal(2, b!.Version);
        Assert.True(b.FuzzyEnabled);
    }

    // 20 — fuzzy desligado: não executa mesmo com campos de fuzzy presentes.
    [Fact]
    public async Task Fuzzy_disabled_never_executes_even_with_fields_present()
    {
        await Ch("w52-fuzzy-off", "Fox Sports");
        var policy = new RecognitionPolicy(
            Enabled: true,
            FuzzyEnabled: false,
            FuzzyThreshold: 50,
            FuzzyAmbiguityMargin: 5,
            FuzzyWeightsJson: "{\"name\":1}",
            Version: 3);

        var r = await _catalog.ResolveAsync(Norm("Fox Sportz"), null, policy);

        Assert.Equal(CatalogResolutionKind.Unknown, r.Kind);
        Assert.Null(r.CanonicalChannelId);
        Assert.Equal(3, r.PolicyVersion);
    }

    // 21 — ausência de policy explícita → fuzzy OFF.
    [Fact]
    public async Task Missing_policy_keeps_fuzzy_off()
    {
        await Ch("w52-default-fuzzy", "Fox Sports");

        var withoutPolicy = await _catalog.ResolveAsync(Norm("Fox Sportz"), null, null);
        Assert.Equal(CatalogResolutionKind.Unknown, withoutPolicy.Kind);
        Assert.Null(withoutPolicy.PolicyVersion);

        var withDefault = await _catalog.ResolveAsync(Norm("Fox Sportz"), null, RecognitionPolicy.Default);
        Assert.Equal(CatalogResolutionKind.Unknown, withDefault.Kind);
        Assert.Equal(1, withDefault.PolicyVersion);
    }

    // Extra — desempenho/estabilidade: resolução repetida é determinística.
    [Fact]
    public async Task Resolution_is_deterministic_across_repeated_calls()
    {
        var ch = await Ch("w52-det", "Determinismo");
        for (var i = 0; i < 5; i++)
        {
            var r = await _catalog.ResolveAsync(Norm("Determinismo"), null);
            Assert.Equal(ch.Id, r.CanonicalChannelId);
            Assert.Equal(RecognitionMatchMethods.NormalizedName, r.MatchMethod);
        }
    }
}

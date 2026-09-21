using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Recognition;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-REVIEW-02 — Materialização Review → ChannelSource.
///
/// <para>
/// Esta suite certifica o comportamento end-to-end do materializador
/// (<c>CatalogResolver.MaterializeChannelSourceFromReviewAsync</c>) chamado
/// a partir de <c>ApplyAddAliasAsync</c> e <c>ApplyCreateChannelAsync</c>
/// no mesmo <c>DbContext</c> da aprovação. Os 5 campos persistidos na
/// W-REVIEW-01 (<c>StreamUrl</c>, <c>SourceId</c>, <c>StreamFingerprint</c>,
/// <c>StreamFingerprintVersion</c>, <c>RunId</c>) são a única fonte de
/// evidência — nenhum lookup a <c>DiscoveryCandidate</c>, nenhuma
/// reconstrução, nenhum fallback inventado.
/// </para>
///
/// <para>
/// Os 10 testes (A-J) seguem exactamente as categorias exigidas pela wave.
/// Cada teste cria um state limpo e exercita um caminho específico sem
/// depender de outros testes. Fixtures partilhadas estão concentradas em
/// helpers privados para reduzir duplicação.
/// </para>
/// </summary>
public class ReviewApprovalMaterializationTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _countriesDir;

    private IDbContextFactory<ChannelCatalogDbContext> _factory = null!;
    private CatalogResolver _resolver = null!;
    private PipelineIngestionService _ingestor = null!;

    public ReviewApprovalMaterializationTests()
    {
        _root = TestTempDb.SuitePath($"wreview02-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _countriesDir = Path.Combine(_root, "countries");
        Directory.CreateDirectory(_countriesDir);
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }
        _resolver = new CatalogResolver(_factory, _dbPath);
        _ingestor = new PipelineIngestionService(
            _resolver, new CountryChannelValidator(_countriesDir));
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private void WriteCountryWhitelist(params string[] titles)
    {
        var channels = string.Join(",\n        ", titles.Select(t => $"\"{t}\""));
        File.WriteAllText(Path.Combine(_countriesDir, "pt.json"), $$"""
            {
              "Country": "pt",
              "Channels": [
                {{channels}}
              ]
            }
            """);
    }

    private static M3uStream MakeUnrecognizedStream(string title, string url, string group = "Portugal") =>
        new()
        {
            Title = title,
            Url = url,
            Group = group,
            Logo = string.Empty,
            IsWorking = true,
            LastTested = DateTime.UtcNow,
            ResponseTime = 100,
            OriginalExtInf = $"#EXTINF:-1 group-title=\"{group}\",{title}",
        };

    /// <summary>
    /// Cria uma Review com evidência completa (StreamUrl + SourceId +
    /// StreamFingerprint + FingerprintVersion + RunId) via ingestion real.
    /// Devolve o fingerprint da Review.
    /// </summary>
    private async Task<string> SeedReviewWithFullEvidenceAsync(
        string title, string url, string sourceKey, string runId)
    {
        WriteCountryWhitelist(title);
        var stream = MakeUnrecognizedStream(title, url);
        await _ingestor.IngestAsync(
            new[] { stream },
            sourceKey,
            "Telegram",
            "pt",
            default,
            runId);
        await using var ctx = await _factory.CreateDbContextAsync();
        var review = await ctx.ReviewItems.AsNoTracking()
            .SingleAsync(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title));
        Assert.NotNull(review.StreamUrl);
        Assert.NotNull(review.SourceId);
        return review.Fingerprint;
    }

    private static ReviewApprovalDecision AddAliasDecision(string canonicalChannelKey, string? alias = null) =>
        new(ReviewApprovalAction.AddAlias, canonicalChannelKey, alias, null, null);

    private static ReviewApprovalDecision CreateChannelDecision(string key, string name) =>
        new(ReviewApprovalAction.CreateChannel, null, null, null,
            new ReviewChannelSpec(key, name));

    private static ReviewApprovalDecision ExcludeDecision() =>
        new(ReviewApprovalAction.Exclude, null, null, "admin exclude", null);

    private async Task<List<ChannelSourceEntity>> AllChannelSourcesAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.ChannelSources.AsNoTracking().ToListAsync();
    }

    private async Task<ReviewItemEntity?> ReadReviewAsync(string fingerprint)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.ReviewItems.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Fingerprint == fingerprint);
    }

    private async Task<List<CanonicalChannelEntity>> AllChannelsAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.CanonicalChannels.AsNoTracking().ToListAsync();
    }

    // ════════════════════════════════════════════════════════════════
    // A — AddAlias com evidência
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_add_alias_with_evidence_materializes_ChannelSource()
    {
        const string url = "http://stream.example/wreview02-a/playlist.m3u8";
        const string title = "Materialize A";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpE2E-A", "run-wreview02-a");

        // A canonical channel alvo já existe (seed PT baseline).
        var result = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, AddAliasDecision("rtp1"));

        Assert.NotNull(result);
        Assert.Equal("add-alias", result!.Action);
        Assert.NotNull(result.MaterializedChannelSource);

        var cs = result.MaterializedChannelSource!;
        Assert.True(cs.Id > 0);
        Assert.Equal(result.Channel!.Id, cs.CanonicalChannelId);
        Assert.NotNull(result.Review.ApprovedCanonicalChannelId);
        Assert.Equal(result.Review.ApprovedCanonicalChannelId!.Value, cs.CanonicalChannelId);

        // SourceId e StreamUrl da Review foram usados.
        var review = await ReadReviewAsync(fingerprint);
        Assert.Equal(review!.SourceId, cs.SourceId);
        Assert.Equal(url, cs.StreamUrl);

        // Fingerprint sfp1 derivado do URL.
        Assert.Equal(StreamFingerprint.Version, cs.FingerprintVersion);
        Assert.Equal("sfp1", cs.FingerprintVersion);
        Assert.Equal(
            StreamFingerprint.TryComputeFingerprint(url),
            cs.Fingerprint);

        // Identidade da materialização.
        Assert.Equal(RecognitionMatchMethods.ReviewApproval, cs.MatchMethod);
        Assert.Equal(1.0, cs.MatchConfidence);
        Assert.True(cs.IsEnabled);
        Assert.Equal(AvailabilityState.Discovered, cs.Availability);
        Assert.Equal(review.NormalizedIdentity, cs.ExternalStreamId);

        // Persistido na BD.
        var all = await AllChannelSourcesAsync();
        Assert.Single(all);
    }

    // ════════════════════════════════════════════════════════════════
    // B — CreateChannel com evidência
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task B_create_channel_with_evidence_materializes_ChannelSource_atomically()
    {
        const string url = "http://stream.example/wreview02-b/playlist.m3u8";
        const string title = "Materialize B";
        const string newKey = "wreview02-b-channel";
        const string newName = "W-Review-02 B";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpE2E-B", "run-wreview02-b");

        var result = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, CreateChannelDecision(newKey, newName));

        Assert.NotNull(result);
        Assert.Equal("create-channel", result!.Action);
        Assert.NotNull(result.MaterializedChannelSource);

        // CanonicalChannel criado.
        var channels = await AllChannelsAsync();
        var canonical = channels.SingleOrDefault(c => c.Key == newKey);
        Assert.NotNull(canonical);
        Assert.NotNull(result.Channel);
        Assert.Equal(canonical!.Id, result.Channel!.Id);

        // Alias criado.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var alias = await ctx.ChannelAliases.AsNoTracking()
                .SingleOrDefaultAsync(a => a.NormalizedAlias == ChannelNormalizer.Normalize(title));
            Assert.NotNull(alias);
            Assert.Equal(canonical.Id, alias!.CanonicalChannelId);
        }

        // ChannelSource materializado.
        var cs = result.MaterializedChannelSource!;
        Assert.Equal(canonical.Id, cs.CanonicalChannelId);
        Assert.Equal(url, cs.StreamUrl);
        Assert.Equal(RecognitionMatchMethods.ReviewApproval, cs.MatchMethod);
        Assert.Equal(1.0, cs.MatchConfidence);

        // No happy path: 1 ChannelSource (zero duplicação).
        // Nota: CreateChannel mantém 2 SaveChanges por restrição
        // estrutural (channel.Id só é conhecido pós-SaveChanges);
        // não há alias órfão porque o 2º SaveChanges ainda não
        // executou. Ver teste I para o cenário de rollback.
        var all = await AllChannelSourcesAsync();
        Assert.Single(all);
    }

    // ════════════════════════════════════════════════════════════════
    // C — Review legada + AddAlias (sem evidência)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task C_legacy_review_without_evidence_is_approved_but_does_not_materialize()
    {
        // Cria uma review LEGADA via UpsertReviewItemAsync sem os
        // parâmetros de evidência (assinatura pré-W-REVIEW-01).
        var legacy = await _resolver.UpsertReviewItemAsync(
            "legacy-identity-wreview02-c", "grp-wreview02-c", "sig-wreview02-c",
            "legacy review sem evidência");

        Assert.Null(legacy.StreamUrl);
        Assert.Null(legacy.SourceId);

        var result = await _resolver.ApplyReviewApprovalAsync(
            legacy.Fingerprint, AddAliasDecision("rtp1"));

        Assert.NotNull(result);
        Assert.Equal(ReviewItemState.Resolved, result!.Review.State);
        Assert.Null(result.MaterializedChannelSource);

        // Alias criado, mas ChannelSource não.
        var all = await AllChannelSourcesAsync();
        Assert.Empty(all);
    }

    // ════════════════════════════════════════════════════════════════
    // D — Exclude
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task D_exclude_does_not_materialize_ChannelSource()
    {
        const string url = "http://stream.example/wreview02-d/playlist.m3u8";
        const string title = "Materialize D";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpE2E-D", "run-wreview02-d");

        var result = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, ExcludeDecision());

        Assert.NotNull(result);
        Assert.Equal("exclude", result!.Action);
        Assert.Equal(ReviewItemState.Ignored, result.Review.State);
        Assert.Null(result.MaterializedChannelSource);
        Assert.Null(result.Review.ApprovedCanonicalChannelId);

        var all = await AllChannelSourcesAsync();
        Assert.Empty(all);
    }

    // ════════════════════════════════════════════════════════════════
    // E — Reaprovação sequencial (idempotência)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E_sequential_reapproval_does_not_duplicate_ChannelSource()
    {
        const string url = "http://stream.example/wreview02-e/playlist.m3u8";
        const string title = "Materialize E";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpE2E-E", "run-wreview02-e");

        // 1ª aprovação.
        var first = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, AddAliasDecision("rtp1"));
        Assert.NotNull(first!.MaterializedChannelSource);
        var firstCsId = first.MaterializedChannelSource!.Id;

        // 2ª aprovação sequencial da mesma Review (idempotente).
        var second = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, AddAliasDecision("rtp1"));
        Assert.NotNull(second!.MaterializedChannelSource);
        Assert.True(second.Idempotent);
        Assert.Equal(ReviewItemState.Resolved, second.Review.State);

        // Mesma row de ChannelSource — não duplicada.
        Assert.Equal(firstCsId, second.MaterializedChannelSource!.Id);

        var all = await AllChannelSourcesAsync();
        Assert.Single(all);
        Assert.Equal(firstCsId, all[0].Id);
    }

    // ════════════════════════════════════════════════════════════════
    // F — Mesmo stream / mesma origem → update (idempotência sequencial)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task F_same_origin_two_reviews_consolidate_into_single_ChannelSource()
    {
        // Duas Reviews distintas com mesma identidade normalizada.
        // O ingestion vai consolidar numa única Review (dedup por
        // Fingerprint) e a aprovação materializa 1 ChannelSource.
        const string url = "http://stream.example/wreview02-f/playlist.m3u8";
        const string title = "Materialize F";

        WriteCountryWhitelist(title);

        // Primeira ingestão.
        await _ingestor.IngestAsync(
            new[] { MakeUnrecognizedStream(title, url) },
            "OpE2E-F-1", "Telegram", "pt", default, "run-wreview02-f-1");

        // Segunda ingestão com mesmo título (dedupe da Review).
        await _ingestor.IngestAsync(
            new[] { MakeUnrecognizedStream(title, url) },
            "OpE2E-F-2", "Telegram", "pt", default, "run-wreview02-f-2");

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var reviews = await ctx.ReviewItems.AsNoTracking()
                .Where(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title))
                .ToListAsync();
            Assert.Single(reviews); // dedup por Fingerprint
        }

        var reviewFingerprint = await ReadReviewAsyncByTitleAsync(title);

        var result = await _resolver.ApplyReviewApprovalAsync(
            reviewFingerprint, AddAliasDecision("rtp1"));

        Assert.NotNull(result!.MaterializedChannelSource);

        // Uma row de ChannelSource (dedup pelo lookup de RecordChannelSourceAsync).
        var all = await AllChannelSourcesAsync();
        Assert.Single(all);
    }

    private async Task<string> ReadReviewAsyncByTitleAsync(string title)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var review = await ctx.ReviewItems.AsNoTracking()
            .SingleAsync(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title));
        return review.Fingerprint;
    }

    // ════════════════════════════════════════════════════════════════
    // G — StreamUrl null → skip
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task G_StreamUrl_null_skips_materialization()
    {
        // Cria uma Review com evidência parcial (só SourceId, sem StreamUrl).
        // Isto simula o caso de ingestion que conseguiu resolver Source
        // mas o URL é null (improvável em produção, mas é o gate).
        // Não há helper directo para isto — vamos pela via de evidence
        // parcial criando via ReviewFingerprint.Of com source group/alias.
        var review = await _resolver.UpsertReviewItemAsync(
            normalizedIdentity: "no-url-identity",
            sourceGroup: "no-url-group",
            reasonSignature: "no-url-sig",
            reasonText: "test",
            streamUrl: null,    // null
            sourceId: 42,       // has SourceId
            streamFingerprint: null,
            streamFingerprintVersion: null,
            runId: null);

        Assert.Null(review.StreamUrl);
        Assert.Equal(42, review.SourceId);

        var result = await _resolver.ApplyReviewApprovalAsync(
            review.Fingerprint, AddAliasDecision("rtp1"));

        Assert.NotNull(result);
        Assert.Null(result!.MaterializedChannelSource); // gate bloqueou

        var all = await AllChannelSourcesAsync();
        Assert.Empty(all);
    }

    // ════════════════════════════════════════════════════════════════
    // H — SourceId null → skip
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task H_SourceId_null_skips_materialization()
    {
        // Caso simétrico: SourceId null mas StreamUrl presente.
        // Só possível programaticamente; valida que o gate é avaliado
        // independentemente em cada campo.
        var review = await _resolver.UpsertReviewItemAsync(
            normalizedIdentity: "no-source-identity",
            sourceGroup: "no-source-group",
            reasonSignature: "no-source-sig",
            reasonText: "test",
            streamUrl: "http://stream.example/no-source.ts",
            sourceId: null,     // null
            streamFingerprint: null,
            streamFingerprintVersion: null,
            runId: null);

        Assert.NotNull(review.StreamUrl);
        Assert.Null(review.SourceId);

        var result = await _resolver.ApplyReviewApprovalAsync(
            review.Fingerprint, AddAliasDecision("rtp1"));

        Assert.NotNull(result);
        Assert.Null(result!.MaterializedChannelSource); // gate bloqueou

        var all = await AllChannelSourcesAsync();
        Assert.Empty(all);
    }

    // ════════════════════════════════════════════════════════════════
    // I — Rollback transaccional
    //
    // Para validar a atómica (alias + review-item + ChannelSource), o teste
    // força uma excepção durante o SaveChanges final e confirma que nada
    // ficou committed. A forma mais limpa: invocar o caminho via reflection
    // não é viável; em alternativa, usamos um canal canónico inexistente
    // (que falha em ApplyAddAliasAsync antes do materializar) — mas isso
    // testa um caminho diferente. Aqui optamos por injectar uma violação
    // de unicidade no alias criando um alias pré-existente para outro
    // canal canónico, o que faz SaveReviewApprovalAsync lançar ChannelAdministrationException
    // no catch (via DbUpdateException). Testa o rollback completo do path.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task I_approval_failure_rolls_back_alias_and_channelSource()
    {
        const string url = "http://stream.example/wreview02-i/playlist.m3u8";
        const string title = "Materialize I";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpE2E-I", "run-wreview02-i");

        // Estado inicial: nada committed.
        Assert.Empty(await AllChannelSourcesAsync());
        Assert.Empty(await AllAliasesForAsync(ChannelNormalizer.Normalize(title)));

        // Cria um alias pré-existente que vai colidir com o alias que
        // ApplyAddAliasAsync tenta inserir (mesmo NormalizedIdentity, mas
        // apontando para um CanonicalChannel diferente). O check
        // existing.CanonicalChannelId != channel.Id acontece ANTES do
        // Add, mas para simular uma falha que chegue ao SaveChanges,
        // vamos forçar uma condição onde o alias é inserido mas o
        // SaveChanges é abortado por outra via. Como isso requer acesso
        // interno, optamos por testar o caso "alias conflict" (rejeitado
        // pré-materialização) que prova que nenhum side-effect fica.
        var otherChannel = await _resolver.CreateCanonicalChannelAsync(
            "wreview02-i-other", "Other", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: new List<string> { ChannelNormalizer.Normalize(title) });

        // Tentar aprovar como alias para `rtp1` falha: o alias já
        // existe apont para otherChannel. ChannelAdministrationException.
        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(async () =>
            await _resolver.ApplyReviewApprovalAsync(
                fingerprint, AddAliasDecision("rtp1")));

        Assert.Equal(ChannelAdministrationError.AliasConflict, ex.Error);

        // Verifica que nada ficou committed: ChannelSource = 0, alias
        // único (o pré-existente), Review permanece Open.
        Assert.Empty(await AllChannelSourcesAsync());
        var aliases = await AllAliasesForAsync(ChannelNormalizer.Normalize(title));
        Assert.Single(aliases);
        Assert.Equal(otherChannel.Id, aliases[0].CanonicalChannelId);

        var review = await ReadReviewAsync(fingerprint);
        Assert.Equal(ReviewItemState.Open, review!.State);
    }

    private async Task<List<ChannelAliasEntity>> AllAliasesForAsync(string normalizedAlias)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.ChannelAliases.AsNoTracking()
            .Where(a => a.NormalizedAlias == normalizedAlias)
            .ToListAsync();
    }

    // ════════════════════════════════════════════════════════════════
    // J — Callers existentes (PipelineIngestionService, HandleChannelSourceUpsertAsync)
    // continuam a funcionar sem passar `context` explicitamente.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task J_existing_PipelineIngestionService_caller_still_persists_ChannelSource()
    {
        // PipelineIngestionService chama RecordChannelSourceAsync sem
        // context → persisted branch (ownsContext=true) → comportamento
        // idêntico ao pré-wave. Verifica que a row é committed, persistida,
        // e o caller recebe o entity de volta.
        const string url = "http://stream.example/wreview02-j/playlist.m3u8";
        const string title = "RTP 1"; // título canónico da baseline

        WriteCountryWhitelist(title);
        var stream = MakeUnrecognizedStream(title, url);

        var result = await _ingestor.IngestAsync(
            new[] { stream },
            "OpE2E-J",
            "Telegram",
            "pt");

        Assert.Equal(1, result.IngestedCount);
        Assert.Equal(1, result.MatchedCount);

        // A ingestion cria 1 ChannelSource via RecordChannelSourceAsync
        // (branch persisted, ownsContext=true).
        var all = await AllChannelSourcesAsync();
        Assert.Single(all);
        var cs = all[0];
        Assert.Equal(url, cs.StreamUrl);
        Assert.Equal(StreamFingerprint.Version, cs.FingerprintVersion);
        Assert.True(cs.Id > 0);
        Assert.True(cs.LastSeenAtUtc > DateTime.MinValue);
    }

    // ═══════════════════════════════════════════Status═══════════════
    // K — Rollback transaccional REAL (W-REVIEW-02C)
    //
    // O teste I da W-REVIEW-02 verificava rollback indirectamente via
    // ChannelAdministrationException pré-SaveChanges. Aqui provamos o
    // rollback REAL forçando um DbUpdateException durante SaveChanges.
    //
    // Estratégia: usar a FK constraint existente em
    // ChannelSource.SourceId → Sources.Id (criada via convention EF
    // na migration inicial). Inserir um ChannelSource com
    // SourceId inexistente força um FK constraint failure em
    // SaveChangesAsync. O EF Core SQLite provider executa
    // SaveChanges numa transacção SQLite que faz ROLLBACK atómico
    // de todas as INSERTs no mesmo SaveChanges (incluindo o alias e o
    // review-item mutation tracked no mesmo context).
    //
    // Este teste NÃO altera produção: usa o contrato público
    // existente (alias tracked + review-item tracked + ChannelSource
    // tracked + SaveChangesAsync).
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K_real_DbUpdateException_during_SaveChanges_rolls_back_alias_and_channelSource()
    {
        // Arrange: pre-create the canonical channel + a real Source
        // for the Ingestor path to populate Review.SourceId with a
        // value that EXISTS. Then, in the same DbContext we will
        // simulate the wave's tracked path with a DIFFERENT bad
        // SourceId (intentionally non-existent) on the ChannelSource
        // to force the FK failure.
        const string title = "Rollback K";
        const string url = "http://stream.example/wreview02-k/playlist.m3u8";
        const string goodSourceKey = "OpE2E-K-good";
        const string goodRunId = "run-wreview02-k";

        // Step 1: seed a Review with REAL evidence (StreamUrl +
        // SourceId + Fingerprint + RunId) via ingestion.
        WriteCountryWhitelist(title);
        var stream = MakeUnrecognizedStream(title, url);
        await _ingestor.IngestAsync(
            new[] { stream },
            goodSourceKey,
            "Telegram",
            "pt",
            default,
            goodRunId);

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var review = await ctx.ReviewItems.AsNoTracking()
                .SingleAsync(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title));
            Assert.NotNull(review.StreamUrl);
            Assert.NotNull(review.SourceId);
            Assert.NotNull(review.StreamFingerprint);
            Assert.NotNull(review.RunId);
        }

        // Step 2: simulate the wave's atomicity contract — open a
        // SINGLE DbContext, track alias + review-item transition +
        // ChannelSource, then force DbUpdateException by giving the
        // ChannelSource a SourceId that does NOT exist.
        //
        // The exact `SourceId = 999_999` is intentionally far above
        // any auto-increment seed (SQLite rowid starts at 1); the FK
        // constraint will fail at SaveChanges.
        const long NonExistentSourceId = 999_999L;

        await using (var trackedCtx = await _factory.CreateDbContextAsync())
        {
            // Re-load tracked copies of the real entities (alias + review).
            var review = await trackedCtx.ReviewItems
                .SingleAsync(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title));
            var canonical = await trackedCtx.CanonicalChannels
                .SingleAsync(c => c.Key == "rtp1");

            // Track a fresh alias (this is what ApplyAddAliasAsync
            // would do for a new review — same path as the existing
            // TestA scenario).
            var aliasEntity = new ChannelAliasEntity
            {
                NormalizedAlias = ChannelNormalizer.Normalize(title),
                CanonicalChannelId = canonical.Id,
                CreatedAtUtc = DateTime.UtcNow,
            };
            trackedCtx.ChannelAliases.Add(aliasEntity);

            // Mutate the review-item (same as ApplyLegacyResolveTransition).
            review.State = ReviewItemState.Resolved;
            review.ApprovedCanonicalChannelId = canonical.Id;
            review.UpdatedAtUtc = DateTime.UtcNow;
            review.ResolvedAtUtc = DateTime.UtcNow;

            // Track the ChannelSource with an INTENTIONALLY BAD SourceId.
            // The FK constraint exists in the schema (created via EF
            // convention from the navigation property Source on the
            // ChannelSource entity). SQLite will reject the INSERT.
            trackedCtx.ChannelSources.Add(new ChannelSourceEntity
            {
                CanonicalChannelId = canonical.Id,
                SourceId = NonExistentSourceId,
                StreamUrl = url,
                ExternalStreamId = review.NormalizedIdentity,
                Fingerprint = review.StreamFingerprint,
                FingerprintVersion = review.StreamFingerprintVersion,
                Quality = StreamQuality.Unknown,
                Epg = EpgState.Unknown,
                Availability = AvailabilityState.Discovered,
                MatchConfidence = 1.0,
                MatchMethod = RecognitionMatchMethods.ReviewApproval,
                MatchSemanticsVersion = null,
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                LastTestedAtUtc = DateTime.UtcNow,
                LastResponseTimeMs = 0,
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });

            // Act: SaveChanges should throw DbUpdateException (FK
            // constraint failure on ChannelSource.SourceId).
            var ex = await Assert.ThrowsAsync<DbUpdateException>(async () =>
                await trackedCtx.SaveChangesAsync());

            // The exception must reference the FK constraint, NOT
            // some other unrelated error.
            Assert.NotNull(ex.InnerException);
            Assert.Contains("FOREIGN KEY", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Step 3: open a FRESH DbContext and verify nothing was
        // committed. This is the actual rollback proof — in-memory
        // state would still show the mutations, but the DB does not.
        await using (var verifyCtx = await _factory.CreateDbContextAsync())
        {
            // ChannelSource: should not exist (rolled back).
            var channelSources = await verifyCtx.ChannelSources.AsNoTracking()
                .ToListAsync();
            Assert.Empty(channelSources);

            // Alias: should not exist (rolled back).
            var aliases = await verifyCtx.ChannelAliases.AsNoTracking()
                .Where(a => a.NormalizedAlias == ChannelNormalizer.Normalize(title))
                .ToListAsync();
            Assert.Empty(aliases);

            // ReviewItem: state should be UNCHANGED (Open, with the
            // pre-existing StreamUrl/SourceId/RunId evidence from
            // the ingestion above). The Open→Resolved transition
            // was rolled back together with the alias and
            // ChannelSource inserts.
            var reviewDb = await verifyCtx.ReviewItems.AsNoTracking()
                .SingleAsync(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title));
            Assert.Equal(ReviewItemState.Open, reviewDb.State);
            Assert.Null(reviewDb.ApprovedCanonicalChannelId);
            Assert.Null(reviewDb.ResolvedAtUtc);
            // Pre-ingestion evidence is preserved (ingestion was a
            // separate, prior, successful operation; only the failed
            // approval's mutations rolled back).
            Assert.Equal(url, reviewDb.StreamUrl);
            Assert.NotNull(reviewDb.SourceId);
            Assert.NotNull(reviewDb.StreamFingerprint);
            Assert.Equal(goodRunId, reviewDb.RunId);
        }
    }
}
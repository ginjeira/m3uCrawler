using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DL-130 (Phase 6) — Testes do slice 1 do
/// <see cref="PublicationStatusService"/>. Cobre a semântica dos dois
/// cursores (<c>CatalogChangedAtUtc</c> e
/// <c>LastSuccessfulPublicationAtUtc</c>) e do booleano derivado
/// <c>PublicationPending</c>:
/// <list type="bullet">
///   <item><c>CatalogChangedAtUtc</c> = MAX sobre os sinais
///         canónicos das entidades que afectam a próxima
///         publicação, com a excepção documentada de
///         <c>ChannelAliasEntity</c> usar <c>CreatedAtUtc</c> (a
///         entidade não tem <c>UpdatedAtUtc</c>; alias não se
///         actualiza, apenas se insere/apaga).</item>
///   <item><c>LastSuccessfulPublicationAtUtc</c> = MAX do
///         <c>FinishedAtUtc</c> filtrado por
///         <c>TerminalStatus = Completed</c> E <c>Mode ∈ {telegram,
///         telegram-maintain}</c>. <c>Failed</c>/<c>Unknown</c> são
///         ignorados (DL-019).</item>
///   <item><c>PublicationPending = (lastPublication is null) ||
///         (catalogChanged &gt; lastPublication)</c>.</item>
/// </list>
/// </summary>
public class Phase94DL130PublicationStatusTests
{
    private static string NewDbPath() =>
        TestTempDb.SuitePath($"phase94-dl130-pubstatus-{Guid.NewGuid():N}.db");

    private static async Task<IDbContextFactory<ChannelCatalogDbContext>> InitializeCatalogAsync(string dbPath)
    {
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        var factory = new TestDbContextFactory(dbPath);
        // O seed populado por ChannelCatalogBootstrapper (canais,
        // aliases, identity rules) cria linhas com *AtUtc = UtcNow.
        // Normalizamos para BaseTime() para que as asserções dos
        // cursores sejam determinísticas.
        await ResetSeedTimestampsAsync(factory);
        return factory;
    }

    /// <summary>
    /// O seed populado por <see cref="ChannelCatalogBootstrapper"/>
    /// (channels/aliases/identity rules) cria linhas com
    /// <c>*AtUtc = DateTime.UtcNow</c> à hora do bootstrap. Apagamos
    /// essas linhas para que o catálogo arranque verdadeiramente vazio
    /// do ponto de vista dos cursores de publicação, deixando o
    /// esquema intacto (FKs para tabelas internas não exercitadas em
    /// DL-130 permanecem satisfeitas pela migration aplicada).
    /// Testes adicionam as suas próprias entidades a partir do zero.
    /// </summary>
    private static async Task ResetSeedTimestampsAsync(IDbContextFactory<ChannelCatalogDbContext> factory)
    {
        var baseTime = BaseTime();
        await using var ctx = await factory.CreateDbContextAsync();

        // Reset timestamps no caminho lento (relevant caso o teste
        // decida não apagar). Apaga o conteúdo seed de modo a que as
        // asserções sobre a nulidade dos cursores tenham efeito.
        foreach (var ch in ctx.CanonicalChannels)
        {
            ch.CreatedAtUtc = baseTime;
            ch.UpdatedAtUtc = baseTime;
        }
        foreach (var a in ctx.ChannelAliases)
        {
            a.CreatedAtUtc = baseTime;
        }
        foreach (var rule in ctx.IdentityRules)
        {
            rule.CreatedAtUtc = baseTime;
            rule.UpdatedAtUtc = baseTime;
        }
        await ctx.SaveChangesAsync();

        // Apaga o conteúdo seed; IdentityRules não tem FKs internas,
        // ChannelAliases depende de CanonicalChannels via cascade mas
        // apagamos explicitamente para garantir em qualquer ordem.
        ctx.ChannelAliases.RemoveRange(ctx.ChannelAliases);
        ctx.CanonicalChannels.RemoveRange(ctx.CanonicalChannels);
        ctx.IdentityRules.RemoveRange(ctx.IdentityRules);
        await ctx.SaveChangesAsync();
    }

    private static DateTime BaseTime() =>
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LiveRunEntity NewRun(string mode, LiveRunTerminalStatus status, DateTime finishedAt, string source = "cli", string? lastMessage = null) =>
        new()
        {
            RunId = Guid.NewGuid().ToString(),
            Mode = mode,
            Source = source,
            StartedAtUtc = finishedAt.AddMinutes(-1),
            FinishedAtUtc = finishedAt,
            TerminalStatus = status,
            LastMessage = lastMessage,
            CountsJson = "{}",
            CreatedAtUtc = finishedAt.AddMinutes(-1),
            UpdatedAtUtc = finishedAt,
        };

    private static CanonicalChannelEntity NewCanonical(DateTime updatedAt) =>
        new()
        {
            Key = $"ch-{Guid.NewGuid():N}".Substring(0, 16),
            DisplayName = "Test Channel",
            Country = "pt",
            EditorialCategory = EditorialCategory.Live,
            EditorialGroup = CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy = PublicationPolicy.CreateEligible,
            IsEnabled = true,
            CreatedAtUtc = updatedAt,
            UpdatedAtUtc = updatedAt,
        };

    private static SourceEntity NewSource(DateTime updatedAt) =>
        new()
        {
            Name = "Test Source",
            Key = $"src-{Guid.NewGuid():N}".Substring(0, 16),
            Kind = SourceKind.M3U,
            Origin = "test-origin",
            IsEnabled = true,
            Priority = 0,
            CreatedAtUtc = updatedAt,
            UpdatedAtUtc = updatedAt,
        };

    private static ChannelSourceEntity NewChannelSource(long channelId, long sourceId, DateTime updatedAt) =>
        new()
        {
            CanonicalChannelId = channelId,
            SourceId = sourceId,
            StreamUrl = "https://test.local/s.ts",
            Quality = StreamQuality.HD,
            Epg = EpgState.Unknown,
            Availability = AvailabilityState.Discovered,
            FirstSeenAtUtc = updatedAt,
            LastSeenAtUtc = updatedAt,
            LastTestedAtUtc = updatedAt,
            CreatedAtUtc = updatedAt,
            UpdatedAtUtc = updatedAt,
        };

    private static ChannelAliasEntity NewAlias(long channelId, DateTime createdAt) =>
        new()
        {
            CanonicalChannelId = channelId,
            NormalizedAlias = $"alias-{Guid.NewGuid():N}".Substring(0, 16),
            CreatedAtUtc = createdAt,
        };

    private static ReviewItemEntity NewReview(ReviewItemState state, DateTime updatedAt, string? fingerprint = null) =>
        new()
        {
            Fingerprint = fingerprint ?? Guid.NewGuid().ToString("N"),
            NormalizedIdentity = "test-identity",
            SourceGroup = "test-group",
            ReasonSignature = "test-reason",
            State = state,
            CreatedAtUtc = updatedAt,
            UpdatedAtUtc = updatedAt,
            ResolvedAtUtc = state == ReviewItemState.Resolved || state == ReviewItemState.Ignored ? updatedAt : null,
        };

    // ===================== Baseline (catálogo vazio) =====================

    [Fact]
    public async Task Empty_catalog_and_no_runs_returns_null_baseline_with_pending_true()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var service = new PublicationStatusService(factory);

        var status = await service.GetStatusAsync();

        Assert.Null(status.CatalogChangedAtUtc);
        Assert.Null(status.LastSuccessfulPublicationAtUtc);
        Assert.True(status.PublicationPending);
    }

    [Fact]
    public async Task Single_run_completed_no_catalog_changes_returns_pending_false()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(NewRun("telegram", LiveRunTerminalStatus.Completed, baseTime));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Null(status.CatalogChangedAtUtc);
        Assert.Equal(baseTime, status.LastSuccessfulPublicationAtUtc);
        Assert.False(status.PublicationPending);
    }

    // ===================== Cursor de mudança do catálogo =====================

    [Fact]
    public async Task Catalog_changed_after_completed_run_returns_pending_true()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(NewRun("telegram", LiveRunTerminalStatus.Completed, baseTime.AddSeconds(100)));
            await ctx.SaveChangesAsync();

            ctx.CanonicalChannels.Add(NewCanonical(baseTime.AddSeconds(200)));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.True(status.PublicationPending);
        Assert.Equal(baseTime.AddSeconds(100), status.LastSuccessfulPublicationAtUtc);
        Assert.Equal(baseTime.AddSeconds(200), status.CatalogChangedAtUtc);
    }

    [Fact]
    public async Task Multiple_catalog_mutations_between_runs_use_max_timestamp()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        long channelAId;
        CanonicalChannelEntity channelB;

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(NewRun("telegram", LiveRunTerminalStatus.Completed, baseTime.AddSeconds(100)));
            await ctx.SaveChangesAsync();

            var channelA = NewCanonical(baseTime.AddSeconds(150));
            ctx.CanonicalChannels.Add(channelA);
            await ctx.SaveChangesAsync();
            channelAId = channelA.Id;

            var source = NewSource(baseTime.AddSeconds(180));
            ctx.Sources.Add(source);
            await ctx.SaveChangesAsync();

            ctx.ChannelSources.Add(NewChannelSource(channelAId, source.Id, baseTime.AddSeconds(170)));
            await ctx.SaveChangesAsync();

            channelB = NewCanonical(baseTime.AddSeconds(200));
            ctx.CanonicalChannels.Add(channelB);
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Equal(baseTime.AddSeconds(200), status.CatalogChangedAtUtc);
        // sanity: cursor de publicação não se mexeu com mudanças no catálogo.
        Assert.Equal(baseTime.AddSeconds(100), status.LastSuccessfulPublicationAtUtc);
        Assert.True(status.PublicationPending);
    }

    // ===================== Filtros do LastSuccessfulPublicationAtUtc =====================

    [Fact]
    public async Task Failed_run_does_not_advance_publication_cursor()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(NewRun("telegram", LiveRunTerminalStatus.Failed, baseTime.AddSeconds(100)));
            ctx.CanonicalChannels.Add(NewCanonical(baseTime.AddSeconds(50)));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Null(status.LastSuccessfulPublicationAtUtc);
        Assert.Equal(baseTime.AddSeconds(50), status.CatalogChangedAtUtc);
        Assert.True(status.PublicationPending);
    }

    [Fact]
    public async Task Recovered_interrupted_run_does_not_advance_cursor()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            // RecoverInterruptedRunsAsync (RunCoordinator.cs:439)
            // materializa runs interrompidas como Failed.
            ctx.LiveRuns.Add(NewRun(
                "telegram-maintain",
                LiveRunTerminalStatus.Failed,
                baseTime.AddSeconds(100),
                lastMessage: "recovered after restart: partial pipeline"));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        // Só com a run Failed, o cursor lastSuccessfulPublication
        // permanece na baseline nula.
        var interim = await service.GetStatusAsync();
        Assert.Null(interim.LastSuccessfulPublicationAtUtc);

        // Inserir depois uma Completed mais antiga (t=50s) — o cursor
        // deve mover-se apenas para t=50s, provando que a Failed de
        // t=100s (mais recente) foi ignorada pelo filtro
        // TerminalStatus=Completed.
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(NewRun(
                "telegram",
                LiveRunTerminalStatus.Completed,
                baseTime.AddSeconds(50)));
            await ctx.SaveChangesAsync();
        }

        var status = await service.GetStatusAsync();
        Assert.Equal(baseTime.AddSeconds(50), status.LastSuccessfulPublicationAtUtc);
        Assert.NotEqual(baseTime.AddSeconds(100), status.LastSuccessfulPublicationAtUtc);
    }

    [Fact]
    public async Task Completed_run_in_non_publication_mode_does_not_advance_cursor()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            // "manual" não é um mode publicação; o filtro Mode ∈ {telegram,
            // telegram-maintain} deve rejeitá-lo.
            ctx.LiveRuns.Add(NewRun("manual", LiveRunTerminalStatus.Completed, baseTime.AddSeconds(100)));
            ctx.CanonicalChannels.Add(NewCanonical(baseTime.AddSeconds(50)));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Null(status.LastSuccessfulPublicationAtUtc);
        Assert.Equal(baseTime.AddSeconds(50), status.CatalogChangedAtUtc);
        Assert.True(status.PublicationPending);
    }

    [Fact]
    public async Task TelegramMaintain_completed_run_advances_cursor()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(NewRun("telegram-maintain", LiveRunTerminalStatus.Completed, baseTime.AddSeconds(100)));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Equal(baseTime.AddSeconds(100), status.LastSuccessfulPublicationAtUtc);
        Assert.False(status.PublicationPending);
    }

    // ===================== ReviewItem: filtro por State =====================

    [Fact]
    public async Task ReviewItem_Resolved_advances_catalog_changed_at()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.ReviewItems.Add(NewReview(ReviewItemState.Resolved, baseTime.AddSeconds(300)));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Equal(baseTime.AddSeconds(300), status.CatalogChangedAtUtc);
        Assert.Null(status.LastSuccessfulPublicationAtUtc);
        Assert.True(status.PublicationPending);
    }

    [Fact]
    public async Task ReviewItem_Ignored_does_not_advance_catalog_changed_at()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.ReviewItems.Add(NewReview(ReviewItemState.Ignored, baseTime.AddSeconds(300)));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        // O filtro State = Resolved exclui Ignored (CatalogResolver.cs:1808-1810).
        Assert.Null(status.CatalogChangedAtUtc);
        Assert.Null(status.LastSuccessfulPublicationAtUtc);
        Assert.True(status.PublicationPending);
    }

    [Fact]
    public async Task ReviewItem_InReview_does_not_advance_catalog_changed_at()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.ReviewItems.Add(NewReview(ReviewItemState.InReview, baseTime.AddSeconds(300)));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Null(status.CatalogChangedAtUtc);
        Assert.Null(status.LastSuccessfulPublicationAtUtc);
        Assert.True(status.PublicationPending);
    }

    // ===================== Excepção documentada: ChannelAlias =====================

    [Fact]
    public async Task ChannelAlias_uses_CreatedAtUtc_not_UpdatedAtUtc()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        long channelId;
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var channel = NewCanonical(baseTime);
            ctx.CanonicalChannels.Add(channel);
            await ctx.SaveChangesAsync();
            channelId = channel.Id;

            // ChannelAliasEntity não tem propriedade UpdatedAtUtc
            // (CatalogEntities.cs:77-91). A imutabilidade da tabela torna
            // CreatedAtUtc o sinal correcto.
            ctx.ChannelAliases.Add(NewAlias(channelId, baseTime.AddSeconds(123)));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Equal(baseTime.AddSeconds(123), status.CatalogChangedAtUtc);
        // Canal foi criado em `baseTime` (UpdatedAtUtc = baseTime); o MAX
        // é dominado pelo alias (123 > 0), garantindo que o sinal entrou.
        Assert.True(status.PublicationPending);
    }

    // ===================== Idempotência =====================

    [Fact]
    public async Task Restart_simulation_yields_same_status_via_new_service_instance()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(NewRun("telegram", LiveRunTerminalStatus.Completed, baseTime.AddSeconds(100)));
            ctx.CanonicalChannels.Add(NewCanonical(baseTime.AddSeconds(200)));
            await ctx.SaveChangesAsync();
        }

        var firstService = new PublicationStatusService(factory);
        var first = await firstService.GetStatusAsync();

        var secondService = new PublicationStatusService(factory);
        var second = await secondService.GetStatusAsync();

        Assert.Equal(first.CatalogChangedAtUtc, second.CatalogChangedAtUtc);
        Assert.Equal(first.LastSuccessfulPublicationAtUtc, second.LastSuccessfulPublicationAtUtc);
        Assert.Equal(first.PublicationPending, second.PublicationPending);
    }

    [Fact]
    public async Task Dispatcharr_failure_after_playlist_write_does_not_block_publication_cursor()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            // Run Completed sem catalog changes — nenhum registo de
            // DispatcharrSyncOutcome é persistido em LiveRuns; a sincronia
            // opcional escreve apenas JSONs em output/, fora da DB. Logo,
            // qualquer Run Completed avança o cursor, independentemente de
            // Dispatcharr ter corrido, ter sido skipped por dry_run ou ter
            // falhado após a escrita da playlist. Isto prova que DL-019
            // (publicação atómica por artefacto) se mantém separada do
            // outcome opcional de Dispatcharr (DL-019 separation).
            ctx.LiveRuns.Add(NewRun("telegram", LiveRunTerminalStatus.Completed, baseTime.AddSeconds(100)));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Equal(baseTime.AddSeconds(100), status.LastSuccessfulPublicationAtUtc);
        Assert.Null(status.CatalogChangedAtUtc);
        Assert.False(status.PublicationPending);
    }

    // ===================== MAX semântico entre timestamps da mesma família =====================

    [Fact]
    public async Task Two_resolved_reviews_only_the_latest_timestamp_wins()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var baseTime = BaseTime();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.ReviewItems.Add(NewReview(ReviewItemState.Resolved, baseTime.AddSeconds(200), fingerprint: "fingerprint-a"));
            ctx.ReviewItems.Add(NewReview(ReviewItemState.Resolved, baseTime.AddSeconds(300), fingerprint: "fingerprint-b"));
            await ctx.SaveChangesAsync();
        }

        var service = new PublicationStatusService(factory);
        var status = await service.GetStatusAsync();

        Assert.Equal(baseTime.AddSeconds(300), status.CatalogChangedAtUtc);
        Assert.Null(status.LastSuccessfulPublicationAtUtc);
        Assert.True(status.PublicationPending);
    }
}

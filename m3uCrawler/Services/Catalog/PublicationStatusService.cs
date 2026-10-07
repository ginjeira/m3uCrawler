using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.LiveRun;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// DL-130 (Phase 5, slice 1) — Cursores de publicação do catálogo.
///
/// <para>
/// Esta classe é um serviço puro: não emite HTTP, não lê ficheiros,
/// não toca em <c>wtelegram.config</c>. A única dependência externa
/// é o <see cref="IDbContextFactory{TContext}"/> que lhe é injectado,
/// para que <see cref="GetStatusAsync"/> possa ser exercitado em
/// testes com uma <c>TestDbContextFactory</c> in-memory, sem
/// precisar de levantar um <c>HttpListener</c> nem um dispatcher
/// real.
/// </para>
/// </summary>
public sealed class PublicationStatusService
{
    private readonly IDbContextFactory<ChannelCatalogDbContext> _dbFactory;

    /// <summary>
    /// Constrói o serviço com a factory de <see cref="ChannelCatalogDbContext"/>
    /// que será usada para abrir um <c>DbContext</c> efémero por chamada a
    /// <see cref="GetStatusAsync"/>. Sem a factory, o serviço não pode
    /// observar o catálogo nem os <c>LiveRun</c>s; portanto a sua ausência
    /// é uma falha de configuração, não um modo degradado.
    /// </summary>
    public PublicationStatusService(IDbContextFactory<ChannelCatalogDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    /// <summary>
    /// Calcula os dois cursores de publicação e o booleano derivado.
    ///
    /// <para>
    /// Os cursores são intencionalmente <c>Max</c> sobre as colunas
    /// <c>UpdatedAtUtc</c>/<c>CreatedAtUtc</c> das entidades que
    /// contribuem para a próxima <c>playlist.m3u</c>, e sobre o
    /// <c>FinishedAtUtc</c> do último <c>LiveRun</c> em modo
    /// publicação bem-sucedido. A escolha dos sinais exactos é
    /// justificada inline nos métodos <see cref="ComputeCatalogChangedAtAsync"/>
    /// e <see cref="ComputeLastSuccessfulPublicationAtAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancelamento cooperativo.</param>
    public async Task<PublicationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var catalogChangedAtUtc = await ComputeCatalogChangedAtAsync(db, cancellationToken).ConfigureAwait(false);
        var lastSuccessfulPublicationAtUtc = await ComputeLastSuccessfulPublicationAtAsync(db, cancellationToken).ConfigureAwait(false);

        // DL-019 — a publicação é atómica por artefacto (escrita via temp+rename)
        // e não existe um caminho manual de "upload". Logo, se nunca houve um
        // Run Completed bem-sucedido, nenhuma playlist foi publicada e o catálogo
        // está por publicar, independentemente do cursor de mudança estar a null
        // (catálogo vazio).
        var publicationPending =
            lastSuccessfulPublicationAtUtc is null
            || catalogChangedAtUtc > lastSuccessfulPublicationAtUtc;

        return new PublicationStatus(
            CatalogChangedAtUtc: catalogChangedAtUtc,
            LastSuccessfulPublicationAtUtc: lastSuccessfulPublicationAtUtc,
            PublicationPending: publicationPending);
    }

    /// <summary>
    /// Cursor <c>catalogChangedAtUtc</c> = <c>MAX</c> sobre o conjunto de
    /// timestamps das entidades que alimentam a próxima publicação.
    ///
    /// <para>
    /// Sinais cobertos (com evidência):
    /// <list type="bullet">
    ///   <item><c>CanonicalChannelEntity.UpdatedAtUtc</c> — <c>CatalogEntities.cs:64</c></item>
    ///   <item><c>ChannelSourceEntity.UpdatedAtUtc</c> — <c>CatalogEntities.cs:1066</c></item>
    ///   <item><c>SourceEntity.UpdatedAtUtc</c> — <c>CatalogEntities.cs:987</c></item>
    ///   <item><c>ChannelAliasEntity.CreatedAtUtc</c> — <c>CatalogEntities.cs:90</c>. NOTA:
    ///         esta entidade NÃO tem <c>UpdatedAtUtc</c> (ver bloco 77–91);
    ///         imutabilidade é suficiente — alias não se actualiza, apenas se
    ///         insere/apaga.</item>
    ///   <item><c>ExternalIdentityEntity.UpdatedAtUtc</c> — <c>CatalogEntities.cs:148</c></item>
    ///   <item><c>ProviderAccountEntity.UpdatedAtUtc</c> — <c>CatalogEntities.cs:847</c></item>
    ///   <item><c>ReviewItemEntity.UpdatedAtUtc</c> filtrado por
    ///         <c>State == Resolved</c> — apenas as resoluções efectivas
    ///         afectam o catálogo. <c>Ignored</c> devolve
    ///         <c>CatalogueChanged: false</c> em <c>CatalogResolver.cs:1808-1810</c>
    ///         e portanto não move o cursor.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Implementação: 7 queries <c>AsNoTracking().MaxAsync</c> agregadas
    /// sequencialmente. As agregações sobre tabelas vazias devolvem
    /// <c>null</c>; o <c>Max</c> final devolve <c>null</c> apenas se as
    /// 7 forem <c>null</c>. Foi preferido o caminho sequencial (em vez
    /// de <c>Task.WhenAll</c>) porque o número de round-trips é
    /// dominado pela latência do SQLite local e a clareza do
    /// "step-by-step with evidence" é mais valiosa do que 7ms poupados
    /// num endpoint cuja frequência é manual/operacional.
    /// </para>
    /// </summary>
    private static async Task<DateTime?> ComputeCatalogChangedAtAsync(
        ChannelCatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var canonicalChannels = await db.CanonicalChannels
            .AsNoTracking()
            .Select(c => (DateTime?)c.UpdatedAtUtc)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var channelSources = await db.ChannelSources
            .AsNoTracking()
            .Select(s => (DateTime?)s.UpdatedAtUtc)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var sources = await db.Sources
            .AsNoTracking()
            .Select(s => (DateTime?)s.UpdatedAtUtc)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var channelAliases = await db.ChannelAliases
            .AsNoTracking()
            .Select(a => (DateTime?)a.CreatedAtUtc)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var externalIdentities = await db.ExternalIdentities
            .AsNoTracking()
            .Select(e => (DateTime?)e.UpdatedAtUtc)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var providerAccounts = await db.ProviderAccounts
            .AsNoTracking()
            .Select(p => (DateTime?)p.UpdatedAtUtc)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var resolvedReviews = await db.ReviewItems
            .AsNoTracking()
            .Where(r => r.State == ReviewItemState.Resolved)
            .Select(r => (DateTime?)r.UpdatedAtUtc)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        return new[]
        {
            canonicalChannels,
            channelSources,
            sources,
            channelAliases,
            externalIdentities,
            providerAccounts,
            resolvedReviews,
        }.Max();
    }

    /// <summary>
    /// Cursor <c>lastSuccessfulPublicationAtUtc</c> = <c>MAX(FinishedAtUtc)</c>
    /// filtrado por:
    /// <list type="bullet">
    ///   <item><c>TerminalStatus == Completed</c> — só runs efectivamente
    ///         terminados com sucesso publicam <c>playlist.m3u</c>
    ///         (DL-019). <c>Failed</c>/<c>Unknown</c> são ignorados.</item>
    ///   <item><c>Mode ∈ { telegram, telegram-maintain }</c> — apenas os
    ///         modos que percorrem a pipeline Telegram produzem a playlist
    ///         final. Os nomes wire vêm de <see cref="LiveRunWireNames.ModeTelegram"/>
    ///         e <see cref="LiveRunWireNames.ModeTelegramMaintain"/>
    ///         (DL-019; <c>LiveRunTypes.cs:165-178</c>).</item>
    /// </list>
    ///
    /// <para>
    /// <c>FinishedAtUtc</c> é assignado por <c>RunCoordinator</c> a partir
    /// de <c>DateTime.UtcNow</c> em <c>RunCoordinator.cs:539</c>
    /// (<c>MarkCompletedAsync</c>) e é o mesmo relógio UTC usado por
    /// todas as colunas <c>*AtUtc</c> do catálogo (EF <c>SaveChangesAsync</c>
    /// no mesmo contexto). Tratar tudo como UTC.
    /// </para>
    /// </summary>
    private static async Task<DateTime?> ComputeLastSuccessfulPublicationAtAsync(
        ChannelCatalogDbContext db,
        CancellationToken cancellationToken)
    {
        return await db.LiveRuns
            .AsNoTracking()
            .Where(r => r.TerminalStatus == LiveRunTerminalStatus.Completed
                        && (r.Mode == LiveRunWireNames.ModeTelegram
                            || r.Mode == LiveRunWireNames.ModeTelegramMaintain))
            .Select(r => (DateTime?)r.FinishedAtUtc)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Snapshot dos dois cursores de publicação do catálogo e do booleano
/// derivado. Todos os campos são imutáveis (record).
/// </summary>
/// <param name="CatalogChangedAtUtc">
/// Instante UTC mais recente em que algo que afecta a próxima
/// publicação mudou. <c>null</c> significa catálogo vazio.
/// </param>
/// <param name="LastSuccessfulPublicationAtUtc">
/// Instante UTC do último Run operacional que terminou em
/// <c>Completed</c> e portanto publicou <c>playlist.m3u</c>.
/// <c>null</c> significa que nenhum Run bem-sucedido alguma vez
/// correu.
/// </param>
/// <param name="PublicationPending">
/// Derivado: <c>true</c> se há mudança no catálogo posterior à
/// última publicação, OU se nunca houve uma publicação
/// bem-sucedida (sem playlist no disco, sem upload manual
/// alternativo — DL-019).
/// </param>
public sealed record PublicationStatus(
    DateTime? CatalogChangedAtUtc,
    DateTime? LastSuccessfulPublicationAtUtc,
    bool PublicationPending);

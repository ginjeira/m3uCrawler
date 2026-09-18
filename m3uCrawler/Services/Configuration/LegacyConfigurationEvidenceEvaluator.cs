using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Resultado da avaliação de evidência legacy.
/// </summary>
/// <param name="HasEvidence">
/// <c>true</c> se existe pelo menos uma evidência objectiva de que a
/// instalação já era operacional antes da PHASE 9C.1.
/// </param>
/// <param name="Reasons">
/// Nomes das evidências detectadas. Nunca contêm credenciais, URLs ou
/// qualquer dado sensível — apenas identificadores internos.
/// </param>
public sealed record LegacyEvidenceResult(bool HasEvidence, IReadOnlyList<string> Reasons);

/// <summary>
/// PHASE 9C.1 — Detecta evidência objectiva de uma instalação já
/// operacional, para adopção legacy (<c>READY</c>) no primeiro arranque
/// após a introdução do lifecycle.
///
/// <para>
/// Critério (apenas entidades que <b>não</b> são criadas por migration ou
/// seed programático/baseline numa instalação nova):
/// </para>
/// <list type="bullet">
///   <item>
///     Entidades de operação no catálogo SQLite: sources, channel-sources,
///     observações, ordering lists/items, import policies, grupos
///     canónicos e mappings, jobs agendados, sync-runs/steps, review items,
///     matching audits, pending country approvals, affinity groups,
///     ownership Dispatcharr e identity rules.
///     <b>Excluídos</b> <c>CanonicalChannels</c>, <c>ChannelAliases</c>
///     (criados pelo seed/baseline), <c>SourcePriorityPolicies</c> e
///     <c>SourceSelectionPolicies</c> (defaults globais criados
///     lazily e, por isso, sem valor probatório de adopção legacy).
///   </item>
///   <item>
///     Artefactos de output produzidos por execuções reais:
///     <c>import_history.json</c>, <c>playlist.m3u</c>,
///     <c>telegram_run_report.json</c> e <c>telegram_playlist_*.m3u</c>.
///   </item>
/// </list>
///
/// <para>
/// Qualquer falha ao ler a BD ou o output é tratada como "sem evidência"
/// (fail-safe): nunca adopta <c>READY</c> com base em leitura parcial.
/// </para>
/// </summary>
public sealed class LegacyConfigurationEvidenceEvaluator
{
    private readonly IDbContextFactory<ChannelCatalogDbContext>? _factory;
    private readonly string? _outputDir;

    public LegacyConfigurationEvidenceEvaluator(
        IDbContextFactory<ChannelCatalogDbContext>? factory,
        string? outputDir)
    {
        _factory = factory;
        _outputDir = outputDir;
    }

    public async Task<LegacyEvidenceResult> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var reasons = new List<string>();

        if (_factory != null)
        {
            try
            {
                await CollectDatabaseEvidenceAsync(reasons, cancellationToken);
            }
            catch (Exception)
            {
                // Fail-safe: uma BD ilegível não prova operação anterior.
                // Não propagamos; apenas deixamos de contar evidência de BD.
            }
        }

        CollectOutputEvidence(reasons);

        return new LegacyEvidenceResult(reasons.Count > 0, reasons);
    }

    private async Task CollectDatabaseEvidenceAsync(
        List<string> reasons,
        CancellationToken cancellationToken)
    {
        await using var context = await _factory!.CreateDbContextAsync(cancellationToken);

        if (await context.Sources.AnyAsync(cancellationToken)) reasons.Add("sources");
        if (await context.ChannelSources.AnyAsync(cancellationToken)) reasons.Add("channel-sources");
        if (await context.ChannelSourceObservations.AnyAsync(cancellationToken)) reasons.Add("channel-source-observations");
        if (await context.OrderingLists.AnyAsync(cancellationToken)) reasons.Add("ordering-lists");
        if (await context.OrderingItems.AnyAsync(cancellationToken)) reasons.Add("ordering-items");
        if (await context.ImportPolicies.AnyAsync(cancellationToken)) reasons.Add("import-policies");
        if (await context.CanonicalGroups.AnyAsync(cancellationToken)) reasons.Add("canonical-groups");
        if (await context.GroupMappings.AnyAsync(cancellationToken)) reasons.Add("group-mappings");
        if (await context.ScheduledJobs.AnyAsync(cancellationToken)) reasons.Add("scheduled-jobs");
        if (await context.SyncRuns.AnyAsync(cancellationToken)) reasons.Add("sync-runs");
        if (await context.SyncRunSteps.AnyAsync(cancellationToken)) reasons.Add("sync-run-steps");
        if (await context.ReviewItems.AnyAsync(cancellationToken)) reasons.Add("review-items");
        if (await context.MatchingAudits.AnyAsync(cancellationToken)) reasons.Add("matching-audits");
        if (await context.PendingCountryApprovals.AnyAsync(cancellationToken)) reasons.Add("pending-country-approvals");
        if (await context.AffinityGroups.AnyAsync(cancellationToken)) reasons.Add("affinity-groups");
        if (await context.DispatcharrChannelOwnerships.AnyAsync(cancellationToken)) reasons.Add("dispatcharr-channels");
        if (await context.DispatcharrStreamOwnerships.AnyAsync(cancellationToken)) reasons.Add("dispatcharr-streams");
        if (await context.IdentityRules.AnyAsync(cancellationToken)) reasons.Add("identity-rules");
    }

    private void CollectOutputEvidence(List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(_outputDir) || !Directory.Exists(_outputDir))
        {
            return;
        }

        try
        {
            if (File.Exists(Path.Combine(_outputDir, "import_history.json"))) reasons.Add("import-history");
            if (File.Exists(Path.Combine(_outputDir, "playlist.m3u"))) reasons.Add("playlist");
            if (File.Exists(Path.Combine(_outputDir, "telegram_run_report.json"))) reasons.Add("run-report");
            if (Directory.EnumerateFiles(_outputDir, "telegram_playlist_*.m3u").Any()) reasons.Add("telegram-playlist");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fail-safe: não conseguimos ler o output; não é evidência.
        }
    }
}

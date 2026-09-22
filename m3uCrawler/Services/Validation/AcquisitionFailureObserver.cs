using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// W2 — informação de uma falha de aquisição, sanitizada. Não inclui
/// credenciais, tokens nem URLs com credenciais
/// (<c>docs/Reestructure/17-SECURITY.md §1</c>).
/// </summary>
public sealed record AcquisitionFailureInfo(
    string? Url,
    AcquisitionFailureKind FailureKind,
    int? HttpStatus,
    string? Detail,
    bool RetryExhausted,
    int Attempts)
{
    public string PersistedKind => AcquisitionFailureClassifier.ToPersistedKind(FailureKind);
    public bool IsRetryable => AcquisitionFailureClassifier.IsRetryable(FailureKind);
}

/// <summary>
/// W2 — ponto de extensão de persistência de falhas de aquisição, para que o
/// <see cref="M3uTesterService"/> não conheça o catálogo nem o RunReport.
/// </summary>
public interface IAcquisitionFailureObserver
{
    Task OnAcquisitionFailureAsync(AcquisitionFailureInfo failure, CancellationToken cancellationToken);
}

/// <summary>
/// W2 — implementação concreta: persiste a falha na <c>Source</c> e agrega-a
/// no <see cref="RunReport"/>. A mesma falha é representada nos dois sítios
/// (<c>docs/Reestructure/19-FAILURE-MODEL.md §6</c>).
/// </summary>
public sealed class CatalogAcquisitionFailureObserver : IAcquisitionFailureObserver
{
    private readonly CatalogResolver? _resolver;
    private readonly long _sourceId;
    private readonly bool _persistSource;
    private readonly RunReport? _report;
    private readonly Func<DateTime> _clock;

    public CatalogAcquisitionFailureObserver(
        CatalogResolver resolver,
        long sourceId,
        RunReport? report = null,
        Func<DateTime>? clock = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        if (sourceId <= 0) throw new ArgumentOutOfRangeException(nameof(sourceId));
        _sourceId = sourceId;
        _persistSource = true;
        _report = report;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// W2-FU-1 (2026-09-22) — sobrecarga aditiva com <c>sourceId</c>
    /// opcional. Quando <paramref name="sourceId"/> é <c>null</c>, o
    /// observer NÃO persiste em <c>Source</c> (a identidade operacional
    /// ainda não está ligada ao boundary de processamento de candidatos
    /// — ver W2-FU-2 follow-up) mas continua a agregar em
    /// <see cref="RunReport"/>. O <paramref name="resolver"/> pode ser
    /// <c>null</c> quando <paramref name="sourceId"/> é <c>null</c>
    /// (não é necessário para o caminho sem persistência).
    /// </summary>
    public CatalogAcquisitionFailureObserver(
        CatalogResolver? resolver,
        long? sourceId,
        RunReport? report = null,
        Func<DateTime>? clock = null)
    {
        if (sourceId.HasValue && sourceId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (sourceId.HasValue && resolver is null)
            throw new ArgumentNullException(nameof(resolver),
                "Resolver is required when sourceId is provided.");
        _resolver = resolver;
        _sourceId = sourceId ?? 0;
        _persistSource = sourceId.HasValue;
        _report = report;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public async Task OnAcquisitionFailureAsync(
        AcquisitionFailureInfo failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var safeUrl = CredentialSanitizer.SanitizeUrl(failure.Url ?? string.Empty);
        var detail = $"kind={failure.PersistedKind} attempts={failure.Attempts} url={safeUrl}"
                     + (failure.Detail is null ? string.Empty : $" detail={failure.Detail}");

        if (_persistSource && _resolver is not null)
        {
            await _resolver.MarkSourceAcquisitionFailureAsync(
                _sourceId,
                failure.PersistedKind,
                _clock(),
                failure.HttpStatus,
                detail,
                cancellationToken).ConfigureAwait(false);
        }

        if (_report is not null)
        {
            System.Threading.Interlocked.Increment(ref _report._AcquisitionFailures);
            if (failure.IsRetryable)
            {
                System.Threading.Interlocked.Increment(ref _report._AcquisitionRetryableFailures);
            }
            else
            {
                System.Threading.Interlocked.Increment(ref _report._AcquisitionTerminalFailures);
            }
        }
    }
}

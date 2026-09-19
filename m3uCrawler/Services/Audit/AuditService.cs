using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace m3uCrawler.Services.Audit;

/// <summary>
/// W6a — Implementação de <see cref="IAuditService"/> sobre o catálogo SQLite.
///
/// <para>
/// Garantias:
/// </para>
/// <list type="bullet">
///   <item>o timestamp é atribuído pelo serviço (relógio injectável, determinístico
///         em testes) e nunca pelo chamador;</item>
///   <item>antes/depois/detalhe passam por <see cref="CredentialSanitizer.SanitizeJson"/>
///         / <see cref="CredentialSanitizer.SanitizeSensitiveText"/> — nunca são
///         persistidos segredos;</item>
///   <item>best-effort: qualquer falha de escrita é registada (log) e engolida,
///         nunca aborta a mutação administrativa.</item>
/// </list>
/// </summary>
public sealed class AuditService : IAuditService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IDbContextFactory<ChannelCatalogDbContext> _factory;
    private readonly Func<DateTime> _clock;
    private readonly ILogger _logger;

    public AuditService(
        IDbContextFactory<ChannelCatalogDbContext> factory,
        Func<DateTime>? clock = null,
        ILogger<AuditService>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = logger ?? NullLogger<AuditService>.Instance;
    }

    public async Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        if (record is null) return;

        try
        {
            var entity = new AuditRecordEntity
            {
                OccurredAtUtc = _clock(),
                ActorType = Clamp(record.Actor.Type, 20) ?? AuditActorType.System,
                ActorId = Clamp(record.Actor.Id, 64),
                ActorName = Clamp(record.Actor.Name, 128),
                Operation = Clamp(record.Operation, 80) ?? string.Empty,
                ObjectType = Clamp(record.ObjectType, 80) ?? string.Empty,
                ObjectId = Clamp(record.ObjectId, 128),
                BeforeJson = Sanitize(record.Before),
                AfterJson = Sanitize(record.After),
                Result = Clamp(record.Result, 20) ?? AuditResult.Success,
                Detail = CredentialSanitizer.SanitizeSensitiveText(record.Detail),
            };

            await using var context = await _factory.CreateDbContextAsync(cancellationToken);
            context.AuditRecords.Add(entity);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort e observável: a mutação administrativa já aconteceu e
            // não pode ser revertida por uma falha de auditoria.
            _logger.LogWarning(
                ex,
                "Audit record failed for operation {Operation} on {ObjectType}/{ObjectId}",
                record.Operation,
                record.ObjectType,
                record.ObjectId);
        }
    }

    private static string? Sanitize(object? value)
    {
        if (value is null) return null;

        string raw;
        try
        {
            raw = value is string s ? s : JsonSerializer.Serialize(value, SerializerOptions);
        }
        catch (Exception)
        {
            // Objecto não serializável: não arriscar expor conteúdo.
            return null;
        }

        return CredentialSanitizer.SanitizeJson(raw);
    }

    private static string? Clamp(string? value, int maxLength)
    {
        if (value is null) return null;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}

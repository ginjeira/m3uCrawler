using System.Globalization;

namespace m3uCrawler.Services.Audit;

/// <summary>Tipo de actor de um registo de auditoria.</summary>
public static class AuditActorType
{
    /// <summary>Sessão humana autenticada (administrador).</summary>
    public const string User = "user";

    /// <summary>Sistema: máquina/bootstrap/pipeline sem sessão humana.</summary>
    public const string System = "system";
}

/// <summary>Resultado de uma operação auditada.</summary>
public static class AuditResult
{
    public const string Success = "success";
    public const string Failure = "failure";
}

/// <summary>
/// Actor de uma alteração administrativa. Imutável e desprovido de segredos —
/// apenas tipo, identificador e nome.
/// </summary>
public sealed record AuditActor(string Type, string? Id = null, string? Name = null)
{
    /// <summary>Actor humano (administrador) a partir do Id da sessão.</summary>
    public static AuditActor User(long adminUserId, string? name)
        => new(AuditActorType.User, adminUserId.ToString(CultureInfo.InvariantCulture), name);

    /// <summary>Actor de sistema, identificado por um nome estável (ex.: <c>machine-token</c>).</summary>
    public static AuditActor System(string name)
        => new(AuditActorType.System, null, name);
}

/// <summary>
/// Pedido de registo de auditoria. <see cref="Before"/> e <see cref="After"/> são
/// objectos serializáveis (projecções sem ciclos e sem segredos); o serviço
/// aplica a sanitização centralizada antes de persistir.
/// </summary>
public sealed record AuditRecord
{
    public AuditActor Actor { get; init; } = AuditActor.System("system");

    public required string Operation { get; init; }

    public required string ObjectType { get; init; }

    public string? ObjectId { get; init; }

    public object? Before { get; init; }

    public object? After { get; init; }

    public string Result { get; init; } = AuditResult.Success;

    public string? Detail { get; init; }
}

/// <summary>
/// W6a — Contrato de escrita de auditoria administrativa. Uma única operação,
/// <see cref="RecordAsync"/>, best-effort e observável: nunca lança para o
/// chamador (uma falha de auditoria é registada e não aborta a mutação).
/// </summary>
public interface IAuditService
{
    Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default);
}

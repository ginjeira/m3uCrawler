using System;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// W6a — Registo de auditoria de uma operação administrativa.
///
/// <para>
/// Requisito normativo: qualquer alteração administrativa deve produzir um
/// registo com actor, timestamp, operação, objecto, antes/depois e resultado
/// (<c>docs/Reestructure/17-SECURITY.md</c> §Segurança). Persistido no mesmo
/// catálogo SQLite (fonte de verdade, <c>16-PERSISTENCE.md</c>).
/// </para>
///
/// <para>
/// <see cref="BeforeJson"/> e <see cref="AfterJson"/> são sempre pré-sanitizados
/// pelo <c>AuditService</c> (URLs com credenciais e campos sensíveis redigidos);
/// nunca contêm segredos (<c>17-SECURITY.md</c>, <c>31-DECISION-LOCK.md</c>).
/// </para>
/// </summary>
public sealed class AuditRecordEntity
{
    public long Id { get; set; }

    /// <summary>Momento do registo em UTC, atribuído pelo serviço (relógio injectável).</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary><c>user</c> (sessão humana) ou <c>system</c> (máquina/bootstrap/pipeline).</summary>
    public string ActorType { get; set; } = "system";

    /// <summary>Identificador do actor quando aplicável (ex.: Id do administrador).</summary>
    public string? ActorId { get; set; }

    /// <summary>Nome do actor quando aplicável (ex.: username). Nunca contém segredos.</summary>
    public string? ActorName { get; set; }

    /// <summary>Operação lógica (ex.: <c>catalog.channel.create</c>).</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Tipo do objecto afectado (ex.: <c>canonical-channel</c>).</summary>
    public string ObjectType { get; set; } = string.Empty;

    /// <summary>Identificador do objecto afectado (chave/Id), quando aplicável.</summary>
    public string? ObjectId { get; set; }

    /// <summary>Estado anterior, JSON sanitizado. <c>null</c> para criação.</summary>
    public string? BeforeJson { get; set; }

    /// <summary>Estado posterior, JSON sanitizado. <c>null</c> para remoção.</summary>
    public string? AfterJson { get; set; }

    /// <summary><c>success</c> ou <c>failure</c>.</summary>
    public string Result { get; set; } = "success";

    /// <summary>Detalhe textual curto, sanitizado.</summary>
    public string? Detail { get; set; }
}

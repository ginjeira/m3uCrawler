using System;
using System.Collections.Generic;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Grupos canónicos por omissão (PHASE 8 <see cref="CanonicalGroupEntity"/>).
/// As chaves vivem em <see cref="CanonicalGroupKeys"/> (strings estáveis);
/// o enum legado <c>CanonicalEditorialGroup</c> foi removido na Wave D2.
///
/// <para>
/// Esta é a <b>única fonte C#</b> do mapeamento
/// <c>(Key/DisplayName/Order/IsDefault)</c>. A migration
/// <c>AddCanonicalChannelGroupFk</c> duplica o mesmo mapeamento em SQL
/// bruto (uma data migration SQLite não pode chamar C#) — manter os
/// dois em sincronia. Ver
/// <c>Migrations/20261007150000_AddCanonicalChannelGroupFk.cs</c>.
/// </para>
/// </summary>
public static class CanonicalGroupDefaults
{
    /// <summary>
    /// Definição de um grupo canónico por omissão.
    /// </summary>
    /// <param name="Key">Slug único estável (chave de idempotência).</param>
    /// <param name="DisplayName">Nome editorial por omissão.</param>
    /// <param name="Order">Ordem de apresentação.</param>
    /// <param name="IsDefault">Se é o grupo por omissão.</param>
    public sealed record GroupDefault(
        string Key,
        string DisplayName,
        int Order,
        bool IsDefault);

    /// <summary>
    /// Os 9 grupos por omissão, pela ordem canónica.
    /// </summary>
    public static readonly IReadOnlyList<GroupDefault> All = new[]
    {
        new GroupDefault(CanonicalGroupKeys.PortugalGeneralistas, "PortugalLive", 0, true),
        new GroupDefault(CanonicalGroupKeys.PortugalFilmesSeries, "PortugalFilmes24_7", 1, false),
        new GroupDefault(CanonicalGroupKeys.PortugalEntretenimento, "PortugalEntretenimento", 2, false),
        new GroupDefault(CanonicalGroupKeys.PortugalDesporto, "PortugalDesporto", 3, false),
        new GroupDefault(CanonicalGroupKeys.PortugalInfantil, "PortugalInfantil", 4, false),
        new GroupDefault(CanonicalGroupKeys.PortugalDocumentarios, "PortugalDocumentarios", 5, false),
        new GroupDefault(CanonicalGroupKeys.PortugalPPV, "PortugalPPV", 6, false),
        new GroupDefault(CanonicalGroupKeys.International, "Foreign", 7, false),
        new GroupDefault(CanonicalGroupKeys.Other, "Other", 8, false),
    };
}

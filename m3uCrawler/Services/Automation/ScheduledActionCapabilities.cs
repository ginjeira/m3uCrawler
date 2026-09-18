using System;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// Capacidades operacionais exigidas por uma acção agendada. Permite que
/// o gate de prontidão seja aplicado por acção em vez de globalmente: uma
/// acção de discovery não deve ficar bloqueada só porque a sessão
/// Telegram não está autenticada.
///
/// <para>
/// O gate global de bootstrap (lifecycle <c>READY</c>) mantém-se para
/// todos os jobs; estas capacidades são verificadas adicionalmente,
/// apenas para a acção que as declara.
/// </para>
/// </summary>
[Flags]
public enum ScheduledActionCapabilities
{
    None = 0,

    /// <summary>Requer sessão Telegram autenticada (discovery Telegram).</summary>
    Telegram = 1,

    /// <summary>Requer Dispatcharr activo e com credenciais válidas.</summary>
    Dispatcharr = 2,

    /// <summary>Requer catálogo canónico com pelo menos um canal.</summary>
    Catalog = 4,

    /// <summary>Requer pasta de output gravável.</summary>
    Output = 8,
}

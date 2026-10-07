namespace m3uCrawler.Services.Configuration;

/// <summary>
/// PHASE 9C.1 — Estado do ciclo de vida de configuração da instalação.
///
/// <para>
/// Apenas os três estados necessários para a primeira wave. A máquina de
/// estados completa (RUNNING/ERROR, health, etc.) fica para waves
/// posteriores da PHASE 9C. O estado é persistido e sobrevive a restart.
/// </para>
/// </summary>
public enum ConfigurationLifecycleState
{
    /// <summary>
    /// Instalação nova sem configuração persistida e sem evidência de
    /// operação anterior. Discovery automático e scheduler bloqueados.
    /// </summary>
    NotConfigured = 0,

    /// <summary>
    /// Configuração em curso (wizard/transição explícita). Continua a
    /// bloquear discovery automático e scheduler.
    /// </summary>
    Configuring = 1,

    /// <summary>
    /// Instalação configurada. O comportamento operacional existente é
    /// preservado (discovery automático e scheduler permitidos).
    /// </summary>
    Ready = 2,
}

/// <summary>
/// Nomes estáveis usados em JSON, logs e na API do dashboard. Não renomear
/// sem actualizar os consumidores — são contrato externo.
/// </summary>
public static class ConfigurationLifecycleStateNames
{
    public const string NotConfigured = "NOT_CONFIGURED";
    public const string Configuring = "CONFIGURING";
    public const string Ready = "READY";

    public static string ToWireName(this ConfigurationLifecycleState state) => state switch
    {
        ConfigurationLifecycleState.Ready => Ready,
        ConfigurationLifecycleState.Configuring => Configuring,
        _ => NotConfigured,
    };

    public static bool TryParse(string? raw, out ConfigurationLifecycleState state)
    {
        switch (raw?.Trim().ToUpperInvariant())
        {
            case Ready:
                state = ConfigurationLifecycleState.Ready;
                return true;
            case Configuring:
                state = ConfigurationLifecycleState.Configuring;
                return true;
            case NotConfigured:
                state = ConfigurationLifecycleState.NotConfigured;
                return true;
            default:
                state = ConfigurationLifecycleState.NotConfigured;
                return false;
        }
    }
}

/// <summary>
/// Fotografia imutável do estado persistido do lifecycle.
/// </summary>
/// <param name="State">Estado actual.</param>
/// <param name="AdoptedFromLegacy">
/// <c>true</c> quando o estado <see cref="ConfigurationLifecycleState.Ready"/>
/// foi atribuído por adopção de uma instalação já operacional (legacy
/// adoption), e não por transição explícita do operador.
/// </param>
/// <param name="AdoptedAtUtc">Instante da adopção legacy, quando aplicável.</param>
/// <param name="LastReason">
/// Razão curta e não sensível da última transição (ex.: nomes de
/// evidências legacy detectadas).
/// </param>
/// <param name="UpdatedAtUtc">Instante da última escrita do estado.</param>
public sealed record ConfigurationLifecycleSnapshot(
    ConfigurationLifecycleState State,
    bool AdoptedFromLegacy,
    DateTime? AdoptedAtUtc,
    string LastReason,
    DateTime UpdatedAtUtc)
{
    public bool IsReady => State == ConfigurationLifecycleState.Ready;
}

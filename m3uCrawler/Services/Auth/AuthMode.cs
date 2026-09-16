using m3uCrawler.Services.Configuration;

namespace m3uCrawler.Services.Auth;

/// <summary>
/// PHASE 9C.2 — Modo de autorização do Dashboard, derivado deterministicamente
/// do lifecycle e da existência de administrador activo.
/// </summary>
public enum AuthMode
{
    /// <summary>
    /// <c>NOT_CONFIGURED</c>/<c>CONFIGURING</c>: só o fluxo de bootstrap está
    /// disponível; endpoints administrativos normais ficam bloqueados.
    /// </summary>
    Bootstrap = 0,

    /// <summary>
    /// <c>READY</c> e existe administrador activo: autenticação humana por
    /// sessão. <c>--web-token</c>, quando configurado, continua válido como
    /// credencial de máquina.
    /// </summary>
    UserAuth = 1,

    /// <summary>
    /// <c>READY</c> mas sem administrador (adopção legacy da 9C.1): mantém
    /// exactamente o comportamento actual baseado apenas em <c>--web-token</c>.
    /// Não cria administrador nem migra credenciais.
    /// </summary>
    Legacy = 2,
}

/// <summary>
/// PHASE 9C.2 — Resolução pura (testável) do modo de autorização.
/// </summary>
public static class AuthModeResolver
{
    public static AuthMode Resolve(ConfigurationLifecycleState lifecycleState, bool hasActiveAdmin)
    {
        if (lifecycleState != ConfigurationLifecycleState.Ready)
        {
            return AuthMode.Bootstrap;
        }

        return hasActiveAdmin ? AuthMode.UserAuth : AuthMode.Legacy;
    }
}

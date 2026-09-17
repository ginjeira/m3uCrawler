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
    /// Comportamento aberto baseado apenas em <c>--web-token</c>, sem sessão
    /// humana. Só é produzido em contexto explicitamente standalone/testes
    /// (<c>_standaloneAuthContext</c>), onde lifecycle e autenticação estão
    /// ambos ausentes e isso é legítimo. Não cria administrador nem migra
    /// credenciais. Deixou de ser o modo de uma instalação <c>READY</c> sem
    /// administrador a partir da PHASE 9C.5 (ver <see cref="AuthModeResolver"/>).
    /// </summary>
    Legacy = 2,
}

/// <summary>
/// PHASE 9C.2 — Resolução pura (testável) do modo de autorização.
/// PHASE 9C.5 — <c>READY</c> sem administrador activo passa a resolver para
/// <see cref="AuthMode.Bootstrap"/> (cenário conceptual <c>BOOTSTRAP_REQUIRED</c>),
/// tornando o First-Run Wizard alcançável numa instalação legacy adoptada.
/// </summary>
public static class AuthModeResolver
{
    public static AuthMode Resolve(ConfigurationLifecycleState lifecycleState, bool hasActiveAdmin)
    {
        // NOT_CONFIGURED / CONFIGURING: wizard de primeira execução.
        if (lifecycleState != ConfigurationLifecycleState.Ready)
        {
            return AuthMode.Bootstrap;
        }

        // PHASE 9C.5 — READY sem administrador activo é BOOTSTRAP_REQUIRED:
        // a configuração existente (possivelmente adoptada de legacy) está
        // preservada e válida, faltando apenas o primeiro administrador. O
        // wizard continua activo sem reconfigurar nada. Com administrador
        // activo, o fluxo é autenticação humana normal.
        return hasActiveAdmin ? AuthMode.UserAuth : AuthMode.Bootstrap;
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace m3uCrawler.Services.Auth;

/// <summary>
/// W10a — Resultado de uma recuperação de password do administrador.
/// </summary>
public enum AdminPasswordResetOutcome
{
    Changed = 0,
    UserNotFound = 1,
    InvalidPassword = 2,
    Mismatch = 3,
}

/// <summary>
/// W10a — Recuperação de password do administrador a partir do host.
///
/// <para>
/// Este fluxo é <b>host-only</b> (CLI): deliberadamente não pede a password
/// actual, porque é precisamente o caso de perdeu o acesso. Não tem qualquer
/// dependência de HTTP nem de consola — a leitura da password é injectada via
/// <c>passwordReader</c>, o que o torna testável. Nunca registar (log) a
/// password nem o hash.
/// </para>
/// </summary>
public sealed class AdminPasswordResetService
{
    private readonly AdminUserStore _users;

    public AdminPasswordResetService(AdminUserStore users)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
    }

    /// <summary>
    /// Lê a nova password e a confirmação através de <paramref name="passwordReader"/>
    /// (chamado duas vezes, pela ordem: nova password, depois confirmação).
    /// Divergência devolve <see cref="AdminPasswordResetOutcome.Mismatch"/> sem
    /// qualquer alteração. Caso contrário delega em
    /// <see cref="AdminUserStore.ChangePasswordByUsernameAsync"/>.
    /// </summary>
    public async Task<AdminPasswordResetOutcome> ResetAsync(
        string username,
        Func<string> passwordReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passwordReader);

        var newPassword = passwordReader() ?? string.Empty;
        var confirmation = passwordReader() ?? string.Empty;

        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            return AdminPasswordResetOutcome.Mismatch;
        }

        var result = await _users.ChangePasswordByUsernameAsync(
            username, newPassword, cancellationToken);

        return result switch
        {
            ChangePasswordResult.Changed => AdminPasswordResetOutcome.Changed,
            ChangePasswordResult.UserNotFound => AdminPasswordResetOutcome.UserNotFound,
            _ => AdminPasswordResetOutcome.InvalidPassword,
        };
    }
}

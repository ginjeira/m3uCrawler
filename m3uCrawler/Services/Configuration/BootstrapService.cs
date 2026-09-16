using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Auth;

namespace m3uCrawler.Services.Configuration;

public enum BootstrapStartOutcome
{
    Started = 0,
    AlreadyConfiguring = 1,
    AlreadyReady = 2,
}

public enum BootstrapAdminOutcome
{
    Created = 0,
    AlreadyCreated = 1,
    NotStarted = 2,
    AlreadyReady = 3,
    Invalid = 4,
}

public enum BootstrapCompleteOutcome
{
    Completed = 0,
    AlreadyReady = 1,
    NotStarted = 2,
    MissingAdmin = 3,
    InvalidConfiguration = 4,
}

public sealed record BootstrapStatus(
    ConfigurationLifecycleState State,
    bool HasActiveAdmin,
    IReadOnlyList<BootstrapCheck> Checks);

public sealed record BootstrapValidation(
    BootstrapCompleteOutcome Outcome,
    IReadOnlyList<BootstrapCheck> Checks);

/// <summary>
/// PHASE 9C.2 — Fluxo de bootstrap do primeiro administrador.
///
/// <para>
/// Invariantes garantidas:
/// </para>
/// <list type="bullet">
///   <item>nunca <c>READY</c> sem administrador activo (o estado só avança
///   depois de a criação e a validação L2 terem sucesso);</item>
///   <item>nunca <c>READY</c> sem L2 válida;</item>
///   <item>nunca dois primeiros administradores (transacção + tabela vazia +
///   unique);</item>
///   <item>retry e concorrência seguros (serialização in-process + estado
///   persistido idempotente);</item>
///   <item>restart em <c>CONFIGURING</c> recuperável (a existência do admin
///   determina o passo de retoma);</item>
///   <item>bootstrap impossível depois de <c>READY</c>.</item>
/// </list>
/// </summary>
public sealed class BootstrapService
{
    private readonly ConfigurationLifecycleService _lifecycle;
    private readonly AdminUserStore _users;
    private readonly BootstrapConfigurationValidator _validator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BootstrapService(
        ConfigurationLifecycleService lifecycle,
        AdminUserStore users,
        BootstrapConfigurationValidator validator)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<BootstrapStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _lifecycle.GetStateAsync(cancellationToken);
        var hasAdmin = await _users.HasActiveAdminAsync(cancellationToken);
        return new BootstrapStatus(snapshot.State, hasAdmin, _validator.Evaluate());
    }

    public async Task<BootstrapStartOutcome> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = (await _lifecycle.GetStateAsync(cancellationToken)).State;
            if (state == ConfigurationLifecycleState.Ready)
            {
                return BootstrapStartOutcome.AlreadyReady;
            }

            if (state == ConfigurationLifecycleState.Configuring)
            {
                return BootstrapStartOutcome.AlreadyConfiguring;
            }

            _lifecycle.SetState(ConfigurationLifecycleState.Configuring, "bootstrap-started");
            return BootstrapStartOutcome.Started;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(BootstrapAdminOutcome Outcome, string? Error)> CreateAdminAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = (await _lifecycle.GetStateAsync(cancellationToken)).State;
            if (state == ConfigurationLifecycleState.Ready)
            {
                return (BootstrapAdminOutcome.AlreadyReady, "already-ready");
            }

            if (state != ConfigurationLifecycleState.Configuring)
            {
                return (BootstrapAdminOutcome.NotStarted, "bootstrap-not-started");
            }

            // Idempotência: se já existe administrador, não criar outro nem
            // o substituir.
            if (await _users.HasAnyAsync(cancellationToken))
            {
                return (BootstrapAdminOutcome.AlreadyCreated, null);
            }

            var usernameError = CredentialPolicy.ValidateUsername(username);
            if (usernameError != null)
            {
                return (BootstrapAdminOutcome.Invalid, usernameError);
            }

            var passwordError = CredentialPolicy.ValidatePassword(password);
            if (passwordError != null)
            {
                return (BootstrapAdminOutcome.Invalid, passwordError);
            }

            var result = await _users.CreateFirstAdminAsync(username!.Trim(), password!, cancellationToken);
            return result == CreateAdminResult.Created
                ? (BootstrapAdminOutcome.Created, null)
                : (BootstrapAdminOutcome.AlreadyCreated, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BootstrapValidation> CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var checks = _validator.Evaluate();

            var state = (await _lifecycle.GetStateAsync(cancellationToken)).State;
            if (state == ConfigurationLifecycleState.Ready)
            {
                return new BootstrapValidation(BootstrapCompleteOutcome.AlreadyReady, checks);
            }

            if (state != ConfigurationLifecycleState.Configuring)
            {
                return new BootstrapValidation(BootstrapCompleteOutcome.NotStarted, checks);
            }

            if (!await _users.HasActiveAdminAsync(cancellationToken))
            {
                return new BootstrapValidation(BootstrapCompleteOutcome.MissingAdmin, checks);
            }

            foreach (var check in checks)
            {
                if (!check.Satisfied)
                {
                    return new BootstrapValidation(BootstrapCompleteOutcome.InvalidConfiguration, checks);
                }
            }

            _lifecycle.SetState(ConfigurationLifecycleState.Ready, "bootstrap-complete");
            return new BootstrapValidation(BootstrapCompleteOutcome.Completed, checks);
        }
        finally
        {
            _gate.Release();
        }
    }
}

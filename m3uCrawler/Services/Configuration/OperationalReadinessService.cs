using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Gate de prontidão operacional (Wave 4 da PHASE 9C). Complementa o
/// <see cref="ConfigurationGate"/> — que responde apenas "o lifecycle está
/// READY?" — com a verificação dos componentes concretos que uma
/// instalação precisa de ter funcionais para operar de forma automática.
///
/// <para>
/// Contrato fail-safe: em caso de dúvida, excepção ou dado em falta,
/// <see cref="IsSetupCompleteAsync"/> devolve <c>false</c>.
/// </para>
/// </summary>
public interface IOperationalReadinessGate
{
    /// <summary>
    /// <c>true</c> apenas quando todos os componentes obrigatórios estão
    /// satisfeitos (ou quando a instalação foi adoptada como legacy).
    /// </summary>
    Task<bool> IsSetupCompleteAsync(CancellationToken ct = default);
}

/// <summary>
/// Item individual da prontidão. <see cref="Detail"/> é sempre curto e
/// sem segredos (não inclui URLs, API keys, passwords ou telefones).
/// </summary>
/// <param name="Key">Identificador estável do componente.</param>
/// <param name="Required">Se o componente é obrigatório para <c>SetupComplete</c>.</param>
/// <param name="Satisfied">Se o componente está satisfeito.</param>
/// <param name="Detail">Descrição curta e não sensível.</param>
public sealed record OperationalReadinessItem(string Key, bool Required, bool Satisfied, string Detail);

/// <summary>
/// Fotografia imutável da prontidão operacional.
///
/// <para>
/// <b>Regras (documentadas de forma explícita):</b>
/// </para>
/// <list type="bullet">
///   <item><b>Obrigatórios para <see cref="SetupComplete"/></b>: lifecycle
///   READY (<see cref="BootstrapReady"/>) E <see cref="HasAdmin"/> E
///   <see cref="TelegramAuthenticated"/> E (<see cref="DispatcharrValid"/>
///   apenas quando <see cref="DispatcharrEnabled"/>) E
///   <see cref="CatalogOk"/> E <see cref="CountryDataOk"/> E
///   <see cref="OutputOk"/>.</item>
///   <item><b>Sources</b>: <see cref="SourcesCount"/> conta as
///   <c>channel_sources</c> realmente ingeridas. <b>Não</b> faz parte de
///   <see cref="SetupComplete"/> — caso contrário o scheduler de descoberta
///   ficaria bloqueado por nunca ter fontes (deadlock). É apenas um
///   requisito de <see cref="OperationalReady"/>.</item>
///   <item><b>Legacy grandfathering</b>: quando o lifecycle tem
///   <c>AdoptedFromLegacy == true</c>, <see cref="SetupComplete"/> e
///   <see cref="OperationalReady"/> são ambos <c>true</c>, mesmo que
///   componentes individuais apareçam insatisfeitos. Isto evita bloquear
///   instalações já operacionais antes da introdução do lifecycle. Os
///   itens continuam a ser reportados para diagnóstico, e
///   <see cref="MissingRequired"/> continua a listar os obrigatórios em
///   falta (meramente informativo nesse cenário).</item>
/// </list>
/// </summary>
public sealed record OperationalReadinessSnapshot(
    bool BootstrapReady,
    bool HasAdmin,
    bool TelegramAuthenticated,
    bool DispatcharrEnabled,
    bool DispatcharrValid,
    bool CatalogOk,
    bool CountryDataOk,
    bool OutputOk,
    int SourcesCount,
    bool SetupComplete,
    bool OperationalReady,
    bool AdoptedFromLegacy,
    IReadOnlyList<OperationalReadinessItem> Items,
    IReadOnlyList<string> MissingRequired);

/// <summary>
/// Wave 4 (PHASE 9C) — Serviço de prontidão operacional.
///
/// <para>
/// Todas as dependências são injectadas como delegates para permitir
/// avaliação determinística em testes e composição explícita em
/// <c>Program.cs</c>, sem construir uma segunda pipeline de serviços.
/// </para>
///
/// <para>
/// Invariantes de segurança: nenhum <c>Detail</c> inclui segredos. O
/// serviço nunca regista valores de configuração.
/// </para>
/// </summary>
public sealed class OperationalReadinessService : IOperationalReadinessGate
{
    public const string KeyBootstrap = "bootstrap";
    public const string KeyAdmin = "admin";
    public const string KeyTelegram = "telegram";
    public const string KeyDispatcharr = "dispatcharr";
    public const string KeyCatalog = "catalog";
    public const string KeyCountryData = "countryData";
    public const string KeyOutput = "output";
    public const string KeySources = "sources";

    private readonly ConfigurationLifecycleService _lifecycle;
    private readonly Func<CancellationToken, Task<bool>> _hasActiveAdmin;
    private readonly Func<bool> _telegramAuthenticated;
    private readonly Func<DispatcharrConfig> _dispatcharrConfig;
    private readonly Func<CancellationToken, Task<bool>> _catalogHasCanonicalChannels;
    private readonly Func<CancellationToken, Task<bool>> _countryDataAvailable;
    private readonly Func<bool> _outputWritable;
    private readonly Func<CancellationToken, Task<int>> _channelSourceCount;
    private readonly Func<DispatcharrTestRecord?> _dispatcharrLastTest;
    private readonly Func<DateTimeOffset> _utcNow;

    /// <summary>
    /// W6c — Idade máxima de um teste de ligação bem sucedido para que a
    /// prontidão o considere actual. Um teste mais antigo é "stale".
    /// </summary>
    public static readonly TimeSpan DispatcharrTestMaxAge = TimeSpan.FromHours(24);

    public OperationalReadinessService(
        ConfigurationLifecycleService lifecycle,
        Func<CancellationToken, Task<bool>> hasActiveAdmin,
        Func<bool> telegramAuthenticated,
        Func<DispatcharrConfig> dispatcharrConfig,
        Func<CancellationToken, Task<bool>> catalogHasCanonicalChannels,
        Func<CancellationToken, Task<bool>> countryDataAvailable,
        Func<bool> outputWritable,
        Func<CancellationToken, Task<int>> channelSourceCount,
        Func<DispatcharrTestRecord?>? dispatcharrLastTest = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _hasActiveAdmin = hasActiveAdmin ?? throw new ArgumentNullException(nameof(hasActiveAdmin));
        _telegramAuthenticated = telegramAuthenticated ?? throw new ArgumentNullException(nameof(telegramAuthenticated));
        _dispatcharrConfig = dispatcharrConfig ?? throw new ArgumentNullException(nameof(dispatcharrConfig));
        _catalogHasCanonicalChannels = catalogHasCanonicalChannels
            ?? throw new ArgumentNullException(nameof(catalogHasCanonicalChannels));
        _countryDataAvailable = countryDataAvailable
            ?? throw new ArgumentNullException(nameof(countryDataAvailable));
        _outputWritable = outputWritable ?? throw new ArgumentNullException(nameof(outputWritable));
        _channelSourceCount = channelSourceCount ?? throw new ArgumentNullException(nameof(channelSourceCount));
        _dispatcharrLastTest = dispatcharrLastTest ?? (() => null);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<OperationalReadinessSnapshot> EvaluateAsync(CancellationToken ct = default)
    {
        ConfigurationLifecycleSnapshot? lifecycleSnapshot;
        try
        {
            lifecycleSnapshot = await _lifecycle.GetStateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fail-safe: um lifecycle ilegível não pode ser considerado READY.
            lifecycleSnapshot = null;
        }

        var adoptedFromLegacy = lifecycleSnapshot?.AdoptedFromLegacy ?? false;
        var bootstrapReady = lifecycleSnapshot?.IsReady ?? false;

        var hasAdmin = await SafeBoolAsync(_hasActiveAdmin, ct).ConfigureAwait(false);
        var telegramAuthenticated = SafeBool(_telegramAuthenticated);
        var dispatcharr = SafeDispatcharr(_dispatcharrConfig);
        var catalogOk = await SafeBoolAsync(_catalogHasCanonicalChannels, ct).ConfigureAwait(false);
        var countryDataOk = await SafeBoolAsync(_countryDataAvailable, ct).ConfigureAwait(false);
        var outputOk = SafeBool(_outputWritable);
        var sourcesCount = await SafeCountAsync(_channelSourceCount, ct).ConfigureAwait(false);

        var dispatcharrEnabled = dispatcharr.Enabled;
        var dispatcharrCredentialsValid = HasDispatcharrCredentials(dispatcharr);
        // Só lê o último teste quando a integração está activada: evita
        // efeitos laterais no settings store para instalações que não usam
        // Dispatcharr.
        var lastTest = dispatcharrEnabled ? SafeLastTest(_dispatcharrLastTest) : null;
        var dispatcharrTestFresh = lastTest is not null
            && lastTest.IsConnected
            && (_utcNow() - lastTest.TestedAtUtc) <= DispatcharrTestMaxAge;
        var dispatcharrValid = !dispatcharrEnabled
            || (dispatcharrCredentialsValid && dispatcharrTestFresh);

        var items = new List<OperationalReadinessItem>
        {
            new(
                KeyBootstrap,
                Required: true,
                Satisfied: bootstrapReady,
                Detail: bootstrapReady ? "lifecycle READY." : "lifecycle não está READY."),
            new(
                KeyAdmin,
                Required: true,
                Satisfied: hasAdmin,
                Detail: hasAdmin ? "administrador activo." : "sem administrador activo."),
            new(
                KeyTelegram,
                Required: true,
                Satisfied: telegramAuthenticated,
                Detail: telegramAuthenticated ? "sessão Telegram autenticada." : "sessão Telegram não autenticada."),
            new(
                KeyDispatcharr,
                Required: dispatcharrEnabled,
                Satisfied: dispatcharrValid,
                Detail: DescribeDispatcharr(
                    dispatcharrEnabled, dispatcharrCredentialsValid, lastTest, dispatcharrTestFresh)),
            new(
                KeyCatalog,
                Required: true,
                Satisfied: catalogOk,
                Detail: catalogOk ? "catálogo canónico com canais." : "catálogo canónico vazio ou indisponível."),
            new(
                KeyCountryData,
                Required: true,
                Satisfied: countryDataOk,
                Detail: countryDataOk
                    ? "dados de país disponíveis."
                    : "dados de país em falta para o país configurado."),
            new(
                KeyOutput,
                Required: true,
                Satisfied: outputOk,
                Detail: outputOk ? "pasta de output gravável." : "pasta de output não gravável."),
            new(
                KeySources,
                Required: false,
                Satisfied: sourcesCount >= 1,
                Detail: sourcesCount >= 1
                    ? $"{sourcesCount} fonte(s) ingerida(s)."
                    : "sem fontes ingeridas."),
        };

        var missingRequired = items
            .Where(i => i.Required && !i.Satisfied)
            .Select(i => i.Key)
            .ToList();

        var mandatorySatisfied = bootstrapReady
            && hasAdmin
            && telegramAuthenticated
            && dispatcharrValid
            && catalogOk
            && countryDataOk
            && outputOk;

        // Legacy grandfathering: uma instalação adoptada como já operacional
        // nunca é bloqueada por componentes que não podem ser confirmados
        // (ex.: sessão Telegram em processo). Os itens são reportados na
        // mesma para diagnóstico.
        var setupComplete = adoptedFromLegacy || mandatorySatisfied;
        var operationalReady = adoptedFromLegacy || (setupComplete && sourcesCount >= 1);

        return new OperationalReadinessSnapshot(
            BootstrapReady: bootstrapReady,
            HasAdmin: hasAdmin,
            TelegramAuthenticated: telegramAuthenticated,
            DispatcharrEnabled: dispatcharrEnabled,
            DispatcharrValid: dispatcharrValid,
            CatalogOk: catalogOk,
            CountryDataOk: countryDataOk,
            OutputOk: outputOk,
            SourcesCount: sourcesCount,
            SetupComplete: setupComplete,
            OperationalReady: operationalReady,
            AdoptedFromLegacy: adoptedFromLegacy,
            Items: items,
            MissingRequired: missingRequired);
    }

    public async Task<bool> IsSetupCompleteAsync(CancellationToken ct = default)
    {
        var snapshot = await EvaluateAsync(ct).ConfigureAwait(false);
        return snapshot.SetupComplete;
    }

    /// <summary>
    /// Credenciais Dispatcharr: exige <c>BaseUrl</c> e (<c>ApiKey</c> OU
    /// <c>Username</c>+<c>Password</c>). A validade operacional exige ainda
    /// um teste de ligação bem sucedido (ver <see cref="EvaluateAsync"/>).
    /// </summary>
    private static bool HasDispatcharrCredentials(DispatcharrConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(config.Username)
            && !string.IsNullOrWhiteSpace(config.Password);
    }

    private static string DescribeDispatcharr(
        bool enabled,
        bool credentialsValid,
        DispatcharrTestRecord? lastTest,
        bool testFresh)
    {
        if (!enabled)
        {
            return "Dispatcharr não activado.";
        }

        if (!credentialsValid)
        {
            return "Dispatcharr activado sem credenciais válidas.";
        }

        if (lastTest is null)
        {
            return "Dispatcharr activado; ligação ainda não testada.";
        }

        if (!lastTest.IsConnected)
        {
            return "Dispatcharr activado; o último teste de ligação falhou.";
        }

        return testFresh
            ? "Dispatcharr activado, credenciais válidas e teste de ligação OK."
            : "Dispatcharr activado; o último teste de ligação expirou.";
    }

    private static DispatcharrTestRecord? SafeLastTest(Func<DispatcharrTestRecord?> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<bool> SafeBoolAsync(
        Func<CancellationToken, Task<bool>> probe,
        CancellationToken ct)
    {
        try
        {
            return await probe(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SafeBool(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static DispatcharrConfig SafeDispatcharr(Func<DispatcharrConfig> probe)
    {
        try
        {
            return probe() ?? DispatcharrConfig.Disabled();
        }
        catch (Exception)
        {
            return DispatcharrConfig.Disabled();
        }
    }

    private static async Task<int> SafeCountAsync(
        Func<CancellationToken, Task<int>> probe,
        CancellationToken ct)
    {
        try
        {
            return await probe(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}

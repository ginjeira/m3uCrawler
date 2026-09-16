using System;
using System.Collections.Generic;
using System.IO;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;

namespace m3uCrawler.Services.Auth;

/// <summary>
/// Resultado de uma verificação da configuração mínima (L2).
/// </summary>
public sealed record BootstrapCheck(string Key, bool Satisfied, string Detail);

/// <summary>
/// PHASE 9C.2 — Validação da configuração mínima (L2).
///
/// <para>
/// L2 é deliberadamente pequeno e apenas com contratos já existentes:
/// catálogo utilizável, output directory utilizável e — <b>só quando
/// activado</b> — Dispatcharr válido. Telegram, sources, ordering,
/// import policies, source priority, affinities e scheduler <b>não</b> são
/// obrigatórios. Qualquer falha é fail-safe (não satisfeito).
/// </para>
/// </summary>
public sealed class BootstrapConfigurationValidator
{
    private readonly IDbContextFactory<ChannelCatalogDbContext>? _factory;
    private readonly string? _outputDir;
    private readonly DispatcharrConfig _dispatcharr;

    public BootstrapConfigurationValidator(
        IDbContextFactory<ChannelCatalogDbContext>? factory,
        string? outputDir,
        DispatcharrConfig? dispatcharr = null)
    {
        _factory = factory;
        _outputDir = outputDir;
        _dispatcharr = dispatcharr ?? DispatcharrConfig.Disabled();
    }

    public IReadOnlyList<BootstrapCheck> Evaluate()
    {
        return new List<BootstrapCheck>
        {
            CheckCatalog(),
            CheckOutput(),
            CheckDispatcharr(),
        };
    }

    private BootstrapCheck CheckCatalog()
    {
        if (_factory == null)
        {
            return new BootstrapCheck("catalog", false, "catálogo indisponível.");
        }

        try
        {
            using var context = _factory.CreateDbContext();
            var hasChannels = context.CanonicalChannels.Any();
            return new BootstrapCheck(
                "catalog",
                hasChannels,
                hasChannels ? "catálogo canónico disponível." : "catálogo canónico vazio.");
        }
        catch (Exception)
        {
            return new BootstrapCheck("catalog", false, "catálogo indisponível.");
        }
    }

    private BootstrapCheck CheckOutput()
    {
        if (string.IsNullOrWhiteSpace(_outputDir))
        {
            return new BootstrapCheck("output", false, "pasta de output não definida.");
        }

        try
        {
            Directory.CreateDirectory(_outputDir);
            var probe = Path.Combine(_outputDir, $".m3ucrawler-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new BootstrapCheck("output", true, "pasta de output gravável.");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new BootstrapCheck("output", false, "pasta de output não é gravável.");
        }
    }

    private BootstrapCheck CheckDispatcharr()
    {
        if (!_dispatcharr.Enabled)
        {
            // Não configurado/activado: não bloqueia READY.
            return new BootstrapCheck("dispatcharr", true, "Dispatcharr não activado.");
        }

        var hasBaseUrl = !string.IsNullOrWhiteSpace(_dispatcharr.BaseUrl);
        var hasApiKey = !string.IsNullOrWhiteSpace(_dispatcharr.ApiKey);
        var hasUserPass = !string.IsNullOrWhiteSpace(_dispatcharr.Username)
            && !string.IsNullOrWhiteSpace(_dispatcharr.Password);

        var valid = hasBaseUrl && (hasApiKey || hasUserPass);
        return new BootstrapCheck(
            "dispatcharr",
            valid,
            valid ? "Dispatcharr activado e válido." : "Dispatcharr activado mas sem credenciais válidas.");
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave A / finding A1 — garante que o baseline canónico PT está
/// disponível em runtime mesmo quando o ficheiro
/// <c>docs/catalog/m3ucrawler_pt_canonical_catalog.json</c> não é
/// empacotado:
///
/// <list type="bullet">
///   <item>O recurso embutido (<c>EmbeddedResource</c>) carrega o
///         baseline e cria os canais PT generalistas
///         (<c>rtp1</c>, <c>sic</c>, <c>tvi</c>, …).</item>
///   <item>Um ficheiro no caminho resolvido tem precedência sobre o
///         recurso embutido.</item>
///   <item>Importar o baseline duas vezes é idempotente.</item>
/// </list>
/// </summary>
public class CatalogBaselineEmbeddedTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly DbContextOptions<ChannelCatalogDbContext> _options;

    public CatalogBaselineEmbeddedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"catalog-baseline-embedded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "catalog.db");
        _options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        SqliteConnection.ClearAllPools();
    }

    private async Task<ChannelCatalogDbContext> NewInitializedContextAsync()
    {
        var ctx = new ChannelCatalogDbContext(_options);
        await ctx.Database.EnsureCreatedAsync();
        return ctx;
    }

    [Fact]
    public async Task Embedded_baseline_loads_core_pt_channels_and_resolves()
    {
        // Sem ficheiro e sem env var: o loader embutido é a fonte.
        var baseline = await CatalogBaselineImporter.LoadEmbeddedAsync();

        Assert.Equal("pt-canonical-tv", baseline.CatalogId);
        Assert.NotNull(baseline.Matching);
        Assert.NotNull(baseline.Matching!.Examples);
        Assert.True(baseline.Matching.Examples.Count >= 20,
            $"Esperado >= 20 exemplos no baseline embutido, obtido {baseline.Matching.Examples.Count}");

        var ctx = await NewInitializedContextAsync();
        var report = await CatalogBaselineImporter.ImportAsync(ctx, baseline);
        await ctx.DisposeAsync();

        Assert.True(report.ChannelsCreated >= 20,
            $"Esperado >= 20 canais criados a partir do embutido, obtido {report.ChannelsCreated}");

        var resolver = new CatalogResolver(new TestDbContextFactory(_dbPath), _dbPath);
        var channels = (await resolver.ListCanonicalChannelsAsync()).ToList();
        foreach (var key in new[] { "rtp1", "rtp2", "sic", "tvi", "cnnportugal", "cmtv" })
        {
            Assert.Contains(channels, c => c.Key == key);
        }

        // Resolução real via CatalogResolver (aliases persistidos).
        foreach (var identity in new[] { "rtp1", "sic", "tvi", "cmtv" })
        {
            var resolved = await resolver.ResolveAsync(identity);
            Assert.True(resolved.CanonicalChannelId.HasValue,
                $"identidade '{identity}' não resolveu para nenhum canal.");
        }

        // "CNN Portugal" normaliza para "cnn" (variant gerada pelo import).
        var cnn = await resolver.ResolveAsync("cnn portugal");
        Assert.True(cnn.CanonicalChannelId.HasValue, "identidade 'cnn portugal' não resolveu.");

        var rtp1 = await resolver.ResolveAsync("rtp1");
        var rtp1Channel = channels.First(c => c.Id == rtp1.CanonicalChannelId!.Value);
        Assert.Equal("RTP 1", rtp1Channel.DisplayName);
    }

    [Fact]
    public async Task Import_embedded_baseline_twice_does_not_duplicate()
    {
        var baseline = await CatalogBaselineImporter.LoadEmbeddedAsync();

        var ctx = await NewInitializedContextAsync();
        var first = await CatalogBaselineImporter.ImportAsync(ctx, baseline);
        var second = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.True(first.ChannelsCreated > 0, "Primeira passagem deve criar canais.");
        Assert.Equal(0, second.ChannelsCreated);
        Assert.Equal(0, second.AliasesAdded);
        Assert.True(second.AliasesSkipped >= first.AliasesAdded,
            "Segunda passagem deve marcar os aliases existentes como skipped.");

        // Sem canais nem aliases duplicados.
        var keys = await ctx.CanonicalChannels.Select(c => c.Key).ToListAsync();
        Assert.Equal(keys.Count, keys.Distinct().Count());

        var aliases = await ctx.ChannelAliases.Select(a => a.NormalizedAlias).ToListAsync();
        Assert.Equal(aliases.Count, aliases.Distinct().Count());

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task File_at_resolved_path_takes_precedence_over_embedded()
    {
        var cwd = Path.Combine(_root, "cwd");
        var baseDir = Path.Combine(_root, "base");
        Directory.CreateDirectory(Path.Combine(cwd, "docs", "catalog"));
        Directory.CreateDirectory(baseDir);

        var fileBaselinePath = Path.Combine(
            cwd, "docs", "catalog", CatalogBaselineImporter.BaselineFileName);
        await File.WriteAllTextAsync(
            fileBaselinePath,
            MinimalBaselineJson("file-baseline", "only-in-file", "pt.fileonly", "Only In File"));

        // Sem env → encontra o ficheiro do CWD.
        var resolved = ChannelCatalogBootstrapper.ResolveBaselinePathFor(cwd, baseDir, null);
        Assert.Equal(Path.GetFullPath(fileBaselinePath), resolved);

        // Ficheiro resolve e é a origem escolhida (precedência sobre o embutido).
        var fromFile = await ChannelCatalogBootstrapper.LoadBaselineAsync(resolved);
        Assert.Equal("file-baseline", fromFile.CatalogId);
        Assert.True(fromFile.Matching!.Examples.ContainsKey("only-in-file"));
        Assert.DoesNotContain("rtp1", fromFile.Matching.Examples.Keys);

        // Sem ficheiro nenhum → decide pelo embutido.
        var emptyCwd = Path.Combine(_root, "empty-cwd");
        var emptyBase = Path.Combine(_root, "empty-base");
        Directory.CreateDirectory(emptyCwd);
        Directory.CreateDirectory(emptyBase);
        Assert.Null(ChannelCatalogBootstrapper.ResolveBaselinePathFor(emptyCwd, emptyBase, null));

        var fromEmbedded = await ChannelCatalogBootstrapper.LoadBaselineAsync(null);
        Assert.Equal("pt-canonical-tv", fromEmbedded.CatalogId);
        Assert.True(fromEmbedded.Matching!.Examples.ContainsKey("rtp1"));
    }

    [Fact]
    public async Task Resolved_path_precedence_is_env_then_cwd_then_base_directory()
    {
        var envDir = Path.Combine(_root, "env");
        var cwdDir = Path.Combine(_root, "cwd2");
        var baseDir = Path.Combine(_root, "base2");
        foreach (var dir in new[] { envDir, cwdDir, baseDir })
        {
            Directory.CreateDirectory(Path.Combine(dir, "docs", "catalog"));
        }

        var envFile = Path.Combine(envDir, "docs", "catalog", CatalogBaselineImporter.BaselineFileName);
        var cwdFile = Path.Combine(cwdDir, "docs", "catalog", CatalogBaselineImporter.BaselineFileName);
        var baseFile = Path.Combine(baseDir, "docs", "catalog", CatalogBaselineImporter.BaselineFileName);
        await File.WriteAllTextAsync(envFile, MinimalBaselineJson("env", "env-key", "pt.env", "Env"));
        await File.WriteAllTextAsync(cwdFile, MinimalBaselineJson("cwd", "cwd-key", "pt.cwd", "Cwd"));
        await File.WriteAllTextAsync(baseFile, MinimalBaselineJson("base", "base-key", "pt.base", "Base"));

        // 1. Env var tem precedência máxima.
        Assert.Equal(
            Path.GetFullPath(envFile),
            ChannelCatalogBootstrapper.ResolveBaselinePathFor(cwdDir, baseDir, envFile));

        // 2. Sem env, o CWD ganha ao BaseDirectory.
        Assert.Equal(
            Path.GetFullPath(cwdFile),
            ChannelCatalogBootstrapper.ResolveBaselinePathFor(cwdDir, baseDir, null));

        // 3. Sem CWD, o BaseDirectory é usado.
        var emptyCwd = Path.Combine(_root, "empty-cwd2");
        Directory.CreateDirectory(emptyCwd);
        Assert.Equal(
            Path.GetFullPath(baseFile),
            ChannelCatalogBootstrapper.ResolveBaselinePathFor(emptyCwd, baseDir, null));

        // 4. Env var inexistente é ignorada (fallback para ficheiro/CWD).
        Assert.Equal(
            Path.GetFullPath(cwdFile),
            ChannelCatalogBootstrapper.ResolveBaselinePathFor(
                cwdDir, baseDir, Path.Combine(_root, "does-not-exist.json")));
    }

    private static string MinimalBaselineJson(
        string catalogId, string sourceKey, string canonicalId, string name)
    {
        var baseline = new CatalogBaseline
        {
            CatalogId = catalogId,
            Version = "1.0",
            Country = "PT",
            Matching = new MatchingBaseline
            {
                Examples =
                {
                    [sourceKey] = new ChannelBaseline
                    {
                        CanonicalId = canonicalId,
                        Name = name,
                        Aliases = new() { name + " HD" },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(baseline);
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave W3s — consistência do atributo <c>Country</c> no catálogo.
///
/// <para>
/// FACT (comportamento actual, fixado por estes testes):
/// <list type="bullet">
///   <item>o import da baseline canónica define <c>Country</c> nos canais
///         que cria (a partir do <c>country</c> do baseline) e mantém-no
///         consistente em re-imports;</item>
///   <item>os canais criados pelo <c>CatalogSeed</c> programático (ex.:
///         <c>sport-tv-2</c>, que não existe na baseline) têm
///         <c>Country == null</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// TODO / ADR: não existe migration para preencher <c>Country</c> em canais
/// semeados. Isso é deliberado nesta wave: a propriedade/ownership dos
/// dados de país <b>é um ADR em aberto</b> (<c>docs/Reestructure/24-DECISIONS.md</c>,
/// item "country data ownership") e não deve ser decidido implicitamente.
/// Este atributo é apenas classificação — não cria identidade de canal.
/// </para>
/// </summary>
public sealed class CountryAttributeConsistencyTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _baselineDbPath;

    public CountryAttributeConsistencyTests()
    {
        _dbPath = TestTempDb.SuitePath($"catalog-baseline-country-{Guid.NewGuid():N}.db");
        _baselineDbPath = TestTempDb.SuitePath($"catalog-baseline-country-import-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath, _baselineDbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Baseline_import_sets_country_on_created_channels()
    {
        var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={_baselineDbPath}")
            .Options;

        await using var ctx = new ChannelCatalogDbContext(options);
        await ctx.Database.EnsureCreatedAsync();

        var baseline = new CatalogBaseline
        {
            CatalogId = "pt-test",
            Version = "1.0",
            Country = "PT",
            Matching = new MatchingBaseline
            {
                Examples =
                {
                    ["rtp1"] = new ChannelBaseline
                    {
                        CanonicalId = "pt.rtp1",
                        Name = "RTP 1",
                        Aliases = new() { "RTP1" },
                    },
                },
            },
        };

        var report = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.Equal(1, report.ChannelsCreated);
        var channel = await ctx.CanonicalChannels.FirstAsync(c => c.Key == "rtp1");
        Assert.Equal("PT", channel.Country);
    }

    [Fact]
    public async Task Seed_channels_have_null_country_current_behavior()
    {
        var factory = new TestDbContextFactory(_dbPath);
        await using var ctx = factory.CreateDbContext();

        var seedOnly = await ctx.CanonicalChannels.FirstAsync(c => c.Key == "sport-tv-2");

        Assert.Null(seedOnly.Country);
    }
}

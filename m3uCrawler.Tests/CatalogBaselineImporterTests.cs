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
/// Testes para <see cref="CatalogBaselineImporter"/>. Foco em:
///
/// <list type="bullet">
///   <item>Determinismo da conversão de <c>canonical_id</c> →
///         <c>Key</c>.</item>
///   <item>Resolução de grupo editorial / categoria a partir do
///         baseline.</item>
///   <item>Idempotência do import (segunda chamada não cria
///         duplicados).</item>
///   <item>Não destrutividade: canais pré-existentes não são
///         removidos.</item>
///   <item>Sanitização: sem URLs nem credenciais no report.</item>
/// </list>
/// </summary>
public class CatalogBaselineImporterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<ChannelCatalogDbContext> _options;

    public CatalogBaselineImporterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"catalog-baseline-test-{Guid.NewGuid():N}.db");
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=Memory;Cache=Shared");
        // In-memory via nova connection string (mais portátil).
        _options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* ignore */ }
        SqliteConnection.ClearAllPools();
    }

    private ChannelCatalogDbContext NewContext() => new(_options);

    [Fact]
    public void CanonicalIdToKey_strips_country_prefix()
    {
        Assert.Equal("rtp1", CatalogBaselineImporter.CanonicalIdToKey("pt.rtp1"));
        Assert.Equal("rtp2", CatalogBaselineImporter.CanonicalIdToKey("pt.rtp2"));
        Assert.Equal("benficatv", CatalogBaselineImporter.CanonicalIdToKey("pt.benficatv"));
    }

    [Fact]
    public void CanonicalIdToKey_joins_multi_segment_with_hyphen()
    {
        Assert.Equal("sic-k", CatalogBaselineImporter.CanonicalIdToKey("pt.sic.k"));
        Assert.Equal("sport-tv-nba", CatalogBaselineImporter.CanonicalIdToKey("pt.sport.tv.nba"));
    }

    [Fact]
    public void CanonicalIdToKey_preserves_no_prefix_as_is()
    {
        Assert.Equal("rtp1", CatalogBaselineImporter.CanonicalIdToKey("rtp1"));
        Assert.Equal("foo-bar-baz", CatalogBaselineImporter.CanonicalIdToKey("foo.bar.baz"));
    }

    [Fact]
    public void ResolveEditorialGroup_maps_known_groups()
    {
        Assert.Equal(CanonicalEditorialGroup.PortugalLive, CatalogBaselineImporter.ResolveEditorialGroup("pt-generalistas"));
        Assert.Equal(CanonicalEditorialGroup.PortugalDesporto, CatalogBaselineImporter.ResolveEditorialGroup("pt-desporto"));
        Assert.Equal(CanonicalEditorialGroup.PortugalInfantil, CatalogBaselineImporter.ResolveEditorialGroup("pt-infantil"));
        Assert.Equal(CanonicalEditorialGroup.PortugalDocumentarios, CatalogBaselineImporter.ResolveEditorialGroup("pt-documentarios"));
        Assert.Equal(CanonicalEditorialGroup.PortugalFilmes24_7, CatalogBaselineImporter.ResolveEditorialGroup("pt-filmes-series"));
        Assert.Equal(CanonicalEditorialGroup.PortugalEntretenimento, CatalogBaselineImporter.ResolveEditorialGroup("pt-entretenimento"));
        Assert.Equal(CanonicalEditorialGroup.Foreign, CatalogBaselineImporter.ResolveEditorialGroup("international-news"));
        Assert.Equal(CanonicalEditorialGroup.PortugalPPV, CatalogBaselineImporter.ResolveEditorialGroup("adultos"));
        Assert.Equal(CanonicalEditorialGroup.PortugalFilmes24_7, CatalogBaselineImporter.ResolveEditorialGroup("vod-filmes"));
    }

    [Fact]
    public void ResolveEditorialGroup_unknown_defaults_to_Other()
    {
        Assert.Equal(CanonicalEditorialGroup.Other, CatalogBaselineImporter.ResolveEditorialGroup(null));
        Assert.Equal(CanonicalEditorialGroup.Other, CatalogBaselineImporter.ResolveEditorialGroup(""));
        Assert.Equal(CanonicalEditorialGroup.Other, CatalogBaselineImporter.ResolveEditorialGroup("xyz"));
    }

    [Fact]
    public void ResolveCategoryFromGroupId_maps_desporto()
    {
        Assert.Equal(EditorialCategory.Desporto, CatalogBaselineImporter.ResolveCategoryFromGroupId("pt-desporto"));
        Assert.Equal(EditorialCategory.Infantil, CatalogBaselineImporter.ResolveCategoryFromGroupId("pt-infantil"));
        Assert.Equal(EditorialCategory.Documentarios, CatalogBaselineImporter.ResolveCategoryFromGroupId("pt-documentarios"));
        Assert.Equal(EditorialCategory.Live, CatalogBaselineImporter.ResolveCategoryFromGroupId("pt-generalistas"));
        Assert.Equal(EditorialCategory.Live, CatalogBaselineImporter.ResolveCategoryFromGroupId(null));
    }

    [Fact]
    public async Task LoadFromFileAsync_throws_on_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".json");
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await CatalogBaselineImporter.LoadFromFileAsync(missing));
    }

    [Fact]
    public async Task LoadFromFileAsync_round_trips_real_baseline()
    {
        // Carrega o ficheiro versionado para confirmar que o
        // schema actual é compatível com os DTOs.
        var baselinePath = LocateBaselineFile();
        var baseline = await CatalogBaselineImporter.LoadFromFileAsync(baselinePath);
        Assert.False(string.IsNullOrEmpty(baseline.CatalogId));
        Assert.False(string.IsNullOrEmpty(baseline.Country));
        Assert.NotEmpty(baseline.Matching.Examples);
    }

    [Fact]
    public async Task ImportAsync_creates_channels_and_aliases()
    {
        await using var ctx = NewContext();
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
                        Aliases = new() { "RTP1", "RTP 1 HD", "rtp1hd" },
                    },
                    ["sic"] = new ChannelBaseline
                    {
                        CanonicalId = "pt.sic",
                        Name = "SIC",
                        Aliases = new() { "SIC HD" },
                    },
                },
            },
        };

        var report = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.Equal(2, report.ChannelsCreated);
        Assert.Equal(0, report.ChannelsUpdated);
        // 6 aliases:
        //   RTP 1: "rtp 1" (auto from DisplayName) + "rtp1", "rtp 1 hd", "rtp1hd" = 4
        //   SIC:   "sic"   (auto from DisplayName) + "sic hd" = 2
        // O alias principal é persistido automaticamente para
        // garantir que o matcher resolve títulos canónicos.
        Assert.Equal(6, report.AliasesAdded);

        var rtp1 = await ctx.CanonicalChannels.Include(c => c.Aliases).FirstAsync(c => c.Key == "rtp1");
        Assert.Equal("RTP 1", rtp1.DisplayName);
        Assert.Equal(PublicationPolicy.CreateEligible, rtp1.PublicationPolicy);
        Assert.True(rtp1.IsEnabled);
        // rtp1 has 4 aliases: "rtp 1" (auto), "rtp1", "rtp 1 hd", "rtp1hd"
        Assert.Equal(4, rtp1.Aliases.Count);
        Assert.Contains(rtp1.Aliases, a => a.NormalizedAlias == "rtp 1");
        Assert.Contains(rtp1.Aliases, a => a.NormalizedAlias == "rtp 1 hd");
    }

    [Fact]
    public async Task ImportAsync_persists_alias_identical_to_channel_name()
    {
        // "SIC" como alias de um canal cujo DisplayName é "SIC" NÃO
        // é redundante — é exactamente o alias que permite ao matcher
        // resolver "SIC" como título. Tem de ser persistido.
        await using var ctx = NewContext();
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
                    ["sic"] = new ChannelBaseline
                    {
                        CanonicalId = "pt.sic",
                        Name = "SIC",
                        Aliases = new() { "SIC", "SIC HD" },
                    },
                },
            },
        };

        var report = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.Equal(1, report.ChannelsCreated);
        Assert.Equal(2, report.AliasesAdded); // SIC e SIC HD
        Assert.Equal(0, report.AliasesSkipped);

        var sic = await ctx.CanonicalChannels.Include(c => c.Aliases).FirstAsync();
        var aliases = sic.Aliases.Select(a => a.NormalizedAlias).ToHashSet();
        Assert.Contains("sic", aliases);
        Assert.Contains("sic hd", aliases);
        Assert.Equal(2, aliases.Count);
    }

    [Fact]
    public async Task ImportAsync_is_idempotent()
    {
        await using var ctx = NewContext();
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

        var first = await CatalogBaselineImporter.ImportAsync(ctx, baseline);
        var second = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.Equal(1, first.ChannelsCreated);
        Assert.Equal(0, first.AliasesSkipped); // primeira vez: nenhum conflito
        // Segunda passagem: nada a criar.
        Assert.Equal(0, second.ChannelsCreated);
        Assert.Equal(0, second.AliasesAdded);
        Assert.True(second.AliasesSkipped >= 1); // alias já existe

        // Garantir que a base de dados só tem UM canal rtp1.
        var count = await ctx.CanonicalChannels.CountAsync(c => c.Key == "rtp1");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ImportAsync_preserves_pre_existing_channel_not_in_baseline()
    {
        // Garante que importar um baseline diferente NÃO elimina
        // canais pré-existentes (aditividade).
        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();

        var preExisting = new CanonicalChannelEntity
        {
            Key = "pre-existing",
            DisplayName = "Pre Existing",
            EditorialCategory = EditorialCategory.Live,
            EditorialGroup = CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy = PublicationPolicy.CreateEligible,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        ctx.CanonicalChannels.Add(preExisting);
        await ctx.SaveChangesAsync();

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
        await CatalogBaselineImporter.ImportAsync(ctx, baseline);
        var all = await ctx.CanonicalChannels.ToListAsync();
        Assert.Contains(all, c => c.Key == "pre-existing");
        Assert.Contains(all, c => c.Key == "rtp1");
    }

    [Fact]
    public async Task ImportAsync_updates_display_name_when_baseline_changes()
    {
        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();

        var baseline1 = new CatalogBaseline
        {
            CatalogId = "pt",
            Version = "1.0",
            Country = "PT",
            Matching = new MatchingBaseline
            {
                Examples =
                {
                    ["rtp1"] = new ChannelBaseline
                    {
                        CanonicalId = "pt.rtp1",
                        Name = "RTP 1 (legacy)",
                        Aliases = new() { "RTP1" },
                    },
                },
            },
        };
        await CatalogBaselineImporter.ImportAsync(ctx, baseline1);

        var baseline2 = new CatalogBaseline
        {
            CatalogId = "pt",
            Version = "2.0",
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
        var report2 = await CatalogBaselineImporter.ImportAsync(ctx, baseline2);

        Assert.Equal(0, report2.ChannelsCreated);
        Assert.Equal(1, report2.ChannelsUpdated);

        var rtp1 = await ctx.CanonicalChannels.FirstAsync(c => c.Key == "rtp1");
        Assert.Equal("RTP 1", rtp1.DisplayName);
    }

    [Fact]
    public async Task ImportAsync_report_contains_no_urls_or_credentials()
    {
        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();

        var baseline = new CatalogBaseline
        {
            CatalogId = "pt",
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

        var text = report.Warnings.Count > 0
            ? string.Join("\n", report.Warnings)
            : string.Empty;
        Assert.DoesNotContain("http://", text);
        Assert.DoesNotContain("https://", text);
        Assert.DoesNotContain("username=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password=", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportAsync_with_empty_examples_returns_empty_report()
    {
        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();

        var baseline = new CatalogBaseline
        {
            CatalogId = "pt",
            Version = "1.0",
            Country = "PT",
            Matching = new MatchingBaseline(),
        };

        var report = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.Equal(0, report.ChannelsCreated);
        Assert.Equal(0, report.AliasesAdded);
        Assert.Contains(report.Warnings, w => w.Contains("matching.examples"));
    }

    [Fact]
    public async Task ImportAsync_real_baseline_creates_distinct_channels()
    {
        // Integração: importar o ficheiro real versionado e
        // validar contadores esperados.
        var baselinePath = LocateBaselineFile();
        if (!File.Exists(baselinePath)) return; // skip se não existir

        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();

        var baseline = await CatalogBaselineImporter.LoadFromFileAsync(baselinePath);
        var report = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.NotEmpty(baseline.Matching.Examples);
        Assert.True(report.ChannelsCreated >= baseline.Matching.Examples.Count - 5, // tolera aliases inválidas/duplicadas
            $"Esperado >= {baseline.Matching.Examples.Count - 5} canais criados, obtido {report.ChannelsCreated}");

        // Todos os canais criados devem ter Key lowercase, sem pontos,
        // sem prefixo país (e.g. "rtp-1", "benfica-tv").
        var channels = await ctx.CanonicalChannels.ToListAsync();
        Assert.All(channels, c =>
        {
            Assert.Equal(c.Key.ToLowerInvariant(), c.Key);
            Assert.DoesNotContain(".", c.Key);
            Assert.False(c.Key.StartsWith("pt-"));
        });
    }

    [Fact]
    public void Baseline_json_schema_matches_dto_round_trip()
    {
        // Round-trip mínimo: serializar um baseline mínimo, validar
        // que o JSON pode ser deserializado de volta para o mesmo DTO.
        var baseline = new CatalogBaseline
        {
            CatalogId = "test",
            Version = "1.0",
            Country = "PT",
            Matching = new MatchingBaseline
            {
                Examples =
                {
                    ["x"] = new ChannelBaseline
                    {
                        CanonicalId = "pt.x",
                        Name = "X",
                        Aliases = new() { "X HD" },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(baseline);
        var roundtrip = JsonSerializer.Deserialize<CatalogBaseline>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(roundtrip);
        Assert.Equal(baseline.CatalogId, roundtrip!.CatalogId);
        Assert.Equal(baseline.Matching.Examples["x"].CanonicalId, roundtrip.Matching.Examples["x"].CanonicalId);
        Assert.Equal(baseline.Matching.Examples["x"].Aliases[0], roundtrip.Matching.Examples["x"].Aliases[0]);
    }

    [Fact]
    public async Task BaselineImport_with_real_file_populates_distinct_channels()
    {
        // Verifica que, quando o baseline real é carregado,
        // produz um número razoável de canais distintos (e.g.
        // o baseline PT define RTP1, SIC, TVI, Sport TV, BTV,
        // Disney, etc.). Não verificamos contagens exactas para
        // permitir evolução do JSON sem quebrar o teste.
        var baselinePath = LocateBaselineFile();
        if (!File.Exists(baselinePath)) return;

        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();
        var baseline = await CatalogBaselineImporter.LoadFromFileAsync(baselinePath);
        var report = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        // O baseline define canais de Generalistas (RTP1, SIC,
        // TVI, CNN Portugal), Desporto (Sport TV 1..7, BTV),
        // Infantil, Filmes, Documentários. Verificamos que os
        // canais mínimos esperados foram criados.
        var expectedKeys = new[] { "rtp1", "rtp2", "sic", "tvi", "sporttv1", "btv" };
        var channels = await ctx.CanonicalChannels.ToListAsync();
        foreach (var key in expectedKeys)
        {
            Assert.Contains(channels, c => c.Key == key);
        }

        // Relatório coerente.
        Assert.True(report.ChannelsCreated >= expectedKeys.Length,
            $"Esperado >= {expectedKeys.Length} canais criados, obtido {report.ChannelsCreated}");
        Assert.True(report.AliasesAdded >= 5,
            $"Esperado >= 5 aliases adicionados, obtido {report.AliasesAdded}");
    }
    [Fact]
    public async Task BaselineImport_two_runs_are_idempotent_against_real_file()
    {
        // Verifica que executar a importação duas vezes não duplica
        // canais nem aliases (apesar de o JSON conter múltiplos).
        var baselinePath = LocateBaselineFile();
        if (!File.Exists(baselinePath)) return;

        await using var ctx = NewContext();
        await ctx.Database.EnsureCreatedAsync();

        var baseline = await CatalogBaselineImporter.LoadFromFileAsync(baselinePath);
        var report1 = await CatalogBaselineImporter.ImportAsync(ctx, baseline);
        var report2 = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.True(report1.ChannelsCreated > 0, "Primeira passagem deve criar canais.");
        Assert.Equal(0, report2.ChannelsCreated);
        Assert.Equal(0, report2.AliasesAdded);
        Assert.True(report2.AliasesSkipped >= report1.AliasesAdded,
            "Segunda passagem deve marcar aliases existentes como skipped.");
    }

    private static string LocateBaselineFile()
    {
        // bin/Debug|Release/net9.0/ — subir 5 níveis para chegar
        // à raiz do repo: net9.0 → Release → bin → m3uCrawler.Tests →
        // m3uCrawler → repo root.
        var dir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            dir, "..", "..", "..", "..", "..", "docs", "catalog",
            "m3ucrawler_pt_canonical_catalog.json"));
        if (File.Exists(candidate)) return candidate;

        // Fallback: tentar 4 níveis (caso a estrutura mude).
        candidate = Path.GetFullPath(Path.Combine(
            dir, "..", "..", "..", "..", "docs", "catalog",
            "m3ucrawler_pt_canonical_catalog.json"));
        return candidate;
    }
}

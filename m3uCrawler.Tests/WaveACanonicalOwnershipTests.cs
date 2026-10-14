using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave A — invariantes de ownership das DUAS listas geridas
/// exclusivamente pelo utilizador:
/// <list type="number">
///   <item>a configuração de canais por país
///         (<c>runtime-data/countries/&lt;code&gt;.json</c>);</item>
///   <item>a lista de canais canónicos (<c>canonical_channels</c> +
///         <c>channel_aliases</c> + grupos canónicos).</item>
/// </list>
///
/// <para>
/// Nenhum processo automático as cria/edita/apaga. O arranque
/// (<see cref="ChannelCatalogBootstrapper.InitializeAsync"/>) aplica
/// apenas migrations e não semeia canais nem importa a baseline; a
/// provisão é explícita, pelos comandos CLI
/// <c>--import-canonical-baseline</c> / <c>--import-country-config</c>.
/// Nenhum grupo canónico é criado automaticamente (nem pela migration, nem
/// pelo import): o import cria apenas os grupos que o próprio ficheiro
/// declarar.
/// </para>
///
/// <para>
/// Estes testes provam que: (a) o arranque não cria canais nem grupos;
/// (b) apagar persiste após reinicialização; (c) os comandos CLI criam a
/// partir de ficheiros fornecidos e são idempotentes; (d) os caminhos de
/// erro do CLI não ecoam conteúdo e devolvem códigos coerentes; (e) o
/// código de país é validado contra path traversal.
/// </para>
/// </summary>
public sealed class WaveACanonicalOwnershipTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public WaveACanonicalOwnershipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wave-a-ownership-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "channel-catalog.db");
    }

    public void Dispose()
    {
        TestTempDb.Cleanup(_dbPath);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static async Task ReinitializeAsync(string dbPath)
    {
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        await using var ctx = await bootstrapper.InitializeAsync();
    }

    [Fact]
    public async Task Fresh_initialize_creates_no_canonical_channels_aliases_or_groups()
    {
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using var ctx = await bootstrapper.InitializeAsync();

        Assert.False(await ctx.CanonicalChannels.AnyAsync());
        Assert.False(await ctx.ChannelAliases.AnyAsync());
        // H1 — a migration já não semeia os 9 grupos canónicos.
        Assert.False(await ctx.CanonicalGroups.AnyAsync());
    }

    [Fact]
    public async Task Deleted_canonical_channel_is_not_recreated_by_reinitialize()
    {
        await ReinitializeAsync(_dbPath);

        var factory = new TestDbContextFactory(_dbPath);
        var resolver = new CatalogResolver(factory, _dbPath);
        var channel = await resolver.CreateCanonicalChannelAsync(
            "user-channel", "User Channel", EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: new List<string> { "user channel" });

        Assert.True(await resolver.DeleteCanonicalChannelAsync(channel.Id));

        // Simula restart: o arranque não pode recriar o que foi apagado.
        await ReinitializeAsync(_dbPath);

        var channels = await resolver.ListCanonicalChannelsAsync();
        Assert.DoesNotContain(channels, c => c.Key == "user-channel");

        await using var ctx = factory.CreateDbContext();
        Assert.False(await ctx.ChannelAliases.AnyAsync(a => a.NormalizedAlias == "user channel"));
    }

    [Fact]
    public async Task Import_canonical_baseline_cli_creates_from_file_and_is_idempotent()
    {
        // O ficheiro declara um grupo próprio ("zz-custom"): só esse deve
        // ser criado. Nenhum dos 9 grupos PT por omissão é imposto.
        var baselinePath = Path.Combine(_root, "user-baseline.json");
        await File.WriteAllTextAsync(baselinePath,
            """
            {
              "catalog_id": "user-baseline",
              "version": "1.0",
              "country": "zz",
              "groups": [
                { "id": "zz-custom", "name": "ZZ Custom", "range": [ 1, 5 ] }
              ],
              "matching": {
                "examples": {
                  "userrtp1": {
                    "canonical_id": "zz.userrtp1",
                    "name": "User RTP 1",
                    "aliases": [ "User RTP1" ]
                  }
                }
              }
            }
            """);

        var args = new[] { "--import-canonical-baseline", baselinePath, "--catalog-db", _dbPath };
        Assert.Equal(0, await Program.RunImportCanonicalBaselineAsync(args));

        await using (var ctx = new TestDbContextFactory(_dbPath).CreateDbContext())
        {
            var groups = await ctx.CanonicalGroups.ToListAsync();
            Assert.Single(groups);
            Assert.Equal("zz-custom", groups[0].Key);
            Assert.Equal("ZZ Custom", groups[0].DisplayName);

            // Nenhum dos grupos por omissão PT foi criado.
            foreach (var def in CanonicalGroupDefaults.All)
            {
                Assert.DoesNotContain(groups, g => g.Key == def.Key);
            }

            // O canal existe; a sua chave resolvida não foi declarada no
            // ficheiro, pelo que fica com GroupId null.
            var channel = await ctx.CanonicalChannels.SingleAsync(c => c.Key == "userrtp1");
            Assert.Null(channel.GroupId);
        }

        var resolver = new CatalogResolver(new TestDbContextFactory(_dbPath), _dbPath);
        var first = await resolver.ListCanonicalChannelsAsync();
        Assert.Contains(first, c => c.Key == "userrtp1" && c.DisplayName == "User RTP 1");

        // Idempotente: segunda importação não cria canais, aliases nem grupos.
        Assert.Equal(0, await Program.RunImportCanonicalBaselineAsync(args));
        var second = await resolver.ListCanonicalChannelsAsync();
        Assert.Equal(first.Count, second.Count);
        Assert.Single(second, c => c.Key == "userrtp1");
        await using (var ctx = new TestDbContextFactory(_dbPath).CreateDbContext())
        {
            Assert.Equal(1, await ctx.CanonicalGroups.CountAsync());
        }
    }

    [Fact]
    public async Task Import_canonical_baseline_maps_channels_only_to_declared_groups()
    {
        // O ficheiro declara o grupo "pt-desporto": o canal cujo grupo
        // resolvido é esse deve mapear para o grupo criado. Os restantes 8
        // grupos por omissão NÃO são criados.
        var baselinePath = Path.Combine(_root, "desporto-baseline.json");
        await File.WriteAllTextAsync(baselinePath,
            """
            {
              "catalog_id": "desporto-baseline",
              "version": "1.0",
              "country": "zz",
              "groups": [
                { "id": "pt-desporto", "name": "ZZ Desporto", "range": [ 1, 10 ] }
              ],
              "matching": {
                "examples": {
                  "userdesporto": {
                    "canonical_id": "zz.userdesporto",
                    "name": "User Desporto",
                    "aliases": [ "Desporto" ]
                  }
                }
              }
            }
            """);

        var args = new[] { "--import-canonical-baseline", baselinePath, "--catalog-db", _dbPath };
        Assert.Equal(0, await Program.RunImportCanonicalBaselineAsync(args));

        await using var ctx = new TestDbContextFactory(_dbPath).CreateDbContext();
        var group = await ctx.CanonicalGroups.SingleAsync(g => g.Key == "pt-desporto");
        var channel = await ctx.CanonicalChannels.SingleAsync(c => c.Key == "userdesporto");
        Assert.Equal(group.Id, channel.GroupId);

        // Além do grupo declarado, nenhum outro grupo por omissão existe.
        var groupCount = await ctx.CanonicalGroups.CountAsync();
        Assert.Equal(1, groupCount);
        foreach (var def in CanonicalGroupDefaults.All.Where(d => d.Key != "pt-desporto"))
        {
            Assert.False(await ctx.CanonicalGroups.AnyAsync(g => g.Key == def.Key));
        }
    }

    [Fact]
    public async Task Import_canonical_baseline_without_groups_creates_none()
    {
        var baselinePath = Path.Combine(_root, "nogroups-baseline.json");
        await File.WriteAllTextAsync(baselinePath,
            """
            {
              "catalog_id": "nogroups-baseline",
              "version": "1.0",
              "country": "zz",
              "matching": {
                "examples": {
                  "usercanal": {
                    "canonical_id": "zz.usercanal",
                    "name": "User Canal",
                    "aliases": [ "User Canal" ]
                  }
                }
              }
            }
            """);

        Assert.Equal(0, await Program.RunImportCanonicalBaselineAsync(
            new[] { "--import-canonical-baseline", baselinePath, "--catalog-db", _dbPath }));

        await using var ctx = new TestDbContextFactory(_dbPath).CreateDbContext();
        Assert.False(await ctx.CanonicalGroups.AnyAsync());
        var channel = await ctx.CanonicalChannels.SingleAsync(c => c.Key == "usercanal");
        Assert.Null(channel.GroupId);
    }

    [Fact]
    public async Task Import_country_config_cli_writes_from_file_is_idempotent_and_delete_persists()
    {
        var countryDir = Path.Combine(_root, "countries");
        var fixturePath = Path.Combine(_root, "user-country.json");
        await File.WriteAllTextAsync(fixturePath,
            """
            { "country": "zz", "displayName": "Testland", "channels": [ "Alpha", "Beta", "alpha" ] }
            """);

        Assert.Equal(0, await Program.RunImportCountryConfigAsync(fixturePath, countryDir));

        var target = Path.Combine(countryDir, "zz.json");
        Assert.True(File.Exists(target));

        var service = new CountryChannelListService(countryDir);
        var saved = service.GetCountry("zz");
        Assert.Equal("Testland", saved.DisplayName);
        // SaveCountry: distinct (ignore-case) + ordenação (ignore-case).
        Assert.Equal(new[] { "Alpha", "Beta" }, saved.Channels);
        Assert.True(CountryConfigProvisioner.IsCountryDataAvailable(countryDir, "zz"));

        // Idempotente: reimportar produz exactamente o mesmo ficheiro.
        var firstBytes = File.ReadAllBytes(target);
        Assert.Equal(0, await Program.RunImportCountryConfigAsync(fixturePath, countryDir));
        Assert.Equal(firstBytes, File.ReadAllBytes(target));

        // Apagar persiste: nenhum arranque recria o ficheiro de país.
        Assert.True(service.DeleteCountry("zz"));
        Assert.False(File.Exists(target));
        await ReinitializeAsync(_dbPath);
        Assert.False(File.Exists(target));
        Assert.False(CountryConfigProvisioner.IsCountryDataAvailable(countryDir, "zz"));
    }

    // =====================================================================
    // L10 — caminhos de erro do CLI: códigos coerentes e sem eco de conteúdo
    // =====================================================================

    [Fact]
    public async Task Import_canonical_baseline_missing_file_returns_4()
    {
        var missing = Path.Combine(_root, "does-not-exist.json");
        var (code, output) = await CaptureAsync(() => Program.RunImportCanonicalBaselineAsync(
            new[] { "--import-canonical-baseline", missing, "--catalog-db", _dbPath }));

        Assert.Equal(4, code);
        Assert.Contains("não encontrado", output);
    }

    [Fact]
    public async Task Import_canonical_baseline_malformed_json_returns_4_without_echoing_content()
    {
        const string marker = "SECRET_MARKER_BASELINE";
        var badPath = Path.Combine(_root, "malformed-baseline.json");
        await File.WriteAllTextAsync(badPath, $"{{ \"catalog_id\": \"{marker}\", ");

        var (code, output) = await CaptureAsync(() => Program.RunImportCanonicalBaselineAsync(
            new[] { "--import-canonical-baseline", badPath, "--catalog-db", _dbPath }));

        Assert.Equal(4, code);
        Assert.DoesNotContain(marker, output);
    }

    [Fact]
    public async Task Import_country_config_missing_file_returns_4()
    {
        var countryDir = Path.Combine(_root, "countries-missing");
        var missing = Path.Combine(_root, "no-country.json");
        var (code, output) = await CaptureAsync(() => Program.RunImportCountryConfigAsync(
            missing, countryDir));

        Assert.Equal(4, code);
        Assert.Contains("não encontrado", output);
    }

    [Fact]
    public async Task Import_country_config_malformed_json_returns_4_without_echoing_content()
    {
        const string marker = "SECRET_MARKER_COUNTRY";
        var countryDir = Path.Combine(_root, "countries-malformed");
        var badPath = Path.Combine(_root, "malformed-country.json");
        await File.WriteAllTextAsync(badPath, $"{{ \"country\": \"{marker}\", ");

        var (code, output) = await CaptureAsync(() => Program.RunImportCountryConfigAsync(
            badPath, countryDir));

        Assert.Equal(4, code);
        Assert.DoesNotContain(marker, output);
        Assert.False(Directory.Exists(countryDir));
    }

    // =====================================================================
    // L11 — validação do código de país (path traversal)
    // =====================================================================

    [Theory]
    [InlineData("../../x")]
    [InlineData("..\\..\\x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("a b")]
    [InlineData("pt.json")]
    public void SaveCountry_rejects_unsafe_country_codes(string code)
    {
        var countryDir = Path.Combine(_root, "countries-traversal");
        var service = new CountryChannelListService(countryDir);

        Assert.False(CountryChannelListService.IsValidCountryCode(code));
        Assert.Throws<ArgumentException>(() => service.SaveCountry(new CountryChannelList
        {
            Country = code,
            DisplayName = "Evil",
            Channels = new List<string> { "Alpha" },
        }));

        // Nada escrito dentro nem fora da pasta configurada.
        Assert.False(Directory.Exists(countryDir));
        Assert.False(File.Exists(Path.Combine(_root, "x.json")));
    }

    [Theory]
    [InlineData("../../x")]
    [InlineData("a\\b")]
    [InlineData("..")]
    public void DeleteCountry_rejects_unsafe_country_codes(string code)
    {
        var countryDir = Path.Combine(_root, "countries-delete");
        Directory.CreateDirectory(countryDir);
        var outside = Path.Combine(_root, "outside.json");
        File.WriteAllText(outside, "{}");

        var service = new CountryChannelListService(countryDir);
        Assert.False(service.DeleteCountry(code));
        // O ficheiro fora da pasta nunca é eliminado nem tocado.
        Assert.True(File.Exists(outside));
    }

    [Theory]
    [InlineData("../../x")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void GetCountry_rejects_unsafe_country_codes(string code)
    {
        var service = new CountryChannelListService(Path.Combine(_root, "countries-get"));
        Assert.Throws<ArgumentException>(() => service.GetCountry(code));
    }

    [Theory]
    [InlineData("pt")]
    [InlineData("es")]
    [InlineData("br")]
    [InlineData("zz")]
    [InlineData("pt-1")]
    [InlineData("a1b2c3d4e5")]
    public void IsValidCountryCode_accepts_safe_codes(string code)
    {
        Assert.True(CountryChannelListService.IsValidCountryCode(code));
    }

    [Fact]
    public async Task Normalize_canonical_aliases_cli_normalizes_legacy_alias()
    {
        // Provisiona o catálogo e insere um alias legacy (forma crua).
        await ReinitializeAsync(_dbPath);
        await using (var ctx = new TestDbContextFactory(_dbPath).CreateDbContext())
        {
            var now = DateTime.UtcNow;
            var channel = new CanonicalChannelEntity
            {
                Key = "norm-cli",
                DisplayName = "Norm Cli",
                EditorialCategory = EditorialCategory.Live,
                GroupId = null,
                PublicationPolicy = PublicationPolicy.CreateEligible,
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            ctx.CanonicalChannels.Add(channel);
            await ctx.SaveChangesAsync();
            ctx.ChannelAliases.Add(new ChannelAliasEntity
            {
                NormalizedAlias = "LEGACY CLI HD",
                CanonicalChannelId = channel.Id,
                CreatedAtUtc = now,
            });
            await ctx.SaveChangesAsync();
        }

        var (code, output) = await CaptureAsync(() => Program.RunNormalizeCanonicalAliasesAsync(
            new[] { "--normalize-canonical-aliases", "--catalog-db", _dbPath }));

        Assert.Equal(0, code);
        Assert.Contains("Normalização de aliases", output);

        await using (var ctx = new TestDbContextFactory(_dbPath).CreateDbContext())
        {
            var channelId = await ctx.CanonicalChannels
                .Where(c => c.Key == "norm-cli")
                .Select(c => c.Id)
                .SingleAsync();
            var aliases = await ctx.ChannelAliases
                .Where(a => a.CanonicalChannelId == channelId)
                .Select(a => a.NormalizedAlias)
                .ToListAsync();
            Assert.Contains("legacy cli", aliases);
            Assert.DoesNotContain("LEGACY CLI HD", aliases);
        }
    }

    private static async Task<(int ExitCode, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var sb = new StringBuilder();
        var writer = new StringWriter(sb);
        Console.SetOut(writer);
        Console.SetError(writer);
        try
        {
            var code = await action();
            return (code, sb.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}

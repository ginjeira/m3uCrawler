using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using m3uCrawler;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes de integração do comando CLI
/// <c>--import-epg-tvg-ids</c>: catálogo temporário + fixture XMLTV local
/// (sem rede), verificação da criação das identidades externas
/// (<c>tvg-id</c>), idempotência e não-sobreposição de mapeamentos
/// existentes.
/// </summary>
public class EpgTvgIdImportCliTests : IDisposable
{
    private readonly List<string> _dbPaths = new();
    private readonly List<string> _files = new();

    public void Dispose()
    {
        // TestTempDb.Cleanup fecha pools SQLite e apaga o ficheiro + sidecars
        // (-wal/-shm). Os nomes usam o prefixo "channel-catalog-", pelo que
        // eventuais sobras são também varridas pelo sweeper da suite.
        TestTempDb.Cleanup(_dbPaths.ToArray());
        TestTempDb.Cleanup(_files.ToArray());
    }

    [Fact]
    public async Task Import_creates_tvg_id_and_second_run_is_idempotent()
    {
        var dbPath = TempDb();
        var resolver = await NewResolverAsync(dbPath);
        var token = Token();
        var key = "epgimp" + token;

        var channel = await resolver.CreateCanonicalChannelAsync(
            key, "EpgImp" + token, EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>(), country: "pt");

        var xmlPath = TempFile(Xmltv(key + ".pt"), gzip: false);

        var first = await RunCaptureAsync(dbPath, xmlPath, "pt");
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("Identidades tvg-id: 1 criada", first.Output);
        Assert.Equal(key + ".pt", await resolver.GetCanonicalTvgIdAsync(channel.Id));

        var second = await RunCaptureAsync(dbPath, xmlPath, "pt");
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("Identidades tvg-id: 0 criada", second.Output);
        Assert.Contains("1 já existente", second.Output);
        Assert.Equal(key + ".pt", await resolver.GetCanonicalTvgIdAsync(channel.Id));
    }

    [Fact]
    public async Task Import_does_not_overwrite_identity_owned_by_another_channel()
    {
        var dbPath = TempDb();
        var resolver = await NewResolverAsync(dbPath);
        var token = Token();

        var target = await resolver.CreateCanonicalChannelAsync(
            "epgimpa" + token, "EpgImpA" + token, EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>(), country: "pt");

        // Canal B já detém o valor EPG (estado do operador/runtime).
        var owner = await resolver.CreateCanonicalChannelAsync(
            "epgimpb" + token, "EpgImpB" + token, EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>(), country: "pt");

        var epgId = "epgimpa" + token + ".pt";
        var seeded = await resolver.RecordExternalIdentityAsync(
            owner.Id, providerId: null, @namespace: ExternalIdentityNamespaces.TvgId,
            rawValue: epgId, origin: "operator", confidence: 1.0);
        Assert.Equal(RecordExternalIdentityOutcome.Created, seeded);

        var xmlPath = TempFile(Xmltv(epgId), gzip: false);
        var result = await RunCaptureAsync(dbPath, xmlPath, "pt");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1 conflito", result.Output);
        // O dono mantém-se; o alvo não recebe nada.
        Assert.Equal(epgId, await resolver.GetCanonicalTvgIdAsync(owner.Id));
        Assert.Null(await resolver.GetCanonicalTvgIdAsync(target.Id));
    }

    [Fact]
    public async Task Import_reads_gzip_file()
    {
        var dbPath = TempDb();
        var resolver = await NewResolverAsync(dbPath);
        var token = Token();
        var key = "epgimpgz" + token;

        var channel = await resolver.CreateCanonicalChannelAsync(
            key, "EpgImpGz" + token, EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>(), country: "pt");

        var gzPath = TempFile(Xmltv(key + ".pt"), gzip: true);
        var result = await RunCaptureAsync(dbPath, gzPath, "pt");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Identidades tvg-id: 1 criada", result.Output);
        Assert.Equal(key + ".pt", await resolver.GetCanonicalTvgIdAsync(channel.Id));
    }

    [Fact]
    public async Task Import_without_source_returns_nonzero()
    {
        var result = await RunCaptureAsync(TempDb(), source: null, "pt");
        Assert.NotEqual(0, result.ExitCode);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task<(int ExitCode, string Output)> RunCaptureAsync(
        string dbPath, string? source, string country)
    {
        var args = source is null
            ? new[] { "--import-epg-tvg-ids" }
            : new[] { "--import-epg-tvg-ids", source, "--catalog-db", dbPath, "--epg-country", country };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var sb = new StringBuilder();
        var sw = new StringWriter(sb);
        Console.SetOut(sw);
        Console.SetError(sw);
        try
        {
            var code = await Program.RunImportEpgTvgIdsAsync(args);
            return (code, sb.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private async Task<CatalogResolver> NewResolverAsync(string dbPath)
    {
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        var context = await bootstrapper.InitializeAsync();
        await context.DisposeAsync();
        return new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
    }

    private string TempDb()
    {
        var path = TestTempDb.SuitePath($"channel-catalog-epg-{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        return path;
    }

    private string TempFile(string xmltv, bool gzip)
    {
        var extension = gzip ? ".xml.gz" : ".xml";
        var path = TestTempDb.SuitePath($"channel-catalog-epg-fixture-{Guid.NewGuid():N}{extension}");
        if (gzip)
        {
            using var file = File.Create(path);
            using var compression = new GZipStream(file, CompressionLevel.SmallestSize);
            var bytes = Encoding.UTF8.GetBytes(xmltv);
            compression.Write(bytes, 0, bytes.Length);
        }
        else
        {
            File.WriteAllText(path, xmltv, Encoding.UTF8);
        }

        _files.Add(path);
        return path;
    }

    private static string Token() => Guid.NewGuid().ToString("N")[..8];

    private static string Xmltv(string channelId)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
           $"<tv generator-info-name=\"test\">\n" +
           $"  <channel id=\"{channelId}\">\n" +
           $"    <display-name>{channelId}</display-name>\n" +
           $"  </channel>\n" +
           $"</tv>\n";
}

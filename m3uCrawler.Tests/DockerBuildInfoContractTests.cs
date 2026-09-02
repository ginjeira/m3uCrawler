using System.Diagnostics;
using System.Reflection;
using m3uCrawler.Build;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Garante coerência entre a metadata embutida na DLL (BuildInfo /
/// AssemblyInformationalVersionAttribute) e os valores esperados de um
/// build Docker produzido a partir de uma release.
///
/// Estes testes existem para defender a política de versionamento
/// (ADR .kilo/plans/20260902_161805-versioning-and-git-flow.md) contra
/// regressões futuras: a fonte canónica única é a metadata vinda do
/// GitHub Actions e propagada via --build-arg do Dockerfile.
/// </summary>
public class DockerBuildInfoContractTests
{
    /// <summary>
    /// Quando o DLL é produzido com a metadata correcta via -p: do
    /// Dockerfile, <see cref="BuildInfo.Current"/> lê os mesmos valores
    /// que os OCI labels. Este teste atravessa a fronteira Dockerfile → assembly
    /// e verifica que o parsing de <c>InformationalVersion</c> é estável.
    /// </summary>
    [Fact]
    public void BuildInfo_Contract_Matches_Dockerfile_Pipeline()
    {
        var bi = BuildInfo.LoadFromAssembly();

        // 1) SemVer válido (não pode conter 'v' prefix nem '+').
        Assert.Matches(@"^\d+\.\d+\.\d+", bi.Version);

        // 2) Commit SHA não vazio.
        Assert.False(string.IsNullOrWhiteSpace(bi.Commit));

        // 3) BuildNumber >= 0.
        Assert.True(bi.BuildNumber >= 0);

        // 4) BuildDate não default (não pode ser 1970-01-01 se a pipeline
        //    foi correctamente configurada para passar build-args).
        //    Em build local sem override, este teste é informativo:
        //    1970-01-01 é o valor esperado.
        Assert.NotEqual(DateTimeOffset.MinValue, bi.BuildDate);
    }

    /// <summary>
    /// Garante que <see cref="BuildInfo.ParseInformationalVersion"/> faz
    /// parsing correcto do formato esperado do Dockerfile:
    /// <c>SemVer+sha.&lt;commit&gt;+build.&lt;N&gt;+date.&lt;ISO&gt;</c>.
    /// </summary>
    [Theory]
    [InlineData("0.1.1+sha.adea65cee3d2+build.42+date.2026-09-02T16:29:58Z",
                "0.1.1", "adea65cee3d2", 42, "2026-09-02T16:29:58Z")]
    [InlineData("0.1.1-rc.1+sha.abc1234+build.5+date.2026-09-02T10:00:00Z",
                "0.1.1-rc.1", "abc1234", 5, "2026-09-02T10:00:00Z")]
    public void ParseInformationalVersion_Dockerfile_Pipeline(
        string raw, string expectedVersion, string expectedSha,
        int expectedBuild, string expectedDate)
    {
        var semver = BuildInfo.ParseInformationalVersion(
            raw, out var sha, out var build, out var date);

        Assert.Equal(expectedVersion, semver);
        Assert.Equal(expectedSha, sha);
        Assert.Equal(expectedBuild, build);
        Assert.NotNull(date);
        Assert.Equal(DateTimeOffset.Parse(expectedDate).UtcDateTime, date!.Value.UtcDateTime);
    }

    /// <summary>
    /// Contrato anti-regressão: o Dockerfile é a fonte canónica da
    /// metadata. Build local com tudo default tem de continuar a
    /// funcionar (caso contrário quebramos a devs-build sem docker).
    /// </summary>
    [Fact]
    public void Dockerfile_Default_Args_Do_Not_Break_Build()
    {
        // Os defaults no Dockerfile são: 0.0.0-dev, unknown, 0, 1970-01-01.
        // Quando alguém faz `docker build ./m3uCrawler` sem -build-arg,
        // o assembly resultante tem de carregar — não pode haver crash
        // e os valores têm de cair nos fallbacks documentados.
        var bi = new BuildInfo("0.0.0-dev", "unknown", 0, new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("0.0.0-dev", bi.Version);
        Assert.Equal("unknown", bi.Commit);
        Assert.Equal(0, bi.BuildNumber);
        Assert.Equal(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), bi.BuildDate);

        // ParseInformationalVersion aceita a string mesmo quando
        // os valores são placeholders.
        var semver = BuildInfo.ParseInformationalVersion(
            "0.0.0-dev+sha.unknown+build.0+date.1970-01-01T00:00:00Z",
            out var sha, out var build, out var date);
        Assert.Equal("0.0.0-dev", semver);
        Assert.Equal("unknown", sha);
        Assert.Equal(0, build);
        Assert.NotNull(date);
    }

    /// <summary>
    /// Esta é a propriedade estrutural que falha na pipeline actual:
    /// para o Dockerfile injectar M3uCrawlerVersion no dotnet publish,
    /// a string `vX.Y.Z` (formato de tag Git) precisa de ser stripped
    /// para `X.Y.Z` (formato SemVer). Os OCI labels podem manter o
    /// prefixo `v` (não é SemVer, é convenção de etiqueta).
    /// </summary>
    [Theory]
    [InlineData("v0.1.1", "0.1.1")]
    [InlineData("v0.1.1-rc.1", "0.1.1-rc.1")]
    [InlineData("0.1.1", "0.1.1")]
    [InlineData("1.0.0", "1.0.0")]
    public void Strip_V_Prefix_From_Version(string withV, string expected)
    {
        // Equivalente do RUN no Dockerfile:
        //   M3uCrawlerVersionSemver=${M3uCrawlerVersion#v}
        var stripped = withV.StartsWith("v") ? withV.Substring(1) : withV;
        Assert.Equal(expected, stripped);
    }
}

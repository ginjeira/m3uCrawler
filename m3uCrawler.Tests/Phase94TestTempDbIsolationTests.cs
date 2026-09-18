using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 (F-001) — Garantias de isolamento do sweeper.
///
/// <para>
/// Estes testes falham se o sweeper regressar a varrer
/// <c>%TEMP%</c> global ou aplicar heurísticas wildcard sobre
/// ficheiros que não lhe pertencem.
/// </para>
/// </summary>
[Collection("TestTempDbIsolation")]
[CollectionDefinition("TestTempDbIsolation", DisableParallelization = true)]
public class Phase94TestTempDbIsolationTests : IDisposable
{
    private readonly string _tempRoot = Path.GetTempPath();
    private readonly string _suiteRoot;

    public Phase94TestTempDbIsolationTests()
    {
        _suiteRoot = TestTempDb.EnsureSuiteRoot();
    }

    public void Dispose() { /* canaries cleaned in test bodies */ }

    [Fact]
    public void SweepSuiteRoot_never_deletes_files_outside_the_suite_namespace()
    {
        // Canaries em %TEMP% (directamente, fora do namespace da suite).
        var canaries = new[]
        {
            Path.Combine(_tempRoot, $"phase94-canary-{Guid.NewGuid():N}.txt"),
            Path.Combine(_tempRoot, $"cfg-myconfig-{Guid.NewGuid():N}.txt"),
            Path.Combine(_tempRoot, $"bootstrap-debug-{Guid.NewGuid():N}.log"),
            Path.Combine(_tempRoot, $"myapp-{Guid.NewGuid():N}.db.lock"),
            Path.Combine(_tempRoot, $"plain-{Guid.NewGuid():N}.db.lock"),
        };

        try
        {
            foreach (var path in canaries)
            {
                File.WriteAllText(path, "canary");
                Assert.True(File.Exists(path), $"Falha a plantar canary: {path}");
            }

            TestTempDb.SweepSuiteRoot();

            foreach (var path in canaries)
            {
                Assert.True(
                    File.Exists(path),
                    $"Canary fora do namespace foi apagado: {path}");
            }
        }
        finally
        {
            foreach (var path in canaries) TryDelete(path);
        }
    }

    [Fact]
    public void SweepSuiteRoot_removes_artefacts_with_known_prefixes_inside_the_suite_namespace()
    {
        var artefacts = new[]
        {
            Path.Combine(_suiteRoot, $"phase94-host-{Guid.NewGuid():N}.db"),
            Path.Combine(_suiteRoot, $"phase94-coord-{Guid.NewGuid():N}.db-wal"),
            Path.Combine(_suiteRoot, $"phase94-coord-{Guid.NewGuid():N}.db-shm"),
            Path.Combine(_suiteRoot, $"sched-liverun-{Guid.NewGuid():N}.db.lock"),
            Path.Combine(_suiteRoot, $"liverun-harden-{Guid.NewGuid():N}.db"),
        };

        try
        {
            foreach (var path in artefacts)
            {
                File.WriteAllText(path, "artefact");
                Assert.True(File.Exists(path));
            }

            TestTempDb.SweepSuiteRoot();

            foreach (var path in artefacts)
            {
                Assert.False(
                    File.Exists(path),
                    $"Artefacto conhecido não foi removido: {path}");
            }
        }
        finally
        {
            foreach (var path in artefacts) TryDelete(path);
        }
    }

    [Fact]
    public void SweepSuiteRoot_preserves_unmatched_files_inside_the_suite_namespace()
    {
        // Ficheiros estranhos colocados no namespace dedicado que
        // não correspondem a qualquer prefixo da suite devem ser
        // PRESERVADOS. O sweeper é scoped, não é um wildcard.
        var strangers = new[]
        {
            Path.Combine(_suiteRoot, $"user-data-{Guid.NewGuid():N}.json"),
            Path.Combine(_suiteRoot, $"notes-{Guid.NewGuid():N}.db"),
            Path.Combine(_suiteRoot, $"myapp-{Guid.NewGuid():N}.db.lock"),
            Path.Combine(_suiteRoot, $"myapp-{Guid.NewGuid():N}.db-wal"),
        };

        try
        {
            foreach (var path in strangers)
            {
                File.WriteAllText(path, "user");
                Assert.True(File.Exists(path));
            }

            TestTempDb.SweepSuiteRoot();

            foreach (var path in strangers)
            {
                Assert.True(
                    File.Exists(path),
                    $"Ficheiro estranho dentro do namespace foi apagado: {path}");
            }
        }
        finally
        {
            foreach (var path in strangers) TryDelete(path);
        }
    }

    [Fact]
    public void SweepSuiteRoot_creates_the_suite_root_if_absent()
    {
        // Não podemos remover o root durante o teste, mas podemos
        // verificar que EnsureSuiteRoot o cria.
        Assert.True(Directory.Exists(TestTempDb.EnsureSuiteRoot()));
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), TestTempDb.SuiteRootName),
            TestTempDb.EnsureSuiteRoot());
    }

    [Fact]
    public void SweepSuiteRoot_works_on_empty_directory_without_errors()
    {
        // Limpa o root e varre-o: deve ser no-op sem excepções.
        Directory.CreateDirectory(_suiteRoot);
        foreach (var f in Directory.EnumerateFiles(_suiteRoot))
        {
            TryDelete(f);
        }
        var deleted = TestTempDb.SweepSuiteRoot();
        Assert.Equal(0, deleted);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 — Utilitário de limpeza para ficheiros temporários
/// criados pelos testes.
///
/// <para>
/// <b>Motivo.</b> A suite cria uma BD temporária por teste
/// (<c>%TEMP%\*.db</c>, <c>%TEMP%\*.json</c>, <c>%TEMP%\*.m3u</c>).
/// Sem limpeza, execuções repetidas acumulam dezenas de milhares de
/// ficheiros até esgotar o disco (observado em 2026-09-17: 53 823
/// ficheiros, 7,4 GB, <c>C:</c> com 0 bytes livres → falha sistémica
/// de todos os testes que tocam em SQLite).
/// </para>
///
/// <para>
/// <b>Camadas de defesa.</b>
/// <list type="number">
///   <item>
///     Por teste (<see cref="Cleanup(string[])"/>): as classes que
///     implementam <see cref="IAsyncLifetime"/> chamam isto no seu
///     <c>DisposeAsync</c>. Fecha pools de conexão, apaga o ficheiro
///     principal e os sidecars <c>-wal</c>/<c>-shm</c>, ignora falhas.
///   </item>
///   <item>
///     Por processo (<see cref="SweepKnownPatterns"/>): um
///     <see cref="ModuleInitializerAttribute"/> regista um
///     <c>ProcessExit</c> que varre <c>%TEMP%</c> e apaga tudo o que
///     corresponda a padrões conhecidos de fixtures de teste.
///     Apanha as classes que não implementam <c>IAsyncLifetime</c>.
///   </item>
/// </list>
/// </para>
/// </summary>
internal static class TestTempDb
{
    /// <summary>
    /// Sufixos de ficheiros temporários a apagar (ficheiro principal +
    /// sidecars SQLite <c>-wal</c>/<c>-shm</c>).
    /// </summary>
    private static readonly string[] SidecarSuffixes = { string.Empty, "-wal", "-shm" };

    /// <summary>
    /// Prefixos de ficheiros/directórios temporários pertencentes a
    /// fixtures da suite de testes. O sweeper no fim do processo
    /// apanha qualquer ficheiro que tenha ficado para trás porque a
    /// classe produtora não implementa <see cref="IAsyncLifetime"/>
    /// ou porque uma <c>try/catch</c> falhou.
    /// </summary>
    private static readonly string[] KnownTestPrefixes =
    {
        // Phase 9C.4 — Live Run.
        "phase94-", "phase94-coord-", "phase94-host-", "phase94-direct-",
        "phase94-recent-", "phase94-instr-fixture-", "phase94-liverun-",
        "phase93-", "phase93-affinity-", "phase93-dispatcharr-",
        "phase93-rev-", "phase93-settings-", "phase93-out-",
        "dash-liverun-", "sched-liverun-", "liverun-harden-",
        // Phase 9C.1 / 9C.2.
        "cfg-", "cfg-gate-", "cfg-9c1-", "cfg-endpoint-",
        "dash-auth-", "dash-corrupt-", "dash-fallback-",
        "dash-files-", "dash-no-files-", "dash-paired-",
        "auth-store-", "bootstrap-", "l2-",
        // Catálogo / matching / scheduler / dispatcharr.
        "channel-catalog-", "channel-catalog-tests-", "channel-catalog-ownership-guard-",
        "catalog-admin-", "catalog-admin-tests-", "catalog-baseline-", "catalog-baseline-test-",
        "ordering-", "ordering-tests-",
        "sources-", "sources-tests-",
        "policies-groups-", "scheduled-actions-", "scheduled-jobs-", "sched-e2e-",
        "sched-out-",
        "degradation-", "http-api-tests-", "pipeline-bridge-",
        "matching-audit-", "sync-run-steps-",
        // Out dir.
        "out_",
    };

    /// <summary>
    /// Sufixos temporais e separadores que algumas fixtures usam para
    /// distinguir runs (underscore + timestamp). O sweeper trata-os
    /// como equivalentes aos prefixos <see cref="KnownTestPrefixes"/>.
    /// </summary>
    private static readonly string[] KnownTestPrefixesAltSeparator =
    {
        "phase94-", "phase94_coord-", "phase94_host-", "phase94_direct-",
        "phase94_recent-", "phase94_instr_fixture-", "phase94_liverun-",
        "phase93-", "phase93_affinity-", "phase93_dispatcharr-",
        "phase93_rev-", "phase93_settings-", "phase93_out-",
        "dash_liverun-", "sched_liverun-", "liverun_harden-",
        "cfg-", "cfg_gate-", "cfg_9c1-", "cfg_endpoint-",
        "dash_auth-", "dash_corrupt-", "dash_fallback-",
        "dash_files-", "dash_no_files-", "dash_paired-",
        "auth_store-", "bootstrap-", "l2-",
        "channel_catalog-", "channel_catalog_tests-", "channel_catalog_ownership_guard-",
        "catalog_admin-", "catalog_admin_tests-", "catalog_baseline-", "catalog_baseline_test-",
        "ordering-", "ordering_tests-",
        "sources-", "sources_tests-",
        "policies_groups-", "scheduled_actions-", "scheduled_jobs-", "sched_e2e-",
        "sched_out-",
        "degradation-", "http_api_tests-", "pipeline_bridge-",
        "matching_audit-", "sync_run_steps-",
        // Auditoria / validação live (timestamped).
        "bundle_guard_validation_", "content_type_distribution_",
        "country_opcao_c_live_validation_", "country_opcao_c_validation_",
        "taxonomy_investigation_",
        // Padrões sem prefixo: timestamped r1/r2/group_audit.
        "r1_", "r2_", "r1-", "r2-",
        "group_audit_", "group_audit-",
        "diag_", "diag-",
    };

    private static readonly string[] KnownTestDirectoryPrefixes =
    {
        "dash-liverun-", "sched-liverun-",
    };

    /// <summary>
    /// Sufixos de ficheiros auxiliares SQLite ou sidecars (lock, journal,
    /// shm, wal) que algumas fixtures criam com nomes como
    /// <c>prefix-guid.db.lock</c>.
    /// </summary>
    private static readonly string[] AuxiliarySuffixes = { ".db.lock" };

    [ModuleInitializer]
    public static void ModuleInit()
    {
        // O test runner do `dotnet test` (vstest) mantém o processo
        // host vivo entre runs e `ProcessExit` raramente dispara de
        // forma fiável. Em vez disso, limpamos no *início* do
        // processo: cada execução arranca com `%TEMP%` livre de
        // artefactos de runs anteriores.
        try
        {
            SweepKnownPatterns();
        }
        catch
        {
            // best effort: nunca bloqueia a suite
        }

        // Também registamos um hook de saída como rede de segurança
        // (caso o processo termine de facto — por exemplo, em testes
        // individuais via `dotnet run`).
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => SafeSweep();
    }

    /// <summary>Apaga <paramref name="paths"/> e respectivos sidecars.</summary>
    public static void Cleanup(params string?[] paths)
    {
        try { SqliteConnection.ClearAllPools(); } catch { /* best effort */ }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            foreach (var suffix in SidecarSuffixes)
            {
                var target = path + suffix;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (!File.Exists(target)) break;
                        File.Delete(target);
                        break;
                    }
                    catch
                    {
                        if (attempt == 4) break;
                        Thread.Sleep(25);
                        try { SqliteConnection.ClearAllPools(); } catch { /* best effort */ }
                    }
                }
            }
        }
    }

    /// <summary>Apaga <paramref name="paths"/> e respectivos sidecars (versão síncrona para <see cref="IDisposable.Dispose"/>).</summary>
    public static void CleanupSync(params string?[] paths) => Cleanup(paths);

    /// <summary>Apaga recursivamente directórios temporários.</summary>
    public static void CleanupDirectory(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    break;
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    /// <summary>
    /// Varre <c>%TEMP%</c> e apaga todos os ficheiros/directórios cujo
    /// nome comece por um padrão conhecido de fixture de teste.
    /// Útil como rede de segurança a correr no fim do processo.
    /// </summary>
    public static int SweepKnownPatterns(string? tempRoot = null)
    {
        tempRoot ??= Path.GetTempPath();
        if (!Directory.Exists(tempRoot)) return 0;

        try { SqliteConnection.ClearAllPools(); } catch { /* best effort */ }
        Thread.Sleep(50);

        var deleted = 0;
        try
        {
            // Apaga directórios em primeiro lugar para libertar
            // handles que impeçam a remoção de ficheiros contidos.
            foreach (var prefix in KnownTestDirectoryPrefixes)
            {
                foreach (var dir in Directory.EnumerateDirectories(tempRoot, prefix + "*", SearchOption.TopDirectoryOnly))
                {
                    if (TryDeleteDirectory(dir)) deleted++;
                }
            }

            // Apaga ficheiros principais + sidecars.
            foreach (var prefix in AllPrefixes())
            {
                foreach (var path in Directory.EnumerateFiles(tempRoot, prefix + "*", SearchOption.TopDirectoryOnly))
                {
                    DeleteWithSidecars(path);
                    if (!File.Exists(path)) deleted++;
                }

                // Sidecars cujo nome não começa pelo prefixo mas
                // segue o ficheiro principal (ex.: "phase94-x.db-wal").
                foreach (var path in Directory.EnumerateFiles(tempRoot, "*-wal", SearchOption.TopDirectoryOnly))
                {
                    var mainPath = Path.ChangeExtension(path, null);
                    if (AllPrefixes().Any(p => mainPath.StartsWith(Path.Combine(tempRoot, p), StringComparison.OrdinalIgnoreCase)))
                    {
                        DeleteFileSafely(path);
                    }
                }
                foreach (var path in Directory.EnumerateFiles(tempRoot, "*-shm", SearchOption.TopDirectoryOnly))
                {
                    var mainPath = Path.ChangeExtension(path, null);
                    if (AllPrefixes().Any(p => mainPath.StartsWith(Path.Combine(tempRoot, p), StringComparison.OrdinalIgnoreCase)))
                    {
                        DeleteFileSafely(path);
                    }
                }
            }

            // Auxiliares SQLite: *.db.lock (lock file de migrations)
            foreach (var suffix in AuxiliarySuffixes)
            {
                foreach (var path in Directory.EnumerateFiles(tempRoot, "*" + suffix, SearchOption.TopDirectoryOnly))
                {
                    var baseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
                    // baseName é o nome antes de ".db.lock"; tenta
                    // casar com algum prefixo conhecido.
                    if (AllPrefixes().Any(p => baseName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        || baseName.Contains("-") || baseName.Contains("_"))
                    {
                        // Heurística segura: nomes com GUID/timestamp
                        // pertencem a fixtures.
                        DeleteFileSafely(path);
                    }
                }
            }
        }
        catch
        {
            // best effort: não bloqueia o shutdown do test runner.
        }

        return deleted;
    }

    private static IEnumerable<string> AllPrefixes()
    {
        foreach (var p in KnownTestPrefixes) yield return p;
        foreach (var p in KnownTestPrefixesAltSeparator) yield return p;
    }

    private static void SafeSweep()
    {
        try { SweepKnownPatterns(); } catch { /* best effort */ }
    }

    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteWithSidecars(string mainPath)
    {
        DeleteFileSafely(mainPath);
        DeleteFileSafely(mainPath + "-wal");
        DeleteFileSafely(mainPath + "-shm");
    }

    private static void DeleteFileSafely(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch
            {
                if (attempt == 2) return;
                Thread.Sleep(25);
            }
        }
    }
}

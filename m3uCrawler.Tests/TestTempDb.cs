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
/// <b>Directório dedicado.</b> Todos os artefactos da suite vivem
/// sob <c>%TEMP%\m3uCrawler.Tests.tmp\</c>. O directório é criado
/// pela primeira chamada a <see cref="EnsureSuiteRoot"/> e os
/// artefactos têm nomes prefixados (e.g. <c>phase94-host-</c>,
/// <c>phase94-coord-</c>) para que o sweeper possa identificá-los
/// sem ambiguidade.
/// </para>
///
/// <para>
/// <b>Porquê um directório dedicado?</b> O sweeper NUNCA opera sobre
/// <c>%TEMP%</c> global: só apaga ficheiros dentro do seu próprio
/// directório, cujos nomes correspondam a prefixos explícitos. Isto
/// garante que:
/// <list type="bullet">
///   <item>ficheiros de outros processos/utilizadores em <c>%TEMP%</c>
///         são preservados;</item>
///   <item>o espaço dedicado é estabilizado entre execuções.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Camadas de defesa.</b>
/// <list type="number">
///   <item>
///     Por teste (<see cref="Cleanup"/>): a suite chama isto no
///     seu <c>DisposeAsync</c>. Fecha pools de conexão, apaga o
///     ficheiro principal e os sidecars <c>-wal</c>/<c>-shm</c>,
///     ignora falhas.
///   </item>
///   <item>
///     Por processo (<see cref="SweepSuiteRoot"/>): um
///     <see cref="ModuleInitializerAttribute"/> corre no início
///     do processo e apaga apenas ficheiros do namespace dedicado
///     cujo nome comece por um dos prefixos conhecidos. Ficheiros
///     que não correspondam são preservados.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// <b>Não há heurísticas wildcard.</b> A heurística anterior que
/// combinava <c>*.db.lock</c> com <c>Contains("-") || Contains("_")</c>
/// operava efectivamente como wildcard sobre <c>%TEMP%</c> e foi
/// removida por razões de segurança (review independente de
/// 2026-09-17, finding F-001).
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
    /// Nome do directório dedicado da suite. Concatenado a
    /// <see cref="Path.GetTempPath"/> na primeira utilização e criado
    /// com <see cref="Directory.CreateDirectory(string)"/> (que é
    /// idempotente e seguro).
    /// </summary>
    public const string SuiteRootName = "m3uCrawler.Tests.tmp";

    /// <summary>
    /// Prefixos de ficheiros/directórios temporários pertencentes a
    /// fixtures da suite. O sweeper compara com <c>String.StartsWith</c>.
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
        // Phase 13 (Wave 13-3/13-4/13-5) — source selection.
        "source-selection-stage-", "source-selection-policy-", "source-selection-preview-",
        // Phase 9C.6 — identidade canónica (Key autoritativa).
        "phase9c6-",
        // Out dir.
        "out_",
    };

    /// <summary>
    /// Variantes dos prefixos com separador <c>_</c> em vez de
    /// <c>-</c>. Algumas fixtures usam timestamp <c>_YYYYMMDD_HHMMSS</c>.
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
        // Phase 13 (Wave 13-3/13-4/13-5) — source selection.
        "source_selection_stage-", "source_selection_policy-", "source_selection_preview-",
        // Phase 9C.6 — identidade canónica (Key autoritativa).
        "phase9c6-", "phase9c6_",
        // Auditoria / validação live (timestamped).
        "bundle_guard_validation_", "content_type_distribution_",
        "country_opcao_c_live_validation_", "country_opcao_c_validation_",
        "taxonomy_investigation_",
        // Padrões sem prefixo: timestamped r1/r2/group_audit.
        "r1_", "r2_", "r1-", "r2-",
        "group_audit_", "group_audit-",
        "diag_", "diag-",
    };

    /// <summary>
    /// Lock files SQLite (lock de migrations): apaga <c>*.db.lock</c>
    /// apenas quando o baseName começa por um dos prefixos conhecidos.
    /// Sem heurística wildcard.
    /// </summary>
    private const string DbLockSuffix = ".db.lock";

    [ModuleInitializer]
    public static void ModuleInit()
    {
        try
        {
            SweepSuiteRoot();
        }
        catch
        {
            // best effort: nunca bloqueia a suite
        }

        // Hook de saída como rede de segurança (em geral, não dispara
        // no `dotnet test`, mas fica disponível para `dotnet run`).
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => SafeSweep();
    }

    /// <summary>
    /// Devolve e cria (se necessário) o directório dedicado da suite.
    /// Idempotente: chamar várias vezes devolve sempre o mesmo path.
    /// </summary>
    public static string EnsureSuiteRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), SuiteRootName);
        Directory.CreateDirectory(root); // no-op se já existir
        return root;
    }

    /// <summary>
    /// Devolve o caminho completo para um ficheiro dentro do directório
    /// dedicado da suite. Útil para testes que opt-in por usar este
    /// namespace isolado. <b>Não</b> cria o ficheiro (responsabilidade
    /// do teste).
    /// </summary>
    public static string SuitePath(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Path.Combine(EnsureSuiteRoot(), fileName);
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
            // Sidecar de lock files apenas se o main path bater um prefixo
            // conhecido. Sem heurística wildcard.
            TryDeleteDbLockIfKnown(path);
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
    /// Varre o <b>directório dedicado da suite</b> e apaga apenas
    /// ficheiros cujo nome comece por um prefixo conhecido. Nunca
    /// toca em ficheiros fora do namespace da suite. Nunca aplica
    /// heurísticas wildcard.
    /// </summary>
    /// <returns>número de ficheiros (principais) efectivamente removidos.</returns>
    public static int SweepSuiteRoot()
    {
        var root = EnsureSuiteRoot();
        if (!Directory.Exists(root)) return 0;

        try { SqliteConnection.ClearAllPools(); } catch { /* best effort */ }
        Thread.Sleep(50);

        var deleted = 0;
        try
        {
            // Ficheiros principais + sidecars.
            foreach (var prefix in AllPrefixes())
            {
                foreach (var path in Directory.EnumerateFiles(root, prefix + "*", SearchOption.TopDirectoryOnly))
                {
                    DeleteWithSidecars(path);
                    if (!File.Exists(path)) deleted++;
                }

                // Sidecars cujo nome não começa pelo prefixo (ex.:
                // "phase94-x.db-wal"). Apenas se o mainPath bater
                // um prefixo conhecido.
                foreach (var path in Directory.EnumerateFiles(root, "*-wal", SearchOption.TopDirectoryOnly))
                {
                    var mainPath = Path.ChangeExtension(path, null);
                    if (AllPrefixes().Any(p => mainPath.StartsWith(Path.Combine(root, p), StringComparison.OrdinalIgnoreCase)))
                    {
                        DeleteFileSafely(path);
                    }
                }
                foreach (var path in Directory.EnumerateFiles(root, "*-shm", SearchOption.TopDirectoryOnly))
                {
                    var mainPath = Path.ChangeExtension(path, null);
                    if (AllPrefixes().Any(p => mainPath.StartsWith(Path.Combine(root, p), StringComparison.OrdinalIgnoreCase)))
                    {
                        DeleteFileSafely(path);
                    }
                }
            }

            // Lock files SQLite (*.db.lock) apenas quando o baseName
            // começar por um dos prefixos conhecidos. SEM wildcard.
            foreach (var path in Directory.EnumerateFiles(root, "*" + DbLockSuffix, SearchOption.TopDirectoryOnly))
            {
                var baseName = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(path));
                if (AllPrefixes().Any(p => baseName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    DeleteFileSafely(path);
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
        try { SweepSuiteRoot(); } catch { /* best effort */ }
    }

    private static void TryDeleteDbLockIfKnown(string mainPath)
    {
        // O ChannelCatalogBootstrapper cria o lock como "<dbpath>.lock".
        // Só apagamos quando o nome do ficheiro bate um prefixo conhecido.
        var baseName = Path.GetFileName(mainPath);
        var lockPath = mainPath + ".lock";
        if (!File.Exists(lockPath)) return;
        if (AllPrefixes().Any(p => baseName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            DeleteFileSafely(lockPath);
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

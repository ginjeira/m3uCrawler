using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 — Utilitário de limpeza para BDs SQLite temporárias
/// criadas pelos testes.
///
/// <para>
/// Motivo: a suite cria uma BD temporária por teste (<c>%TEMP%\*.db</c>).
/// Sem limpeza, execuções repetidas acumulam dezenas de milhares de
/// ficheiros até esgotar o disco (observado em 2026-09-17: 53 823
/// ficheiros, 7,4 GB, <c>C:</c> com 0 bytes livres → falha sistémica de
/// todos os testes que tocam em SQLite).
/// </para>
///
/// <para>
/// A limpeza é best-effort: fecha os pools de conexões do
/// <c>Microsoft.Data.Sqlite</c>, apaga o ficheiro principal e os
/// sidecars <c>-wal</c>/<c>-shm</c>, e ignora falhas (o ficheiro pode
/// estar momentaneamente bloqueado). Nunca lança.
/// </para>
/// </summary>
internal static class TestTempDb
{
    public static void Cleanup(params string?[] paths)
    {
        // Libertar handles de conexões pooled (cache privada por
        // conexão), caso contrário o ficheiro pode continuar em uso.
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            // best effort
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var target = path + suffix;
                // Retry curto: o ficheiro pode estar bloqueado por uma
                // conexão pooled/contexto ainda a fechar.
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

    /// <summary>Limpa recursivamente directórios temporários de teste.</summary>
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
}

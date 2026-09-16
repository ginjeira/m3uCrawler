using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace m3uCrawler.Services.Validation
{
    /// <summary>
    /// EXPERIMENTAL (2026-09-16): sincronizacao por conta Xtream para a
    /// experiencia "experiment/serial-per-xtream-account".
    ///
    /// Definicao da experiencia: uma conta Xtream e' identificada pelo par
    /// (URL, username). A password NAO participa da identidade. Portanto:
    ///   - duas requests concorrentes para o mesmo (URL, username) devem
    ///     serializar (1 operacao activa por conta);
    ///   - contas diferentes (URL ou username diferente) podem correr em
    ///     paralelo;
    ///   - um global lock NAO e' aceitavel (anti-pattern que estrangularia
    ///     o pipeline).
    ///
    /// Implementacao: ConcurrentDictionary&lt;string, Entry&gt; onde cada
    /// identidade tem um SemaphoreSlim(1,1). Reference counting para limpar
    /// entries quando nao estao em uso (evita crescimento ilimitado).
    ///
    /// Observabilidade minima:
    ///   XTREAM_ACCOUNT_LOCK identity=&lt;fingerprint&gt; action=WAIT
    ///   XTREAM_ACCOUNT_LOCK identity=&lt;fingerprint&gt; action=ACQUIRED elapsedMs=...
    ///   XTREAM_ACCOUNT_LOCK identity=&lt;fingerprint&gt; action=RELEASED heldMs=...
    ///
    /// O fingerprint e' SHA-256(URL + "|" + username) truncado a 16 chars hex.
    /// A password NUNCA participa do input do fingerprint. O fingerprint e'
    /// seguro para logs.
    /// </summary>
    public sealed class XtreamAccountLockManager
    {
        private sealed class Entry
        {
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            public int RefCount;
        }

        private readonly ConcurrentDictionary<string, Entry> _entries
            = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        public int TrackedIdentities => _entries.Count;

        public static string ComputeIdentity(string url, string? username)
        {
            if (string.IsNullOrEmpty(url)) return "0";
            var u = username ?? string.Empty;
            var composite = url + "|" + u;
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(composite));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        public async Task<IAsyncDisposable> AcquireAsync(string identity, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(identity)) identity = "0";

            var entry = _entries.AddOrUpdate(
                identity,
                _ => new Entry { RefCount = 1 },
                (_, existing) =>
                {
                    Interlocked.Increment(ref existing.RefCount);
                    return existing;
                });

            var waitSw = Stopwatch.StartNew();
            Console.WriteLine($"[DIAG_EXPERIMENT_SERIAL_PER_XTREAM] XTREAM_ACCOUNT_LOCK identity={identity} action=WAIT");
            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Decrement refcount if we lost the race (cancellation before grant).
                DecrementRefCount(identity, entry);
                throw;
            }
            waitSw.Stop();
            Console.WriteLine($"[DIAG_EXPERIMENT_SERIAL_PER_XTREAM] XTREAM_ACCOUNT_LOCK identity={identity} action=ACQUIRED waitElapsedMs={waitSw.ElapsedMilliseconds}");
            return new Releaser(this, identity, entry, waitSw);
        }

        private void DecrementRefCount(string identity, Entry entry)
        {
            if (Interlocked.Decrement(ref entry.RefCount) == 0)
            {
                // Best-effort cleanup. If another thread races and re-adds the
                // same identity between Decrement and Remove, the Remove will
                // fail and we keep the existing entry (correct).
                if (((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(
                        KeyValuePair.Create(identity, entry)))
                {
                    try { entry.Semaphore.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        private sealed class Releaser : IAsyncDisposable
        {
            private readonly XtreamAccountLockManager _owner;
            private readonly string _identity;
            private readonly Entry _entry;
            private readonly Stopwatch _heldSw;
            private int _disposed;

            public Releaser(XtreamAccountLockManager owner, string identity, Entry entry, Stopwatch heldSw)
            {
                _owner = owner;
                _identity = identity;
                _entry = entry;
                _heldSw = heldSw;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
                try { _entry.Semaphore.Release(); } catch { /* ignore */ }
                _heldSw.Stop();
                Console.WriteLine($"[DIAG_EXPERIMENT_SERIAL_PER_XTREAM] XTREAM_ACCOUNT_LOCK identity={_identity} action=RELEASED heldMs={_heldSw.ElapsedMilliseconds}");
                _owner.DecrementRefCount(_identity, _entry);
                return ValueTask.CompletedTask;
            }
        }
    }
}

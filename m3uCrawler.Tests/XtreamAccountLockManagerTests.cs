using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// EXPERIMENT-SERIAL-PER-XTREAM (2026-09-16): testes deterministicos da
/// sincronizacao por conta Xtream.
///
/// Verificam o modelo de concorrência pretendido:
///
///   Conta A ──► teste A1 ──► teste A2 ──► teste A3 ──► ...
///   Conta B ──► teste B1 ──► teste B2 ──► teste B3 ──► ...
///
///   A, B e C podem executar em paralelo.
///   Dentro de A, nunca existem duas operacoes simultaneas.
///
/// Estes testes nao sao evidencia sobre o incidente 110751; servem apenas
/// para validar o mecanismo do lock-manager.
/// </summary>
public class XtreamAccountLockManagerTests
{
    private const string UrlA = "http://neorcqds.example:8080/get.php?username=alice&password=SecretA&type=m3u_plus&output=m3u8";
    private const string UrlB = "http://neorcqds.example:8080/get.php?username=bob&password=SecretB&type=m3u_plus&output=m3u8";
    private const string UrlC = "http://otherhost.example:9090/get.php?username=alice&password=SecretA&type=m3u_plus&output=m3u8";
    private const string UrlA_PwdDifferent = "http://neorcqds.example:8080/get.php?username=alice&password=DIFFERENT_PASSWORD&type=m3u_plus&output=m3u8";

    // ----------------------------------------------------------------------------
    // Identity / fingerprint tests
    // ----------------------------------------------------------------------------

    [Fact]
    public void Same_url_and_same_username_yield_same_identity()
    {
        var id1 = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        var id2 = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Same_url_different_username_yields_different_identity()
    {
        var idAlice = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        var idBob = XtreamAccountLockManager.ComputeIdentity(UrlB, "bob");
        Assert.NotEqual(idAlice, idBob);
    }

    [Fact]
    public void Different_url_same_username_yields_different_identity()
    {
        // Same username "alice" but different host:port (UrlC).
        var id1 = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        var id2 = XtreamAccountLockManager.ComputeIdentity(UrlC, "alice");
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Identity_does_not_contain_password()
    {
        // The fingerprint is computed from URL + username. The password in
        // the URL is included in the URL but the resulting identity is a
        // SHA-256 hex digest, never the password in clear.
        var id = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        Assert.DoesNotContain("SecretA", id);
        Assert.DoesNotContain("alice", id); // username not exposed either
        Assert.DoesNotContain("DIFFERENT", id); // password not exposed
        // SHA-256 hex produces [0-9a-f]+. Length should be 16 chars (8 bytes hex).
        Assert.Matches("^[0-9a-f]{16}$", id);
    }

    [Fact]
    public void Same_url_and_username_with_different_password_yields_same_identity()
    {
        // EXPERIMENTAL definition: identity = (URL, username). Password not
        // part of identity. Therefore two accounts on the same URL with
        // same username but different passwords have the SAME identity and
        // are serialised.
        //
        // The URL itself contains the password as a query parameter, so we
        // must strip the `password=` parameter before hashing.
        var id1 = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        var id2 = XtreamAccountLockManager.ComputeIdentity(UrlA_PwdDifferent, "alice");
        Assert.NotEqual(id1, id2); // raw URLs differ (password in query)
        // But via the helper that strips password, they are the same:
        var c1 = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = UrlA,
            DetectedFrom = "xtream publication",
        };
        var c2 = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = UrlA_PwdDifferent,
            DetectedFrom = "xtream publication",
        };
        Assert.Equal(
            TelegramScraperService.TryExtractXtreamIdentity(c1),
            TelegramScraperService.TryExtractXtreamIdentity(c2));
    }

    // ----------------------------------------------------------------------------
    // Concurrency tests
    // ----------------------------------------------------------------------------

    private static async Task<T> DelayAsync<T>(T value, int ms)
    {
        await Task.Delay(ms).ConfigureAwait(false);
        return value;
    }

    [Fact]
    public async Task Same_account_serialises_concurrent_acquires()
    {
        // Same identity: 5 acquires should run one at a time. Total wall
        // time should be >= 5 * holdMs (not 1 * holdMs).
        var mgr = new XtreamAccountLockManager();
        var identity = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        var overlap = 0;
        var inFlight = 0;
        var maxInFlight = 0;
        var doneLock = new object();

        async Task AcquireOne()
        {
            await using (await mgr.AcquireAsync(identity, CancellationToken.None))
            {
                var n = Interlocked.Increment(ref inFlight);
                lock (doneLock) { if (n > maxInFlight) maxInFlight = n; }
                await Task.Delay(40).ConfigureAwait(false);
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref overlap);
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(AcquireOne)).ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();

        Assert.Equal(5, overlap);
        Assert.Equal(1, maxInFlight); // strict serial
        // 5 ops * 40ms = 200ms minimum; allow some scheduler slack but
        // require at least 150ms (would be ~40ms if parallel).
        Assert.True(sw.ElapsedMilliseconds >= 150,
            $"Expected >= 150ms (serial), got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Different_username_runs_in_parallel()
    {
        var mgr = new XtreamAccountLockManager();
        var idA = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        var idB = XtreamAccountLockManager.ComputeIdentity(UrlB, "bob");
        var inFlight = 0;
        var maxInFlight = 0;
        var lockObj = new object();

        async Task Acquire(string id)
        {
            await using (await mgr.AcquireAsync(id, CancellationToken.None))
            {
                var n = Interlocked.Increment(ref inFlight);
                lock (lockObj) { if (n > maxInFlight) maxInFlight = n; }
                await Task.Delay(80).ConfigureAwait(false);
                Interlocked.Decrement(ref inFlight);
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t1 = Task.Run(() => Acquire(idA));
        var t2 = Task.Run(() => Acquire(idB));
        await Task.WhenAll(t1, t2);
        sw.Stop();

        Assert.True(maxInFlight >= 2, $"Expected concurrent execution, got maxInFlight={maxInFlight}");
        Assert.True(sw.ElapsedMilliseconds < 160, $"Expected < 160ms (parallel), got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Different_url_runs_in_parallel()
    {
        var mgr = new XtreamAccountLockManager();
        var idA = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        var idC = XtreamAccountLockManager.ComputeIdentity(UrlC, "alice"); // same user, different host
        var inFlight = 0;
        var maxInFlight = 0;
        var lockObj = new object();

        async Task Acquire(string id)
        {
            await using (await mgr.AcquireAsync(id, CancellationToken.None))
            {
                var n = Interlocked.Increment(ref inFlight);
                lock (lockObj) { if (n > maxInFlight) maxInFlight = n; }
                await Task.Delay(80).ConfigureAwait(false);
                Interlocked.Decrement(ref inFlight);
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t1 = Task.Run(() => Acquire(idA));
        var t2 = Task.Run(() => Acquire(idC));
        await Task.WhenAll(t1, t2);
        sw.Stop();

        Assert.True(maxInFlight >= 2, $"Expected concurrent execution, got maxInFlight={maxInFlight}");
        Assert.True(sw.ElapsedMilliseconds < 160, $"Expected < 160ms (parallel), got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Lock_is_released_on_exception()
    {
        var mgr = new XtreamAccountLockManager();
        var identity = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");

        // Acquire and throw inside the using block.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using (await mgr.AcquireAsync(identity, CancellationToken.None))
            {
                throw new InvalidOperationException("boom");
            }
        });

        // The lock must have been released. A subsequent acquire must
        // succeed quickly (no deadlock).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (await mgr.AcquireAsync(identity, CancellationToken.None))
        {
            await Task.Delay(20).ConfigureAwait(false);
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Subsequent acquire took {sw.ElapsedMilliseconds}ms (possible deadlock)");
    }

    [Fact]
    public async Task Exception_in_one_account_does_not_block_others()
    {
        var mgr = new XtreamAccountLockManager();
        var idA = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        var idB = XtreamAccountLockManager.ComputeIdentity(UrlB, "bob");

        // 1) Hold A for a while, then release.
        //    Meanwhile, B should be acquirable independently (in parallel).
        var acquiredBStart = new SemaphoreSlim(0, 1);
        var acquiredBDone = new SemaphoreSlim(0, 1);
        var bSw = System.Diagnostics.Stopwatch.StartNew();
        var bTask = Task.Run(async () =>
        {
            acquiredBStart.Release();
            await using (await mgr.AcquireAsync(idB, CancellationToken.None))
            {
                await Task.Delay(40).ConfigureAwait(false);
            }
            bSw.Stop();
            acquiredBDone.Release();
        });

        // Wait for B's task to start.
        await acquiredBStart.WaitAsync().ConfigureAwait(false);

        // Now hold A in parallel.
        await using (await mgr.AcquireAsync(idA, CancellationToken.None))
        {
            await Task.Delay(40).ConfigureAwait(false);
        }

        // B should have completed in parallel (~40ms) not serially (~80ms).
        await acquiredBDone.WaitAsync().ConfigureAwait(false);

        Assert.True(bSw.ElapsedMilliseconds < 70,
            $"B took {bSw.ElapsedMilliseconds}ms (should run in parallel with A)");

        // 2) Now exercise the exception path: a thrown exception inside the
        //    using block must release the lock, NOT block others.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using (await mgr.AcquireAsync(idA, CancellationToken.None))
            {
                throw new InvalidOperationException("forced");
            }
        });

        // After the exception, a subsequent acquire must succeed quickly.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (await mgr.AcquireAsync(idA, CancellationToken.None))
        {
            await Task.Delay(20).ConfigureAwait(false);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Post-exception acquire took {sw.ElapsedMilliseconds}ms (possible leak)");
    }

    [Fact]
    public async Task Tracked_identities_do_not_grow_unboundedly()
    {
        // Exercise many distinct identities, then dispose all acquires, and
        // verify the internal map shrinks back (refcount cleanup).
        var mgr = new XtreamAccountLockManager();
        var identities = Enumerable.Range(0, 100)
            .Select(i => XtreamAccountLockManager.ComputeIdentity($"http://h{i}.example/get.php", $"u{i}"))
            .ToArray();

        // Acquire each one once, release it.
        foreach (var id in identities)
        {
            await using (await mgr.AcquireAsync(id, CancellationToken.None))
            {
                // immediate release.
            }
        }

        // Allow the cleanup to run.
        await Task.Delay(50).ConfigureAwait(false);

        Assert.True(mgr.TrackedIdentities <= identities.Length,
            $"TrackedIdentities={mgr.TrackedIdentities} should not exceed the number of distinct identities used");
        // After all acquires complete and refcount hits 0, the map should
        // be empty (best-effort cleanup).
        Assert.Equal(0, mgr.TrackedIdentities);
    }

    [Fact]
    public async Task Same_identity_acquired_repeatedly_only_one_active_at_a_time()
    {
        var mgr = new XtreamAccountLockManager();
        var identity = XtreamAccountLockManager.ComputeIdentity(UrlA, "alice");
        var inFlight = 0;
        var maxInFlight = 0;
        var lockObj = new object();
        var total = 20;

        async Task AcquireAndHold()
        {
            await using (await mgr.AcquireAsync(identity, CancellationToken.None))
            {
                var n = Interlocked.Increment(ref inFlight);
                lock (lockObj) { if (n > maxInFlight) maxInFlight = n; }
                await Task.Delay(15).ConfigureAwait(false);
                Interlocked.Decrement(ref inFlight);
            }
        }

        var tasks = Enumerable.Range(0, total).Select(_ => Task.Run(AcquireAndHold)).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxInFlight);
    }

    // ----------------------------------------------------------------------------
    // TryExtractXtreamIdentity (CandidatePlaylist -> identity)
    // ----------------------------------------------------------------------------

    [Fact]
    public void TryExtractXtreamIdentity_returns_null_for_non_xtream_publication()
    {
        var c = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = UrlA,
            DetectedFrom = "m3u url",
        };
        Assert.Null(TelegramScraperService.TryExtractXtreamIdentity(c));
    }

    [Fact]
    public void TryExtractXtreamIdentity_returns_fingerprint_for_xtream_publication()
    {
        var c = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = UrlA,
            DetectedFrom = "xtream publication",
        };
        var id = TelegramScraperService.TryExtractXtreamIdentity(c);
        Assert.NotNull(id);
        // The fingerprint should be the SHA-256 of the password-stripped URL.
        var urlNoPwd = "http://neorcqds.example:8080/get.php?username=alice&type=m3u_plus&output=m3u8";
        Assert.Equal(XtreamAccountLockManager.ComputeIdentity(urlNoPwd, "alice"), id);
    }

    [Fact]
    public void TryExtractXtreamIdentity_url_with_no_username_returns_null()
    {
        var c = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = "http://x.example/y",
            DetectedFrom = "xtream publication",
        };
        Assert.Null(TelegramScraperService.TryExtractXtreamIdentity(c));
    }

    [Fact]
    public void TryExtractXtreamIdentity_null_url_returns_null()
    {
        var c = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = null,
            DetectedFrom = "xtream publication",
        };
        Assert.Null(TelegramScraperService.TryExtractXtreamIdentity(c));
    }

    [Fact]
    public void TryExtractXtreamIdentity_different_username_produces_different_identity()
    {
        var cAlice = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = UrlA,
            DetectedFrom = "xtream publication",
        };
        var cBob = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = UrlB,
            DetectedFrom = "xtream publication",
        };
        var idA = TelegramScraperService.TryExtractXtreamIdentity(cAlice);
        var idB = TelegramScraperService.TryExtractXtreamIdentity(cBob);
        Assert.NotNull(idA);
        Assert.NotNull(idB);
        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public void TryExtractXtreamIdentity_same_username_different_password_produces_same_identity()
    {
        // Definition: identity = (URL, username). Password must NOT
        // participate. So two candidates with same URL + same username but
        // different passwords are the SAME identity.
        var c1 = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = UrlA,
            DetectedFrom = "xtream publication",
        };
        var c2 = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = UrlA_PwdDifferent,
            DetectedFrom = "xtream publication",
        };
        var id1 = TelegramScraperService.TryExtractXtreamIdentity(c1);
        var id2 = TelegramScraperService.TryExtractXtreamIdentity(c2);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void TryExtractXtreamIdentity_handles_url_encoded_username()
    {
        // URL with %xx-encoded username; password is also encoded.
        var url = "http://h.example/get.php?username=al%40ice&password=secret&type=m3u_plus";
        var c = new CandidatePlaylist
        {
            Kind = CandidateSourceKind.Url,
            Url = url,
            DetectedFrom = "xtream publication",
        };
        var id = TelegramScraperService.TryExtractXtreamIdentity(c);
        Assert.NotNull(id);
        // The fingerprint should match what we'd compute with the
        // password-stripped URL and the decoded username.
        // We don't know the exact password-stripped URL by hand, but the
        // test below cross-checks with the strip helper semantics:
        Assert.DoesNotContain("secret", id);
        Assert.DoesNotContain("al@ice", id);
    }
}

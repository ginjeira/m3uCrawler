using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-DEDUP (2026-10-01) — testes puros do <see cref="ValidationKeyRegistry"/>
/// (sem rede, sem I/O). O registo é o contrato que permite a
/// <see cref="AccountValidator"/> evitar GETs físicos redundantes dentro de um
/// run, reutilizando apenas conhecimento <c>Working</c>.
/// </summary>
public class ValidationKeyRegistryTests
{
    [Fact]
    public void TryBegin_returns_true_first_time_and_false_for_repeated_key()
    {
        var registry = new ValidationKeyRegistry();

        Assert.True(registry.TryBegin("sfp1-key-a"));
        Assert.False(registry.TryBegin("sfp1-key-a"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void IsKnownWorking_is_false_before_mark_true_after_mark()
    {
        var registry = new ValidationKeyRegistry();
        var key = "sfp1-key-b";

        Assert.False(registry.IsKnownWorking(key));

        registry.TryBegin(key);
        Assert.False(registry.IsKnownWorking(key));

        registry.MarkWorking(key);
        Assert.True(registry.IsKnownWorking(key));
        Assert.True(registry.TryGetState(key, out var state));
        Assert.Equal(ValidationKeyState.Working, state);
    }

    [Fact]
    public void MarkFailed_deterministic_is_terminal_and_not_known_working()
    {
        var registry = new ValidationKeyRegistry();
        var key = "sfp1-key-c";

        registry.MarkFailed(key, StreamFailureKind.Deterministic);

        Assert.True(registry.TryGetState(key, out var state));
        Assert.Equal(ValidationKeyState.FailedTerminal, state);
        Assert.False(registry.IsKnownWorking(key));
        Assert.Equal(1, registry.FailedKeyCount);
        Assert.Equal(0, registry.WorkingKeyCount);
    }

    [Fact]
    public void MarkFailed_http_status_5xx_is_transient_and_not_known_working()
    {
        var registry = new ValidationKeyRegistry();
        var key = "sfp1-key-d";

        registry.MarkFailed(key, StreamFailureKind.HttpStatus5xx);

        Assert.True(registry.TryGetState(key, out var state));
        Assert.Equal(ValidationKeyState.FailedTransient, state);
        Assert.False(registry.IsKnownWorking(key));
        Assert.Equal(1, registry.FailedKeyCount);
    }

    [Fact]
    public void InProgress_state_is_not_known_working_but_TryGetState_true()
    {
        var registry = new ValidationKeyRegistry();
        var key = "sfp1-key-e";

        Assert.True(registry.TryBegin(key));

        Assert.True(registry.TryGetState(key, out var state));
        Assert.Equal(ValidationKeyState.InProgress, state);
        Assert.False(registry.IsKnownWorking(key));
    }

    [Fact]
    public void Null_and_empty_keys_are_safe_and_create_no_entries()
    {
        var registry = new ValidationKeyRegistry();

        Assert.False(registry.TryBegin(null));
        Assert.False(registry.TryBegin(string.Empty));
        Assert.False(registry.IsKnownWorking(null));
        Assert.False(registry.IsKnownWorking(string.Empty));
        Assert.False(registry.TryGetState(null, out var state));
        Assert.Equal(ValidationKeyState.InProgress, state);

        registry.MarkWorking(null);
        registry.MarkWorking(string.Empty);
        registry.MarkFailed(null, StreamFailureKind.Deterministic);
        registry.MarkFailed(string.Empty, StreamFailureKind.Network);

        Assert.Equal(0, registry.Count);
        Assert.Equal(0, registry.WorkingKeyCount);
        Assert.Equal(0, registry.FailedKeyCount);
    }

    [Fact]
    public void Reset_clears_entries_and_counters()
    {
        var registry = new ValidationKeyRegistry();
        registry.TryBegin("sfp1-key-f");
        registry.MarkWorking("sfp1-key-g");
        registry.RecordPhysical();
        registry.RecordPhysical();
        registry.RecordReuse();

        Assert.Equal(2, registry.Count);
        Assert.Equal(2, registry.PhysicalCount);
        Assert.Equal(1, registry.ReuseCount);

        registry.Reset();

        Assert.Equal(0, registry.Count);
        Assert.Equal(0, registry.PhysicalCount);
        Assert.Equal(0, registry.ReuseCount);
        Assert.False(registry.IsKnownWorking("sfp1-key-g"));
    }

    [Fact]
    public void RecordPhysical_and_RecordReuse_increment_counters()
    {
        var registry = new ValidationKeyRegistry();

        registry.RecordPhysical();
        registry.RecordPhysical();
        registry.RecordPhysical();
        registry.RecordReuse();
        registry.RecordReuse();

        Assert.Equal(3, registry.PhysicalCount);
        Assert.Equal(2, registry.ReuseCount);
    }

    [Fact]
    public void Count_WorkingKeyCount_FailedKeyCount_are_accurate_for_mixed_set()
    {
        var registry = new ValidationKeyRegistry();

        registry.MarkWorking("sfp1-working-1");
        registry.MarkWorking("sfp1-working-2");
        registry.MarkFailed("sfp1-terminal", StreamFailureKind.Deterministic);
        registry.MarkFailed("sfp1-transient", StreamFailureKind.Timeout);
        registry.TryBegin("sfp1-in-progress");

        Assert.Equal(5, registry.Count);
        Assert.Equal(2, registry.WorkingKeyCount);
        Assert.Equal(2, registry.FailedKeyCount);
    }

    [Fact]
    public void Concurrent_TryBegin_on_same_key_has_exactly_one_winner()
    {
        var registry = new ValidationKeyRegistry();
        var winners = 0;

        Parallel.For(0, 200, _ =>
        {
            if (registry.TryBegin("sfp1-contended")) Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Concurrent_mark_and_read_on_many_keys_is_consistent()
    {
        const int keyCount = 64;
        var registry = new ValidationKeyRegistry();
        var keys = Enumerable.Range(0, keyCount).Select(i => $"sfp1-concurrent-{i}").ToArray();

        Parallel.For(0, keys.Length, i =>
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                registry.MarkWorking(keys[i]);
                _ = registry.IsKnownWorking(keys[i]);
            }
        });

        Assert.Equal(keyCount, registry.WorkingKeyCount);
        foreach (var key in keys)
        {
            Assert.True(registry.IsKnownWorking(key));
        }
    }
}

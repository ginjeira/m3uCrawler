using m3uCrawler.Services.Preview;

namespace m3uCrawler.Tests;

/// <summary>
/// Handle de processo falso para testes do motor de preview — sem ffmpeg real.
/// </summary>
internal sealed class FakeProcessHandle : IProcessHandle
{
    private readonly object _gate = new();
    private bool _exited;
    private int? _exitCode;

    public FakeProcessHandle(string stderrTail = "")
    {
        StderrTail = stderrTail;
    }

    public bool HasExited
    {
        get { lock (_gate) return _exited; }
    }

    public int? ExitCode
    {
        get { lock (_gate) return _exitCode; }
    }

    public bool Killed { get; private set; }

    public string StderrTail { get; set; }

    public Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        lock (_gate) return Task.FromResult(_exited);
    }

    public void Kill()
    {
        lock (_gate)
        {
            Killed = true;
            _exited = true;
            _exitCode ??= -1;
        }
    }

    /// <summary>Simula um processo que termina sozinho com o código dado.</summary>
    public void MarkExited(int exitCode)
    {
        lock (_gate)
        {
            _exited = true;
            _exitCode = exitCode;
        }
    }

    public void Dispose() { }
}

/// <summary>
/// <see cref="IProcessRunner"/> falso: regista invocações e devolve resultados configuráveis,
/// permitindo testar o motor sem <c>ffmpeg</c>/<c>ffprobe</c> e sem rede.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly object _gate = new();

    public List<(string Exe, IReadOnlyList<string> Args, string WorkingDir)> Starts { get; } = new();

    public List<(string Exe, IReadOnlyList<string> Args, TimeSpan Timeout)> Runs { get; } = new();

    public List<FakeProcessHandle> Handles { get; } = new();

    public int RunCallCount { get; private set; }

    /// <summary>Handler de <c>RunAsync</c> (ffprobe). Default: exit 0 com JSON vazio.</summary>
    public Func<IReadOnlyList<string>, ProcessResult>? RunHandler { get; set; }

    public IProcessHandle Start(string exe, IReadOnlyList<string> args, string workingDir)
    {
        var handle = new FakeProcessHandle();
        lock (_gate)
        {
            Starts.Add((exe, args, workingDir));
            Handles.Add(handle);
        }
        return handle;
    }

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        lock (_gate)
        {
            RunCallCount++;
            Runs.Add((exe, args, timeout));
        }

        var result = RunHandler?.Invoke(args) ?? new ProcessResult(0, "{}", string.Empty, false);
        return Task.FromResult(result);
    }
}

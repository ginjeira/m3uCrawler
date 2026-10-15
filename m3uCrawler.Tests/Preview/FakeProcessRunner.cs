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
    private int _exitCodeOnExit = -1;
    private string _stderr = string.Empty;

    public FakeProcessHandle(string stderrTail = "")
    {
        _stderr = stderrTail;
    }

    public bool HasExited
    {
        get { lock (_gate) return _exited; }
    }

    // Só devolve o código quando o processo já saiu: um processo vivo não tem exit code.
    public int? ExitCode
    {
        get { lock (_gate) return _exited ? _exitCode : null; }
    }

    public bool Killed { get; private set; }

    // Simula saída tardia: o stderr só fica visível depois de o processo sair. Ler antes
    // do exit devolve vazio, tal como o Process real enquanto o pipe ainda está aberto.
    public string StderrTail
    {
        get { lock (_gate) return _exited ? _stderr : string.Empty; }
        set { lock (_gate) _stderr = value; }
    }

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
            _exitCode ??= _exitCodeOnExit;
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

    /// <summary>
    /// Agenda uma saída espontânea após <paramref name="delay"/> (o stderr configurado só
    /// passa a ser visível nesse momento), simulando um ffmpeg que falha sozinho.
    /// </summary>
    public void ScheduleExit(int exitCode, TimeSpan delay)
    {
        lock (_gate) _exitCodeOnExit = exitCode;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay).ConfigureAwait(false); } catch { }
            MarkExited(exitCode);
        });
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

    /// <summary>
    /// Quando <c>true</c> (default), <see cref="Start"/> materializa imediatamente o artefacto
    /// esperado no dir da sessão (<c>index.m3u8</c> para HLS, <c>thumb.jpg</c> para snapshot),
    /// simulando um ffmpeg que produz o ficheiro. Desligar para simular um processo que nunca
    /// gera o artefacto.
    /// </summary>
    public bool AutoCreateArtifacts { get; set; } = true;

    /// <summary>
    /// Substitui a materialização default. Invocado (fire-and-forget) em <see cref="Start"/>
    /// com os args e o working dir da sessão — permite simular atraso na criação do artefacto
    /// ou criá-lo condicionalmente (ex.: só snapshot).
    /// </summary>
    public Func<IReadOnlyList<string>, string, Task>? ArtifactWriter { get; set; }

    /// <summary>
    /// Invocado em <see cref="Start"/> com os args e o handle acabado de criar (antes da
    /// materialização do artefacto). Permite configurar stderr/saída tardia por processo.
    /// </summary>
    public Action<IReadOnlyList<string>, FakeProcessHandle>? HandleConfigurator { get; set; }

    public IProcessHandle Start(string exe, IReadOnlyList<string> args, string workingDir)
    {
        var handle = new FakeProcessHandle();
        lock (_gate)
        {
            Starts.Add((exe, args, workingDir));
            Handles.Add(handle);
        }

        HandleConfigurator?.Invoke(args, handle);

        var writer = ArtifactWriter;
        if (writer is not null)
        {
            _ = writer(args, workingDir);
        }
        else if (AutoCreateArtifacts)
        {
            WriteDefaultArtifact(args, workingDir);
        }

        return handle;
    }

    /// <summary>
    /// Escreve, no dir da sessão, o primeiro artefacto referenciado nos args
    /// (<c>index.m3u8</c> tem prioridade sobre <c>thumb.jpg</c>).
    /// </summary>
    private static void WriteDefaultArtifact(IReadOnlyList<string> args, string workingDir)
    {
        var target = args.FirstOrDefault(a => a.EndsWith("index.m3u8", StringComparison.Ordinal))
                     ?? args.FirstOrDefault(a => a.EndsWith("thumb.jpg", StringComparison.Ordinal));
        if (target is not null)
        {
            WriteArtifactFile(target, workingDir);
        }
    }

    /// <summary>Cria <paramref name="path"/> (relativo a <paramref name="workingDir"/> se preciso) com conteúdo não-vazio.</summary>
    public static void WriteArtifactFile(string path, string workingDir)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(workingDir, path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(full, full.EndsWith(".m3u8", StringComparison.Ordinal)
            ? "#EXTM3U\n#EXT-X-VERSION:3\n"
            : "jpeg-bytes");
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

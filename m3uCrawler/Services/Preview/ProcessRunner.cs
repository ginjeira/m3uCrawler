using System.Diagnostics;
using System.Text;

namespace m3uCrawler.Services.Preview;

/// <summary>
/// Implementação de <see cref="IProcessRunner"/> sobre <see cref="Process"/>.
///
/// Regra absoluta: esta classe NUNCA regista/imprime o URL nem a linha de argumentos
/// completos. O único texto exposto é <see cref="IProcessHandle.StderrTail"/>, sempre
/// sanitizado com <c>CredentialSanitizer.SanitizeSensitiveText</c> e truncado.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int StderrTailLimit = 8192;

    /// <inheritdoc />
    public IProcessHandle Start(string exe, IReadOnlyList<string> args, string workingDir)
    {
        var psi = BuildStartInfo(exe, args, string.IsNullOrWhiteSpace(workingDir) ? null : workingDir, redirectStdout: false);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Falha ao iniciar o processo de preview.");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }
        return new ProcessHandle(process, StderrTailLimit);
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = BuildStartInfo(exe, args, workingDirectory: null, redirectStdout: true);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Falha ao iniciar o processo de preview.");
        }

        var stdoutTask = ReadToEndSafeAsync(process.StandardOutput, ct);
        var stderrTask = ReadBoundedAsync(process.StandardError, StderrTailLimit, ct);

        var timedOut = false;
        var effectiveTimeout = timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(effectiveTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                timedOut = true;
                TryKill(process);
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* processo já morto */ }
            }
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var exitCode = TryGetExitCode(process);
        return new ProcessResult(exitCode, stdout, stderr, timedOut);
    }

    private static ProcessStartInfo BuildStartInfo(string exe, IReadOnlyList<string> args, string? workingDirectory, bool redirectStdout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = true,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        return psi;
    }

    private static async Task<string> ReadToEndSafeAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var buffer = new char[1024];
        var builder = new StringBuilder(limit);
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                builder.Append(buffer, 0, read);
                if (builder.Length > limit)
                {
                    builder.Remove(0, builder.Length - limit);
                }
            }
        }
        catch
        {
            // processo morto / stream fechado durante leitura — devolve o que houver
        }
        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { /* já terminou ou inacessível */ }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private sealed class ProcessHandle : IProcessHandle
    {
        private readonly Process _process;
        private readonly int _limit;
        private readonly StringBuilder _stderr = new();
        private readonly object _gate = new();
        private readonly Task _stderrPump;

        public ProcessHandle(Process process, int limit)
        {
            _process = process;
            _limit = limit;
            _stderrPump = Task.Run(PumpStderrAsync);
        }

        private async Task PumpStderrAsync()
        {
            try
            {
                var buffer = new char[1024];
                int read;
                while ((read = await _process.StandardError.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                {
                    lock (_gate)
                    {
                        _stderr.Append(buffer, 0, read);
                        if (_stderr.Length > _limit)
                        {
                            _stderr.Remove(0, _stderr.Length - _limit);
                        }
                    }
                }
            }
            catch
            {
                // processo terminou / pipe fechado
            }
        }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch { return true; }
            }
        }

        public int? ExitCode
        {
            get
            {
                try { return _process.HasExited ? _process.ExitCode : null; }
                catch { return null; }
            }
        }

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            if (HasExited)
            {
                return true;
            }

            var delay = timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout;
            try
            {
                var exitTask = _process.WaitForExitAsync();
                var completed = await Task.WhenAny(exitTask, Task.Delay(delay)).ConfigureAwait(false);
                return completed == exitTask || HasExited;
            }
            catch
            {
                return true;
            }
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { /* processo já terminou */ }
        }

        public string StderrTail
        {
            get
            {
                string raw;
                lock (_gate)
                {
                    raw = _stderr.ToString();
                }
                return CredentialSanitizer.SanitizeSensitiveText(raw);
            }
        }

        public void Dispose()
        {
            try { _stderrPump.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
            _process.Dispose();
        }
    }
}

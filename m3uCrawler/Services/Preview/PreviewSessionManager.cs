namespace m3uCrawler.Services.Preview;

/// <summary>
/// Motor de sessões de preview de streams. Orquestra resolução da referência, sondagem
/// (ffprobe), arranque do remux HLS ou captura de snapshot (ffmpeg), controlo de concorrência,
/// expiração por inactividade/TTL e encerramento.
///
/// Toda a I/O de estado é protegida por um lock. É robusto a processos que terminam sozinhos
/// (não assume que o handle ainda está vivo) e nunca lança por sessões desconhecidas.
///
/// Nenhuma implementação regista o URL/credenciais: os args são passados ao
/// <see cref="IProcessRunner"/> e o único texto de diagnóstico é <see cref="IProcessHandle.StderrTail"/>
/// (já sanitizado).
/// </summary>
public sealed class PreviewSessionManager : IAsyncDisposable
{
    /// <summary>Instante (do início do stream) usado para capturar o frame de snapshot.</summary>
    private static readonly TimeSpan SnapshotPosition = TimeSpan.FromSeconds(1);

    private readonly PreviewOptions _options;
    private readonly IProcessRunner _runner;
    private readonly StreamProbeService _probe;
    private readonly Func<string, CancellationToken, Task<StreamReference?>> _resolver;
    private readonly string _root;
    private readonly SemaphoreSlim _slots;
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _sweeper;
    private bool _disposed;

    public PreviewSessionManager(
        PreviewOptions options,
        IProcessRunner runner,
        StreamProbeService probe,
        Func<string, CancellationToken, Task<StreamReference?>> resolver,
        string previewRootDir)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

        if (string.IsNullOrWhiteSpace(previewRootDir))
        {
            throw new ArgumentException("previewRootDir é obrigatório.", nameof(previewRootDir));
        }
        _root = Path.GetFullPath(previewRootDir);
        Directory.CreateDirectory(_root);

        var capacity = Math.Max(1, _options.MaxConcurrentSessions);
        _slots = new SemaphoreSlim(capacity, capacity);
        _sweeper = Task.Run(SweepLoopAsync);
    }

    /// <summary>Número de sessões activas (diagnóstico/testes).</summary>
    internal int ActiveSessionCount
    {
        get { lock (_gate) return _sessions.Count; }
    }

    /// <summary>Indica se existe uma sessão activa com o id dado (diagnóstico/testes).</summary>
    internal bool HasSession(string sessionId)
    {
        lock (_gate) return sessionId is not null && _sessions.ContainsKey(sessionId);
    }

    /// <summary>
    /// Resolve <paramref name="reference"/> e inicia uma sessão de preview. Devolve <c>null</c>
    /// se a referência não for conhecida (a camada HTTP decide 404). Devolve um resultado com
    /// <c>Reason="busy"</c> quando o limite de sessões concorrentes está esgotado.
    /// </summary>
    public async Task<PreviewSessionStartResult?> StartAsync(string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var resolved = await _resolver(reference, ct).ConfigureAwait(false);
        if (resolved is null || string.IsNullOrWhiteSpace(resolved.Url))
        {
            return null;
        }

        if (!await _slots.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new PreviewSessionStartResult(string.Empty, string.Empty, null, null, null, false, "busy");
        }

        SessionState? session = null;
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var dir = Path.Combine(_root, id);
            Directory.CreateDirectory(dir);

            var probe = await _probe.ProbeAsync(resolved.Url, ct).ConfigureAwait(false);
            var playable = probe?.PlayableByBrowser == true;

            IProcessHandle handle;
            string? manifestUrl = null;
            string? thumbnailUrl;
            string kind;

            if (playable)
            {
                handle = _runner.Start(
                    _options.FfmpegPath,
                    FfmpegArguments.HlsRemux(resolved.Url, dir, _options.UserAgent),
                    dir);
                manifestUrl = $"/api/preview/{id}/index.m3u8";
                thumbnailUrl = $"/api/preview/{id}/thumb.jpg";
                kind = "hls";
            }
            else if (_options.TranscodeNonPlayable)
            {
                // Opt-in: o stream não é reproduzível no browser, mas o operador
                // aceita o custo de CPU do transcode H.264/AAC para HLS.
                handle = _runner.Start(
                    _options.FfmpegPath,
                    FfmpegArguments.HlsTranscode(resolved.Url, dir, _options.UserAgent),
                    dir);
                manifestUrl = $"/api/preview/{id}/index.m3u8";
                thumbnailUrl = $"/api/preview/{id}/thumb.jpg";
                kind = "hls";
            }
            else
            {
                handle = _runner.Start(
                    _options.FfmpegPath,
                    FfmpegArguments.Thumbnail(resolved.Url, Path.Combine(dir, "thumb.jpg"), _options.UserAgent, SnapshotPosition),
                    dir);
                thumbnailUrl = $"/api/preview/{id}/thumb.jpg";
                kind = "snapshot";
            }

            var now = DateTimeOffset.UtcNow;
            session = new SessionState
            {
                Id = id,
                Dir = dir,
                CreatedUtc = now,
                LastActivityUtc = now,
                Process = handle,
            };

            bool disposed;
            lock (_gate)
            {
                disposed = _disposed;
                if (!disposed)
                {
                    _sessions[id] = session;
                }
            }

            if (disposed)
            {
                await TeardownAsync(session).ConfigureAwait(false);
                return null;
            }

            return new PreviewSessionStartResult(
                id,
                kind,
                manifestUrl,
                thumbnailUrl,
                probe,
                playable,
                null);
        }
        catch
        {
            if (session is not null)
            {
                lock (_gate) _sessions.Remove(session.Id);
                await TeardownAsync(session).ConfigureAwait(false);
            }
            else
            {
                ReleaseSlot(null);
            }
            throw;
        }
    }

    /// <summary>
    /// Regista actividade recente numa sessão (renova o idle timeout). Inofensivo para sessões
    /// desconhecidas.
    /// </summary>
    public void Touch(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.LastActivityUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Resolve um ficheiro servível dentro do directório da sessão. Rejeita path traversal,
    /// caminhos absolutos e sessões desconhecidas. Em sucesso preenche <paramref name="fullPath"/>
    /// e <paramref name="contentType"/> e regista actividade.
    /// </summary>
    public bool TryResolveFile(string sessionId, string relativePath, out string fullPath, out string contentType)
    {
        fullPath = string.Empty;
        contentType = "application/octet-stream";

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        SessionState? session;
        lock (_gate)
        {
            _sessions.TryGetValue(sessionId, out session);
        }
        if (session is null)
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(session.Dir, relativePath));
        }
        catch
        {
            return false;
        }

        var baseDir = Path.GetFullPath(session.Dir);
        var prefix = baseDir.EndsWith(Path.DirectorySeparatorChar)
            ? baseDir
            : baseDir + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!File.Exists(candidate))
        {
            return false;
        }

        fullPath = candidate;
        contentType = ContentTypeFor(candidate);
        Touch(sessionId);
        return true;
    }

    /// <summary>Termina a sessão (mata o processo e remove o directório). Idempotente.</summary>
    public async Task StopAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        SessionState? session;
        lock (_gate)
        {
            _sessions.Remove(sessionId, out session);
        }
        if (session is null)
        {
            return;
        }

        await TeardownAsync(session).ConfigureAwait(false);
    }

    /// <summary>Mata todas as sessões, remove directorizações e para o sweeper de fundo.</summary>
    public async ValueTask DisposeAsync()
    {
        List<SessionState> sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            sessions = new List<SessionState>(_sessions.Values);
            _sessions.Clear();
        }

        _shutdown.Cancel();
        try { await _sweeper.ConfigureAwait(false); } catch { /* cancelamento esperado */ }

        foreach (var session in sessions)
        {
            await TeardownAsync(session).ConfigureAwait(false);
        }

        _shutdown.Dispose();
        _slots.Dispose();
    }

    private async Task SweepLoopAsync()
    {
        var tick = ComputeTick();
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(tick, _shutdown.Token).ConfigureAwait(false);
                await SweepExpiredAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private TimeSpan ComputeTick()
    {
        var idle = _options.IdleTimeout > TimeSpan.Zero ? _options.IdleTimeout : TimeSpan.FromMilliseconds(250);
        var max = _options.MaxDuration > TimeSpan.Zero ? _options.MaxDuration : idle;
        var quarter = Math.Min(idle.TotalMilliseconds, max.TotalMilliseconds) / 4.0;
        var clamped = Math.Clamp(quarter, 25.0, 5000.0);
        return TimeSpan.FromMilliseconds(clamped);
    }

    private async Task SweepExpiredAsync()
    {
        var now = DateTimeOffset.UtcNow;
        List<SessionState>? expired = null;

        lock (_gate)
        {
            foreach (var session in _sessions.Values)
            {
                var idleExpired = now - session.LastActivityUtc > _options.IdleTimeout;
                var ttlExpired = now - session.CreatedUtc > _options.MaxDuration;
                if (idleExpired || ttlExpired)
                {
                    (expired ??= new List<SessionState>()).Add(session);
                }
            }

            if (expired is not null)
            {
                foreach (var session in expired)
                {
                    _sessions.Remove(session.Id);
                }
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var session in expired)
        {
            await TeardownAsync(session).ConfigureAwait(false);
        }
    }

    private async Task TeardownAsync(SessionState session)
    {
        try
        {
            var process = session.Process;
            if (process is not null)
            {
                process.Kill();
                try { await process.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* processo já morto */ }

                try { process.Dispose(); } catch { /* nunca propagar falhas de teardown */ }
            }
        }
        catch { /* nunca propagar falhas de teardown */ }

        try
        {
            if (Directory.Exists(session.Dir))
            {
                Directory.Delete(session.Dir, recursive: true);
            }
        }
        catch { /* directório em uso — melhor esforço */ }

        ReleaseSlot(session);
    }

    private void ReleaseSlot(SessionState? session)
    {
        if (session is not null)
        {
            if (session.SlotReleased)
            {
                return;
            }
            session.SlotReleased = true;
        }

        try { _slots.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private static string ContentTypeFor(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".m3u8" => "application/vnd.apple.mpegurl",
            ".ts" => "video/mp2t",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".m4s" => "video/iso.segment",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        };
    }

    private sealed class SessionState
    {
        public required string Id { get; init; }
        public required string Dir { get; init; }
        public required DateTimeOffset CreatedUtc { get; init; }
        public DateTimeOffset LastActivityUtc { get; set; }
        public IProcessHandle? Process { get; init; }
        public bool SlotReleased { get; set; }
    }
}

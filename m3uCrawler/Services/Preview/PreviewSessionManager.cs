using System.Globalization;
using m3uCrawler.Services;

namespace m3uCrawler.Services.Preview;

/// <summary>
/// Motor de sessões de preview de streams. Orquestra resolução da referência, sondagem
/// (ffprobe), arranque do remux HLS ou captura de snapshot (ffmpeg), controlo de concorrência,
/// expiração por inactividade/TTL e encerramento.
///
/// Toda a I/O de estado é protegida por um lock. É robusto a processos que terminam sozinhos
/// (não assume que o handle ainda está vivo) e nunca lança por sessões desconhecidas.
///
/// <para>
/// <see cref="StartAsync"/> só devolve sucesso <b>depois</b> de o artefacto de arranque
/// (<c>index.m3u8</c> para HLS, <c>thumb.jpg</c> para snapshot) existir com conteúdo —
/// caso contrário o <c>hls.js</c> pede o manifest cedo demais e apanha 404. Em falha de HLS
/// tenta degradar para snapshot; se também falhar devolve <c>Reason="preview-start-timeout"</c>.
/// </para>
///
/// Nenhuma implementação regista o URL/credenciais: os args são passados ao
/// <see cref="IProcessRunner"/> e o único texto de diagnóstico é <see cref="IProcessHandle.StderrTail"/>
/// (já sanitizado).
/// </summary>
public sealed class PreviewSessionManager : IAsyncDisposable
{
    /// <summary>Instante (do início do stream) usado para capturar o frame de snapshot.</summary>
    private static readonly TimeSpan SnapshotPosition = TimeSpan.FromSeconds(1);

    /// <summary>Intervalo de amostragem na espera pelo artefacto de arranque.</summary>
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Limite de caracteres do stderr registado numa falha (defesa contra ruído).</summary>
    private const int StderrLogLimit = 512;

    private readonly PreviewOptions _options;
    private readonly IProcessRunner _runner;
    private readonly StreamProbeService _probe;
    private readonly Func<string, CancellationToken, Task<StreamReference?>> _resolver;
    private readonly string _root;
    private readonly SemaphoreSlim _slots;
    private readonly Action<string> _log;
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
        string previewRootDir,
        Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _log = log ?? Console.WriteLine;

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
    ///
    /// <para>
    /// Só devolve sucesso depois de o artefacto de arranque existir com conteúdo. Se o HLS
    /// falhar, tenta degradar para snapshot (<c>Reason="hls-failed-snapshot"</c>); se também
    /// falhar, devolve <c>Reason="preview-start-timeout"</c> sem manifest/thumbnail.
    /// </para>
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
        IProcessHandle? handle = null;
        var id = string.Empty;
        string? dir = null;
        try
        {
            id = Guid.NewGuid().ToString("N");
            dir = Path.Combine(_root, id);
            Directory.CreateDirectory(dir);

            var probe = await _probe.ProbeAsync(resolved.Url, ct).ConfigureAwait(false);
            if (probe is null)
            {
                // Sem sondagem assumimos H.264/AAC (a maioria do catálogo): é preferível
                // tentar HLS a degradar silenciosamente para snapshot. Sem URL no log.
                _log($"🎬 preview {id} probe indisponível; assumindo playable");
            }
            var playable = probe?.PlayableByBrowser ?? true;

            // Caminho primário: HLS (remux se playable, transcode opt-in se não playable);
            // caso contrário snapshot directo.
            var primaryKind = playable || _options.TranscodeNonPlayable ? "hls" : "snapshot";
            var primaryArgs = primaryKind == "hls"
                ? (playable
                    ? FfmpegArguments.HlsRemux(resolved.Url, dir, _options.UserAgent)
                    : FfmpegArguments.HlsTranscode(resolved.Url, dir, _options.UserAgent))
                : FfmpegArguments.Thumbnail(resolved.Url, Path.Combine(dir, "thumb.jpg"), _options.UserAgent, SnapshotPosition);

            handle = _runner.Start(_options.FfmpegPath, primaryArgs, dir);
            _log($"🎬 preview {id} kind={primaryKind} playable={playable}");

            var artifactPath = primaryKind == "hls"
                ? Path.Combine(dir, "index.m3u8")
                : Path.Combine(dir, "thumb.jpg");

            var ready = await WaitForArtifactAsync(handle, artifactPath, _options.ReadyTimeout, ct).ConfigureAwait(false);

            var kind = primaryKind;
            string? reason = null;

            if (!ready && primaryKind == "hls")
            {
                // O HLS não produziu o manifest: registar diagnóstico e tentar snapshot
                // no mesmo directório antes de desistir.
                LogFailure(id, handle);
                await StopHandleAsync(handle).ConfigureAwait(false);
                handle = null;

                kind = "snapshot";
                handle = _runner.Start(
                    _options.FfmpegPath,
                    FfmpegArguments.Thumbnail(resolved.Url, Path.Combine(dir, "thumb.jpg"), _options.UserAgent, SnapshotPosition),
                    dir);
                _log($"🎬 preview {id} kind=snapshot playable={playable}");

                var thumbPath = Path.Combine(dir, "thumb.jpg");
                ready = await WaitForArtifactAsync(handle, thumbPath, _options.ReadyTimeout, ct).ConfigureAwait(false);
                if (ready)
                {
                    reason = "hls-failed-snapshot";
                }
            }

            if (!ready)
            {
                if (kind == "snapshot")
                {
                    LogFailure(id, handle);
                }
                await CleanupProcessAndDirAsync(handle, dir).ConfigureAwait(false);
                ReleaseSlot(null);
                _log($"🎬 preview {id} terminada (preview-start-timeout)");
                return new PreviewSessionStartResult(
                    string.Empty, string.Empty, null, null, probe, playable, "preview-start-timeout");
            }

            var manifestUrl = kind == "hls" ? $"/api/preview/{id}/index.m3u8" : null;
            var thumbnailUrl = $"/api/preview/{id}/thumb.jpg";

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
                await TeardownAsync(session, "disposed").ConfigureAwait(false);
                return null;
            }

            return new PreviewSessionStartResult(id, kind, manifestUrl, thumbnailUrl, probe, playable, reason);
        }
        catch
        {
            if (session is not null)
            {
                lock (_gate) _sessions.Remove(session.Id);
                await TeardownAsync(session, "start-error").ConfigureAwait(false);
            }
            else
            {
                await CleanupProcessAndDirAsync(handle, dir).ConfigureAwait(false);
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

        await TeardownAsync(session, "stop").ConfigureAwait(false);
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
            await TeardownAsync(session, "dispose").ConfigureAwait(false);
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
        List<(SessionState Session, string Reason)>? expired = null;

        lock (_gate)
        {
            foreach (var session in _sessions.Values)
            {
                var idleExpired = now - session.LastActivityUtc > _options.IdleTimeout;
                var ttlExpired = now - session.CreatedUtc > _options.MaxDuration;
                if (idleExpired || ttlExpired)
                {
                    var reason = ttlExpired ? "max-duration" : "idle-timeout";
                    (expired ??= new List<(SessionState, string)>()).Add((session, reason));
                }
            }

            if (expired is not null)
            {
                foreach (var (session, _) in expired)
                {
                    _sessions.Remove(session.Id);
                }
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var (session, reason) in expired)
        {
            await TeardownAsync(session, reason).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Espera (com poll) até o artefacto de arranque existir com conteúdo ou o processo
    /// terminar. Verifica o artefacto antes de <see cref="IProcessHandle.HasExited"/> para
    /// não falhar um processo one-shot (snapshot) que já escreveu o ficheiro e saiu.
    /// </summary>
    private async Task<bool> WaitForArtifactAsync(IProcessHandle handle, string artifactPath, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);
        while (true)
        {
            if (IsArtifactReady(artifactPath))
            {
                return true;
            }
            if (handle.HasExited)
            {
                return false;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var delay = remaining < ReadyPollInterval ? remaining : ReadyPollInterval;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static bool IsArtifactReady(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Mata/termina o processo (se vivo), espera e liberta o handle. Nunca lança.</summary>
    private static async Task StopHandleAsync(IProcessHandle? handle)
    {
        if (handle is null)
        {
            return;
        }

        try { handle.Kill(); } catch { /* processo já morto */ }
        try { await handle.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* já morto */ }
        try { handle.Dispose(); } catch { /* nunca propagar falhas de teardown */ }
    }

    private static async Task CleanupProcessAndDirAsync(IProcessHandle? handle, string? dir)
    {
        await StopHandleAsync(handle).ConfigureAwait(false);

        try
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* directório em uso — melhor esforço */ }
    }

    private void LogFailure(string sessionId, IProcessHandle handle)
    {
        var exit = handle.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var tail = CredentialSanitizer.SanitizeSensitiveText(handle.StderrTail).Trim();
        if (tail.Length > StderrLogLimit)
        {
            tail = tail[..StderrLogLimit];
        }
        _log($"⚠️ preview {sessionId} falhou ({exit}) {tail}");
    }

    private async Task TeardownAsync(SessionState session, string reason)
    {
        await CleanupProcessAndDirAsync(session.Process, session.Dir).ConfigureAwait(false);
        ReleaseSlot(session);
        _log($"🎬 preview {session.Id} terminada ({reason})");
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

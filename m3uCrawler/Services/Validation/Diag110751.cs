using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace m3uCrawler.Services.Validation
{
    /// <summary>
    /// DIAGNOSTIC-110751 (2026-09-15): modulo de instrumentacao profunda para
    /// observar o percurso REAL da mensagem Telegram 110751 (e adjacentes) em
    /// PRODUCAO. Foi desenhado para responder ao pedido:
    ///
    ///   "DESCOBRIR POR QUE RAZÃO A APLICAÇÃO COMPLETA m3uCrawler NÃO
    ///    PROCESSA CORRECTAMENTE A MENSAGEM REAL 110751 EM PRODUÇÃO."
    ///
    /// Caracteristicas:
    ///   1. Activado exclusivamente pela variavel de ambiente
    ///      M3UCRAWLER_DIAG_110751 (qualquer valor nao-vazio activa).
    ///      Em CI/build normal, sem env var, e' zero-overhead: o ctor
    ///      nao inicializa o timer, o metodo Report() ignora silenciosamente.
    ///   2. Emite eventos [DIAG_110751] para stdout com pares chave=valor
    ///      facilmente pesquisa'veis por `grep messageId=110751`.
    ///   3. Correlaciona tudo por runId (random GUID por invocacao
    ///      SearchAndTestM3UInTelegramAsync) e por candidateId. Emite
    ///      tambem messageId quando conhecido.
    ///   4. Watchdog periodico: detecta operacoes que ficam sem progresso
    ///      por mais de WATCHDOG_QUIET_MS (default 30s) e emite
    ///      [DIAG_110751] WATCHDOG_TIMEOUT com ultima operacao conhecida.
    ///      NAO altera timeouts funcionais. NAO substitui HttpClient.Timeout
    ///      nem OverallTimeout. Apenas observa.
    ///   5. First-failure: a primeira exception, cancellation ou timeout
    ///      observavel e' capturada (FirstFailure) com tipo, mensagem,
    ///      stack trace e timestamp. Nao tenta continuar.
    ///   6. Watchdog pode chamar uma callback opcional para abortar o
    ///      processamento de diagnostico se for explicitamente pedido
    ///      (StopDiagOnFirstFailure). Em caso contrario, emite apenas
    ///      o timeout e continua a observar.
    ///   7. NUNCA escreve credenciais (passwords/tokens). URLs passam por
    ///      CredentialSanitizer.SanitizeUrl quando logadas.
    ///
    /// Esta classe e' desenhada para ser chamada de Multiplos locais do
    /// codigo de producao (TelegramScraperService.cs, M3uTesterService.cs,
    /// etc.) sem dependencias circulares. Cada call site emite eventos
    /// especificos sem duplicar instrumentacao ja existente.
    /// </summary>
    public static class Diag110751
    {
        public const string Tag = "[DIAG_110751]";
        private const int WATCHDOG_QUIET_MS = 30_000;
        private const int WATCHDOG_TICK_MS = 5_000;

        // Estado global (static). Em producao so' existe uma run em cada
        // momento para esta app; manter state global simplifica a
        // instrumentacao sem perder informacao relevante.
        private static readonly object _gate = new();
        private static bool _active;
        private static bool _stopping;
        private static string _runId = string.Empty;
        private static long _runStartTicks;
        private static long _lastEventTicks;
        private static string _lastOperation = "<none>";
        private static string _lastOperationDetail = "";
        private static int _lastStageMessageId;
        private static string _lastStageCandidateId = string.Empty;
        private static int _lastStageAccountIndex = -1;
        private static int _lastStageTotalAccounts = -1;
        private static Timer? _watchdog;
        private static long _watchdogTimeoutsEmitted;
        private static Exception? _firstFailure;
        private static long _firstFailureTicks;
        private static string _firstFailureOperation = string.Empty;
        private static int _eventsEmitted;
        // Lista de callbacks a invocar quando a primeira falha ocorre
        // (modo fail-fast). Permite que o caller faca cleanup controlado
        // (ex.: fechar channel writer) sem ter de polling.
        private static readonly List<Action<Exception>> _stopCallbacks = new();

        public static bool IsActive { get { lock (_gate) { return _active; } } }
        public static bool IsStopping { get { lock (_gate) { return _stopping; } } }
        public static string RunId { get { lock (_gate) { return _runId; } } }
        public static Exception? FirstFailure { get { lock (_gate) { return _firstFailure; } } }
        public static string FirstFailureOperation { get { lock (_gate) { return _firstFailureOperation; } } }

        /// <summary>
        /// Inicializa o diagnostico. Idempotente. Le a env var em cada chamada
        /// (cheap), e se a activacao passar de off->on, cria o watchdog.
        /// </summary>
        public static void EnsureInitialized()
        {
            lock (_gate)
            {
                if (_active) return;
                var env = Environment.GetEnvironmentVariable("M3UCRAWLER_DIAG_110751");
                if (string.IsNullOrEmpty(env)) return;
                _active = true;
                _stopping = false;
                _firstFailure = null;
                _firstFailureOperation = string.Empty;
                _firstFailureTicks = 0;
                _stopCallbacks.Clear();
                _watchdogTimeoutsEmitted = 0;
                _eventsEmitted = 0;
                _runId = "diag_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                _runStartTicks = Stopwatch.GetTimestamp();
                _lastEventTicks = _runStartTicks;
                _watchdog = new Timer(_ => WatchdogTick(), null, WATCHDOG_TICK_MS, WATCHDOG_TICK_MS);
                Emit("INIT", $"watchdogQuietMs={WATCHDOG_QUIET_MS} watchdogTickMs={WATCHDOG_TICK_MS}");
            }
        }

        /// <summary>
        /// Termina o diagnostico. Para o watchdog. Emite RUN_END.
        /// </summary>
        public static void Shutdown(string reason)
        {
            lock (_gate)
            {
                if (!_active) return;
                Emit("RUN_END", $"reason='{Trunc(reason, 200)}' stopping={_stopping} firstFailure={(_firstFailure == null ? "<none>" : SafeOpName(_firstFailureOperation))}");
                _watchdog?.Dispose();
                _watchdog = null;
                _stopCallbacks.Clear();
                // Permanecer _active=true para permitir consulta pos-shutdown
                // via FirstFailure.
            }
        }

        /// <summary>
        /// Repoe todo o estado do modulo para o estado inicial (inactivo).
        /// Usado apenas em testes que precisam de isolamento entre runs.
        /// Em producao NAO deve ser chamado.
        /// </summary>
        public static void ResetForTests()
        {
            lock (_gate)
            {
                _watchdog?.Dispose();
                _watchdog = null;
                _active = false;
                _stopping = false;
                _runId = string.Empty;
                _runStartTicks = 0;
                _lastEventTicks = 0;
                _lastOperation = "<none>";
                _lastOperationDetail = "";
                _lastStageMessageId = 0;
                _lastStageCandidateId = string.Empty;
                _lastStageAccountIndex = -1;
                _lastStageTotalAccounts = -1;
                _watchdogTimeoutsEmitted = 0;
                _firstFailure = null;
                _firstFailureTicks = 0;
                _firstFailureOperation = string.Empty;
                _eventsEmitted = 0;
                _stopCallbacks.Clear();
            }
        }

        /// <summary>
        /// Reporta um evento de diagnostico. Todos os parametros sao opcionais;
        /// string.Empty ou null sao simplesmente omitidos do output.
        ///
        /// operation: nome da operacao/fase em curso (ex.: "DOWNLOAD",
        ///   "BUILD_URL", "PROMOTE", "WRITE", "RESOLVE_FROM_HTML").
        /// stage: "START" / "END" / "ERROR" / "STUCK" / etc.
        /// detail: chave=valor (sem prefixo tag).
        /// </summary>
        public static void Report(
            string operation,
            string stage,
            string detail = "",
            int? messageId = null,
            string? candidateId = null,
            int? accountIndex = null,
            int? totalAccounts = null,
            Exception? exception = null,
            long? elapsedMs = null,
            long? sinceLastEventMs = null)
        {
            EnsureInitialized();
            if (!_active) return;

            var sb = new StringBuilder(256);
            sb.Append(Tag).Append(' ');
            sb.Append("runId=").Append(_runId);
            sb.Append(" op=").Append(SafeOpName(operation));
            sb.Append(" stage=").Append(SafeOpName(stage));
            if (messageId.HasValue) sb.Append(" messageId=").Append(messageId.Value);
            if (!string.IsNullOrEmpty(candidateId)) sb.Append(" candidateId=").Append(candidateId);
            if (accountIndex.HasValue) sb.Append(" accountIndex=").Append(accountIndex.Value);
            if (totalAccounts.HasValue) sb.Append(" totalAccounts=").Append(totalAccounts.Value);

            var nowTicks = Stopwatch.GetTimestamp();
            var totalMs = (long)((nowTicks - _runStartTicks) * 1000.0 / Stopwatch.Frequency);
            sb.Append(" elapsedMs=").Append(totalMs);
            if (elapsedMs.HasValue) sb.Append(" opElapsedMs=").Append(elapsedMs.Value);
            if (sinceLastEventMs.HasValue) sb.Append(" sinceLastEventMs=").Append(sinceLastEventMs.Value);
            if (!string.IsNullOrEmpty(detail))
            {
                sb.Append(' ').Append(Trunc(detail, 480));
            }

            lock (_gate)
            {
                _eventsEmitted++;
                _lastEventTicks = nowTicks;
                _lastOperation = operation;
                _lastOperationDetail = detail ?? "";
                if (messageId.HasValue) _lastStageMessageId = messageId.Value;
                if (!string.IsNullOrEmpty(candidateId)) _lastStageCandidateId = candidateId!;
                if (accountIndex.HasValue) _lastStageAccountIndex = accountIndex.Value;
                if (totalAccounts.HasValue) _lastStageTotalAccounts = totalAccounts.Value;
            }

            try
            {
                Console.WriteLine(sb.ToString());
                if (exception != null)
                {
                    RecordFirstFailure(operation, exception);
                    Console.WriteLine($"{Tag} EXCEPTION type={exception.GetType().FullName} message='{Trunc(exception.Message, 240)}' stackTrace='{Trunc(exception.StackTrace ?? "", 1500)}'");
                }
            }
            catch (Exception ex)
            {
                // Nunca propagar falha de logging.
                Console.Error.WriteLine($"{Tag} log-write-failed ex={ex.GetType().Name}");
            }
        }

        /// <summary>
        /// Marca a primeira falha observada. Idempotente: a primeira fica
        /// preservada. Emite FIRST_FAILURE com toda a informacao disponivel.
        /// Em modo de investigacao (DIAG_110751), tambem activa o modo
        /// fail-fast: set _stopping=true, invoca todas as stop-callbacks
        /// registadas para permitir que o caller faca cleanup controlado
        /// (fechar channel writer, cancelar token, etc.), e emite
        /// DIAGNOSTIC_STOPPED. Apos isto, novas accounts/candidates NAO
        /// devem ser processadas — IsStopping devolve true.
        ///
        /// A exception original NAO e' mascarada: o caller pode ainda
        /// observa-la via FirstFailure e decide como terminar.
        /// </summary>
        public static void RecordFirstFailure(string operation, Exception exception)
        {
            bool wasStopping;
            List<Action<Exception>> callbacks;
            lock (_gate)
            {
                if (!_active) return;
                wasStopping = _stopping;
                if (_firstFailure != null) return;
                _firstFailure = exception;
                _firstFailureTicks = Stopwatch.GetTimestamp();
                _firstFailureOperation = operation;
                _stopping = true;
                // Snapshot the callback list to invoke outside the lock.
                callbacks = new List<Action<Exception>>(_stopCallbacks);
            }
            Console.WriteLine($"{Tag} FIRST_FAILURE op='{SafeOpName(operation)}' type={exception.GetType().FullName} message='{Trunc(exception.Message, 240)}' stackTrace='{Trunc(exception.StackTrace ?? "", 1500)}'");
            if (!wasStopping)
            {
                Console.WriteLine($"{Tag} DIAGNOSTIC_STOPPING reason=first_failure op='{SafeOpName(operation)}'");
                foreach (var cb in callbacks)
                {
                    try { cb(exception); }
                    catch (Exception cbEx)
                    {
                        Console.Error.WriteLine($"{Tag} stop-callback-error ex={cbEx.GetType().Name}");
                    }
                }
                Console.WriteLine($"{Tag} DIAGNOSTIC_STOPPED");
            }
        }

        /// <summary>
        /// Marca cancellation explicita como primeira falha observavel.
        /// Igual a RecordFirstFailure mas com semantica de cancellation.
        /// </summary>
        public static void RecordFirstCancellation(string operation, CancellationToken token)
        {
            bool wasStopping;
            List<Action<Exception>> callbacks;
            Exception cancelEx = new OperationCanceledException("first-failure-cancellation");
            lock (_gate)
            {
                if (!_active) return;
                wasStopping = _stopping;
                if (_firstFailure != null) return;
                _firstFailure = cancelEx;
                _firstFailureTicks = Stopwatch.GetTimestamp();
                _firstFailureOperation = operation;
                _stopping = true;
                callbacks = new List<Action<Exception>>(_stopCallbacks);
            }
            Console.WriteLine($"{Tag} FIRST_CANCELLATION op='{SafeOpName(operation)}' cancellationRequested={token.IsCancellationRequested}");
            if (!wasStopping)
            {
                Console.WriteLine($"{Tag} DIAGNOSTIC_STOPPING reason=first_cancellation op='{SafeOpName(operation)}'");
                foreach (var cb in callbacks)
                {
                    try { cb(cancelEx); }
                    catch (Exception cbEx)
                    {
                        Console.Error.WriteLine($"{Tag} stop-callback-error ex={cbEx.GetType().Name}");
                    }
                }
                Console.WriteLine($"{Tag} DIAGNOSTIC_STOPPED");
            }
        }

        /// <summary>
        /// Regista uma callback a ser invocada quando a primeira falha
        /// ocorre. Usada pelo codigo de producao para fazer cleanup
        /// controlado (fechar channel writer, cancelar CancellationToken,
        /// etc.). Idempotente em relacao a duplicados: a mesma callback
        /// registada varias vezes so' e' chamada uma vez.
        /// </summary>
        public static void RegisterStopCallback(Action<Exception> callback)
        {
            lock (_gate)
            {
                if (!_active) return;
                if (!_stopCallbacks.Contains(callback))
                {
                    _stopCallbacks.Add(callback);
                }
            }
        }

        /// <summary>
        /// Cancela o registo de uma stop callback. Idempotente.
        /// </summary>
        public static void UnregisterStopCallback(Action<Exception> callback)
        {
            lock (_gate)
            {
                _stopCallbacks.Remove(callback);
            }
        }

        /// <summary>
        /// Pede explicitamente que o diagnostico pare (modo fail-fast).
        /// Usado pelo watchdog ou por qualquer call site que detecte uma
        /// anomalia grave. Marca _stopping, invoca callbacks e emite
        /// DIAGNOSTIC_STOPPING/STOPPED.
        /// </summary>
        public static void RequestStop(string reason, string? operation = null)
        {
            bool wasStopping;
            List<Action<Exception>> callbacks;
            Exception stopEx = new InvalidOperationException("diagnostic-stop-requested: " + reason);
            lock (_gate)
            {
                if (!_active) return;
                wasStopping = _stopping;
                _stopping = true;
                if (!wasStopping && _firstFailure == null)
                {
                    _firstFailure = stopEx;
                    _firstFailureOperation = operation ?? "<stop-requested>";
                    _firstFailureTicks = Stopwatch.GetTimestamp();
                }
                callbacks = new List<Action<Exception>>(_stopCallbacks);
            }
            if (!wasStopping)
            {
                Console.WriteLine($"{Tag} DIAGNOSTIC_STOPPING reason='{Trunc(reason, 200)}' op='{SafeOpName(operation ?? "<stop-requested>")}'");
                foreach (var cb in callbacks)
                {
                    try { cb(stopEx); }
                    catch (Exception cbEx)
                    {
                        Console.Error.WriteLine($"{Tag} stop-callback-error ex={cbEx.GetType().Name}");
                    }
                }
                Console.WriteLine($"{Tag} DIAGNOSTIC_STOPPED");
            }
        }

        private static void Emit(string stage, string detail)
        {
            try
            {
                Console.WriteLine($"{Tag} {stage} runId={_runId} {detail}");
            }
            catch
            {
                // ignore
            }
        }

        private static void WatchdogTick()
        {
            long nowTicks;
            long lastTicks;
            string op;
            string detail;
            string cId;
            int mid;
            int aidx;
            int total;
            int events;
            lock (_gate)
            {
                if (!_active) return;
                nowTicks = Stopwatch.GetTimestamp();
                lastTicks = _lastEventTicks;
                op = _lastOperation;
                detail = _lastOperationDetail;
                cId = _lastStageCandidateId;
                mid = _lastStageMessageId;
                aidx = _lastStageAccountIndex;
                total = _lastStageTotalAccounts;
                events = _eventsEmitted;
            }
            var quietMs = (long)((nowTicks - lastTicks) * 1000.0 / Stopwatch.Frequency);
            if (quietMs < WATCHDOG_QUIET_MS) return;

            lock (_gate) { _watchdogTimeoutsEmitted++; }
            try
            {
                var totalMs = (long)((nowTicks - _runStartTicks) * 1000.0 / Stopwatch.Frequency);
                // DIAGNOSTIC_WATCHDOG_TIMEOUT e' distinto de FIRST_FAILURE.
                // O watchdog observa e regista, mas NAO substitui uma
                // failure real. O caller (que activou o diag) decide se
                // deve interromper o diagnostico quando o watchdog dispara.
                Console.WriteLine(
                    $"{Tag} DIAGNOSTIC_WATCHDOG_TIMEOUT quietMs={quietMs} totalElapsedMs={totalMs} events={events} " +
                    $"lastOp='{SafeOpName(op)}' lastDetail='{Trunc(detail, 240)}' " +
                    $"messageId={mid} candidateId='{cId}' accountIndex={aidx} totalAccounts={total} " +
                    $"threadId={Thread.CurrentThread.ManagedThreadId}");
            }
            catch
            {
                // ignore
            }
        }

        private static string SafeOpName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "<none>";
            return Trunc(s.Replace('\n', '_').Replace('\r', '_').Replace(' ', '_'), 64);
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }
    }

    /// <summary>
    /// Wrapper que cronometra uma fase. Use:
    ///   using (Diag110751Span.Start("BUILD_URL", messageId, candidateId, accountIndex)) { ... }
    /// </summary>
    public readonly struct Diag110751Span : IDisposable
    {
        private readonly string _op;
        private readonly int? _messageId;
        private readonly string? _candidateId;
        private readonly int? _accountIndex;
        private readonly int? _totalAccounts;
        private readonly long _ticks;
        private readonly Stopwatch _sw;

        private Diag110751Span(string op, int? mid, string? cid, int? aidx, int? total)
        {
            _op = op;
            _messageId = mid;
            _candidateId = cid;
            _accountIndex = aidx;
            _totalAccounts = total;
            _ticks = Stopwatch.GetTimestamp();
            _sw = Stopwatch.StartNew();
        }

        public static Diag110751Span Start(string op, int? messageId = null, string? candidateId = null, int? accountIndex = null, int? totalAccounts = null)
        {
            Diag110751.Report(op, "START", "", messageId, candidateId, accountIndex, totalAccounts);
            return new Diag110751Span(op, messageId, candidateId, accountIndex, totalAccounts);
        }

        public void Dispose()
        {
            _sw.Stop();
            Diag110751.Report(_op, "END", $"opElapsedMs={_sw.ElapsedMilliseconds}",
                _messageId, _candidateId, _accountIndex, _totalAccounts, exception: null,
                elapsedMs: _sw.ElapsedMilliseconds);
        }

        public void Error(Exception ex, string detail = "")
        {
            _sw.Stop();
            Diag110751.Report(_op, "ERROR", detail, _messageId, _candidateId, _accountIndex, _totalAccounts, ex, _sw.ElapsedMilliseconds);
        }

        public void Stage(string stage, string detail = "")
        {
            Diag110751.Report(_op, stage, detail, _messageId, _candidateId, _accountIndex, _totalAccounts);
        }
    }

    /// <summary>
    /// Helpers para instrumentar o download HTTP com informacao de DNS/connect
    /// sempre que possivel, sem alterar o caminho de producao. Sao op-in via
    /// Diag110751.IsActive.
    /// </summary>
    public static class Diag110751Http
    {
        public static void ReportDownloadStart(string url, int? messageId, string? candidateId)
        {
            var safe = SafeUrl(url);
            string host = "?", port = "?";
            try
            {
                var u = new Uri(url);
                host = u.Host;
                port = u.Port.ToString(CultureInfo.InvariantCulture);
            }
            catch { }
            Diag110751.Report("DOWNLOAD", "START", $"host={host} port={port} url='{safe}'", messageId, candidateId);
        }

        public static void ReportRequestCreated(string url, int? messageId, string? candidateId, string requestId)
        {
            Diag110751.Report("DOWNLOAD", "REQUEST_CREATED", $"requestId={requestId} host={TryHost(url)}", messageId, candidateId);
        }

        public static void ReportRequestSendStart(string url, int? messageId, string? candidateId, string requestId)
        {
            Diag110751.Report("DOWNLOAD", "SEND_START", $"requestId={requestId}", messageId, candidateId);
        }

        public static async Task<(string? resolvedIp, AddressFamily family, Exception? error)> TryResolveAsync(string host)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                if (addrs == null || addrs.Length == 0)
                {
                    return (null, AddressFamily.Unknown, new SocketException((int)SocketError.HostNotFound));
                }
                var first = addrs[0];
                return (first.ToString(), first.AddressFamily, null);
            }
            catch (Exception ex)
            {
                return (null, AddressFamily.Unknown, ex);
            }
        }

        public static void ReportDnsResolved(int? messageId, string? candidateId, string requestId, string host, string ip, AddressFamily family, long elapsedMs)
        {
            Diag110751.Report("DOWNLOAD", "DNS_RESOLVED", $"requestId={requestId} host={host} ip={ip} family={family} dnsElapsedMs={elapsedMs}", messageId, candidateId);
        }

        public static void ReportDnsFailed(int? messageId, string? candidateId, string requestId, string host, Exception ex, long elapsedMs)
        {
            Diag110751.Report("DOWNLOAD", "DNS_FAILED", $"requestId={requestId} host={host} dnsElapsedMs={elapsedMs} exType={ex.GetType().Name}", messageId, candidateId, exception: ex);
        }

        public static void ReportConnected(int? messageId, string? candidateId, string requestId, string localEp, string remoteEp, long elapsedMs)
        {
            Diag110751.Report("DOWNLOAD", "CONNECTED", $"requestId={requestId} localEp={localEp} remoteEp={remoteEp} connectElapsedMs={elapsedMs}", messageId, candidateId);
        }

        public static void ReportHeadersReceived(int? messageId, string? candidateId, string requestId, int status, string httpVersion, string contentType, string contentLength, long elapsedMs)
        {
            Diag110751.Report("DOWNLOAD", "HEADERS_RECEIVED", $"requestId={requestId} status={status} httpVersion={httpVersion} contentType={contentType} contentLength={contentLength} headersElapsedMs={elapsedMs}", messageId, candidateId);
        }

        public static void ReportBodyStart(int? messageId, string? candidateId, string requestId, long elapsedMs)
        {
            Diag110751.Report("DOWNLOAD", "BODY_READ_START", $"requestId={requestId} elapsedMs={elapsedMs}", messageId, candidateId);
        }

        public static void ReportBodyProgress(int? messageId, string? candidateId, string requestId, long bytesRead, long elapsedMs)
        {
            Diag110751.Report("DOWNLOAD", "BODY_READ_PROGRESS", $"requestId={requestId} bytesRead={bytesRead} elapsedMs={elapsedMs}", messageId, candidateId);
        }

        public static void ReportBodyEnd(int? messageId, string? candidateId, string requestId, long bytesRead, long elapsedMs)
        {
            Diag110751.Report("DOWNLOAD", "BODY_READ_END", $"requestId={requestId} bytesRead={bytesRead} bodyElapsedMs={elapsedMs}", messageId, candidateId);
        }

        public static void ReportDownloadEnd(int? messageId, string? candidateId, string requestId, long elapsedMs, bool ok)
        {
            Diag110751.Report("DOWNLOAD", "END", $"requestId={requestId} ok={ok} totalElapsedMs={elapsedMs}", messageId, candidateId);
        }

        public static void ReportDownloadError(int? messageId, string? candidateId, string requestId, Exception ex, long elapsedMs)
        {
            Diag110751.Report("DOWNLOAD", "ERROR", $"requestId={requestId} elapsedMs={elapsedMs}", messageId, candidateId, exception: ex);
        }

        public static string SafeUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return "<null>";
            try
            {
                return CredentialSanitizer.SanitizeUrl(url) ?? url;
            }
            catch
            {
                return "<unparseable>";
            }
        }

        public static string TryHost(string? url)
        {
            if (string.IsNullOrEmpty(url)) return "?";
            try { return new Uri(url).Host; } catch { return "?"; }
        }
    }
}

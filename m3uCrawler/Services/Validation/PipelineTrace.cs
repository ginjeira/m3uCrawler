using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace m3uCrawler.Services.Validation
{
    /// <summary>
    /// Níveis de severidade do evento de tracing.
    /// </summary>
    public enum TraceLevel
    {
        Debug = 0,
        Information = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>
    /// Categorias de eventos emitidos pela camada de observabilidade.
    /// Usadas para permitir filtros e agregação consistente em testes.
    /// </summary>
    public enum TraceCategory
    {
        RunStart,
        RunEnd,
        RunParameters,
        MessageAnalyzed,
        MessageMediaInfo,
        DetectStart,
        DetectEnd,
        CandidateCreated,
        CandidateRejected,
        AttachmentDownloadStart,
        AttachmentDownloadProgress,
        AttachmentDownloadComplete,
        AttachmentDownloadFailed,
        ChannelEnqueue,
        ChannelDequeue,
        WorkerStart,
        WorkerEnd,
        CandidateProcessStart,
        CandidateProcessEnd,
        ResolverStart,
        ResolverEnd,
        XtreamAccount,
        CandidatePromoted,
        FilterStart,
        FilterEnd,
        ParseStart,
        ParseEnd,
        HttpRequestStart,
        HttpRequestHeaders,
        HttpRequestBody,
        HttpRequestEnd,
        HttpRequestFailed,
        StreamValidationStart,
        StreamValidationEnd,
        StreamResult,
    }

    /// <summary>
    /// Correlation IDs partilhados por toda a cadeia. Cada ID pode estar
    /// vazio quando não é aplicável à fase (ex. `candidateId` antes da
    /// criação do candidate).
    /// </summary>
    public sealed class TraceContext
    {
        public string RunId { get; init; } = string.Empty;
        public long? TelegramMessageId { get; init; }
        public string? ChatTitle { get; init; }
        public string? CandidateId { get; init; }
        public string? ParentCandidateId { get; init; }
        public string? RequestId { get; init; }
        public string? AttachmentFilename { get; init; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("runId=").Append(RunId);
            if (TelegramMessageId.HasValue) sb.Append(" messageId=").Append(TelegramMessageId.Value);
            if (!string.IsNullOrEmpty(ChatTitle)) sb.Append(" chat='").Append(ChatTitle).Append('\'');
            if (!string.IsNullOrEmpty(CandidateId)) sb.Append(" candidateId=").Append(CandidateId);
            if (!string.IsNullOrEmpty(ParentCandidateId)) sb.Append(" parentCandidateId=").Append(ParentCandidateId);
            if (!string.IsNullOrEmpty(RequestId)) sb.Append(" requestId=").Append(RequestId);
            if (!string.IsNullOrEmpty(AttachmentFilename)) sb.Append(" filename='").Append(AttachmentFilename).Append('\'');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Evento imutável registado pela camada de tracing.
    /// </summary>
    public sealed class TraceEvent
    {
        public DateTime UtcTimestamp { get; init; } = DateTime.UtcNow;
        public long ElapsedFromRunStartMs { get; init; }
        public TraceLevel Level { get; init; }
        public TraceCategory Category { get; init; }
        public TraceContext Context { get; init; } = new();
        public string Message { get; init; } = string.Empty;
        public string? ExceptionType { get; init; }
        public string? ExceptionMessage { get; init; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(UtcTimestamp.ToString("O", CultureInfo.InvariantCulture))
              .Append(" (+").Append(ElapsedFromRunStartMs).Append("ms)")
              .Append(" [").Append(Level).Append("] [").Append(Category).Append("] ")
              .Append(Context).Append(' ').Append(Message);
            if (!string.IsNullOrEmpty(ExceptionType))
                sb.Append(" exception=").Append(ExceptionType);
            if (!string.IsNullOrEmpty(ExceptionMessage))
                sb.Append(" exceptionMessage='").Append(ExceptionMessage).Append('\'');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Sink passivo: implementado por <see cref="PipelineTrace"/> (que acumula
    /// eventos em memória) e por testes (que capturam eventos num List).
    /// </summary>
    public interface ITraceSink
    {
        void Emit(TraceEvent evt);
    }

    /// <summary>
    /// Sink no-op usado quando a chamada é feita sem trace. Garante que o
    /// código de produção continua a funcionar quando o trace não é
    /// inicializado (ex. testes legados).
    /// </summary>
    public sealed class NullTraceSink : ITraceSink
    {
        public static readonly NullTraceSink Instance = new();
        public void Emit(TraceEvent evt) { }
    }

    /// <summary>
    /// Extension methods que facilitam a chamada a partir de código
    /// de produção. Suportam null safety (sink opcional).
    /// </summary>
    public static class TraceSinkExtensions
    {
        public static void Debug(this ITraceSink? sink, TraceCategory cat, TraceContext ctx, string msg)
        {
            if (sink == null) return;
            sink.Emit(new TraceEvent { Level = TraceLevel.Debug, Category = cat, Context = ctx, Message = msg });
        }

        public static void Information(this ITraceSink? sink, TraceCategory cat, TraceContext ctx, string msg)
        {
            if (sink == null) return;
            sink.Emit(new TraceEvent { Level = TraceLevel.Information, Category = cat, Context = ctx, Message = msg });
        }

        public static void Warning(this ITraceSink? sink, TraceCategory cat, TraceContext ctx, string msg, Exception? ex = null)
        {
            if (sink == null) return;
            sink.Emit(new TraceEvent
            {
                Level = TraceLevel.Warning,
                Category = cat,
                Context = ctx,
                Message = msg,
                ExceptionType = ex?.GetType().Name,
                ExceptionMessage = ex?.Message,
            });
        }

        public static void Error(this ITraceSink? sink, TraceCategory cat, TraceContext ctx, string msg, Exception? ex)
        {
            if (sink == null) return;
            sink.Emit(new TraceEvent
            {
                Level = TraceLevel.Error,
                Category = cat,
                Context = ctx,
                Message = msg,
                ExceptionType = ex?.GetType().Name,
                ExceptionMessage = ex?.Message,
            });
        }
    }

    /// <summary>
    /// Implementação principal do tracing. Cria um runId único, regista
    /// eventos com timestamps relativos e fornece listas para verificação
    /// por testes.
    ///
    /// Não altera comportamento funcional: é puramente observabilidade.
    /// </summary>
    public sealed class PipelineTrace : ITraceSink
    {
        private readonly object _lock = new();
        private readonly List<TraceEvent> _events = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public string RunId { get; }
        public TraceLevel MinimumLevel { get; set; } = TraceLevel.Information;

        public PipelineTrace()
        {
            RunId = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        public PipelineTrace(string runId)
        {
            RunId = runId;
        }

        public void Emit(TraceEvent evt)
        {
            if (evt.Level < MinimumLevel) return;
            var enriched = new TraceEvent
            {
                UtcTimestamp = evt.UtcTimestamp,
                ElapsedFromRunStartMs = _sw.ElapsedMilliseconds,
                Level = evt.Level,
                Category = evt.Category,
                Context = new TraceContext
                {
                    RunId = RunId,
                    TelegramMessageId = evt.Context.TelegramMessageId,
                    ChatTitle = evt.Context.ChatTitle,
                    CandidateId = evt.Context.CandidateId,
                    ParentCandidateId = evt.Context.ParentCandidateId,
                    RequestId = evt.Context.RequestId,
                    AttachmentFilename = evt.Context.AttachmentFilename,
                },
                Message = evt.Message,
                ExceptionType = evt.ExceptionType,
                ExceptionMessage = evt.ExceptionMessage,
            };
            lock (_lock)
            {
                _events.Add(enriched);
            }
            Console.WriteLine(enriched);
        }

        public void Debug(TraceCategory cat, TraceContext ctx, string msg)
            => Emit(new TraceEvent { Level = TraceLevel.Debug, Category = cat, Context = ctx, Message = msg });

        public void Information(TraceCategory cat, TraceContext ctx, string msg)
            => Emit(new TraceEvent { Level = TraceLevel.Information, Category = cat, Context = ctx, Message = msg });

        public void Warning(TraceCategory cat, TraceContext ctx, string msg, Exception? ex = null)
            => Emit(new TraceEvent
            {
                Level = TraceLevel.Warning,
                Category = cat,
                Context = ctx,
                Message = msg,
                ExceptionType = ex?.GetType().Name,
                ExceptionMessage = ex?.Message,
            });

        public void Error(TraceCategory cat, TraceContext ctx, string msg, Exception? ex)
            => Emit(new TraceEvent
            {
                Level = TraceLevel.Error,
                Category = cat,
                Context = ctx,
                Message = msg,
                ExceptionType = ex?.GetType().Name,
                ExceptionMessage = ex?.Message,
            });

        /// <summary>
        /// Snapshot thread-safe de todos os eventos registados até ao momento.
        /// Usado por testes e por sumários.
        /// </summary>
        public IReadOnlyList<TraceEvent> Snapshot()
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }

        /// <summary>
        /// Devolve apenas eventos cujo <see cref="TraceCategory"/> coincide com
        /// algum dos fornecidos. Útil para testes e sumários.
        /// </summary>
        public IReadOnlyList<TraceEvent> SnapshotByCategory(params TraceCategory[] cats)
        {
            var set = new HashSet<TraceCategory>(cats);
            lock (_lock)
            {
                return _events.Where(e => set.Contains(e.Category)).ToArray();
            }
        }

        /// <summary>
        /// Conta eventos por categoria. Usado para reconciliar com RunReport.
        /// </summary>
        public IReadOnlyDictionary<TraceCategory, int> CountByCategory()
        {
            lock (_lock)
            {
                return _events.GroupBy(e => e.Category)
                              .ToDictionary(g => g.Key, g => g.Count());
            }
        }
    }

    /// <summary>
    /// Sink de captura para testes: recolhe eventos num List.
    /// </summary>
    public sealed class CapturingTraceSink : ITraceSink
    {
        private readonly object _lock = new();
        private readonly List<TraceEvent> _events = new();

        public IReadOnlyList<TraceEvent> Events
        {
            get { lock (_lock) { return _events.ToArray(); } }
        }

        public void Emit(TraceEvent evt)
        {
            lock (_lock)
            {
                _events.Add(evt);
            }
        }
    }
}

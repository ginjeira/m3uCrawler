using System.Collections.Generic;
using System.Linq;
using m3uCrawler.Models;
using m3uCrawler.Services.Validation;

namespace m3uCrawler.Services.Validation
{
    /// <summary>
    /// PHASE-OBSERVABILITY (2026-09-15): utilitario que recolhe
    /// contadores de uma <see cref="PipelineTrace"/> (via snapshot) e
    /// reconcilia-os com o <see cref="RunReport"/>. Permite a analise
    /// pos-run de "onde estao os eventos vs. onde estao os contadores
    /// agregados" sem alterar a logica funcional.
    /// </summary>
    public static class RunReportTraceReconciler
    {
        /// <summary>
        /// Soma os contadores por categoria e grava-os no RunReport.
        /// Idempotente: pode ser chamado multiplas vezes com snapshots
        /// crescentes.
        /// </summary>
        public static void RecordSnapshot(RunReport report, PipelineTrace trace)
        {
            if (report == null || trace == null) return;
            var counts = trace.CountByCategory();
            if (counts.TryGetValue(TraceCategory.HttpRequestStart, out var v1)) report.TraceEventsHttpRequestStart += v1;
            if (counts.TryGetValue(TraceCategory.HttpRequestEnd, out var v2)) report.TraceEventsHttpRequestEnd += v2;
            if (counts.TryGetValue(TraceCategory.HttpRequestFailed, out var v3)) report.TraceEventsHttpRequestFailed += v3;
            if (counts.TryGetValue(TraceCategory.ResolverStart, out var v4)) report.TraceEventsResolverStart += v4;
            if (counts.TryGetValue(TraceCategory.ResolverEnd, out var v5)) report.TraceEventsResolverEnd += v5;
            if (counts.TryGetValue(TraceCategory.CandidateCreated, out var v6)) report.TraceEventsCandidateCreated += v6;
            if (counts.TryGetValue(TraceCategory.CandidateRejected, out var v7)) report.TraceEventsCandidateRejected += v7;
            if (counts.TryGetValue(TraceCategory.ChannelEnqueue, out var v8)) report.TraceEventsChannelEnqueue += v8;
            if (counts.TryGetValue(TraceCategory.ChannelDequeue, out var v9)) report.TraceEventsChannelDequeue += v9;
            if (counts.TryGetValue(TraceCategory.WorkerStart, out var v10)) report.TraceEventsWorkerStart += v10;
            if (counts.TryGetValue(TraceCategory.WorkerEnd, out var v11)) report.TraceEventsWorkerEnd += v11;
            if (counts.TryGetValue(TraceCategory.AttachmentDownloadStart, out var v12)) report.TraceEventsAttachmentDownloadStart += v12;
            if (counts.TryGetValue(TraceCategory.AttachmentDownloadComplete, out var v13)) report.TraceEventsAttachmentDownloadComplete += v13;
            if (counts.TryGetValue(TraceCategory.AttachmentDownloadFailed, out var v14)) report.TraceEventsAttachmentDownloadFailed += v14;
            if (counts.TryGetValue(TraceCategory.XtreamAccount, out var v15)) report.TraceEventsXtreamAccount += v15;
            if (counts.TryGetValue(TraceCategory.CandidatePromoted, out var v16)) report.TraceEventsCandidatePromoted += v16;
        }
    }
}

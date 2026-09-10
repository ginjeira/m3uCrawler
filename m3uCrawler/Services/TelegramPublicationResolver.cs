using System.Text;
using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    /// <summary>
    /// Camada de resolution: dado uma lista de TelegramPublicationRef, invoca o
    /// TelegramMessageFetcher (que abstrai a chamada a WTelegram
    /// Channels_GetMessages / Messages_GetMessages), classifica o conteudo e
    /// aplica XtreamPublicationResolver quando aplicavel.
    ///
    /// Suporta resolucao recursiva com:
    ///   * dedup por (channelId, messageId) — set "seen"
    ///   * limite de profundidade (MaxResolutionDepth)
    ///   * tratamento de FLOOD_WAIT (delay + retry, configuravel)
    ///   * classificacao tri-state (Resolved / ResolutionFailed / Unsupported / RequiresReview)
    ///
    /// NAO toca no pipeline M3U/Xtream. Devolve as contas Xtream descobertas para
    /// que o caller (TelegramScraperService) faca o fan-out.
    /// </summary>
    internal static class TelegramPublicationResolver
    {
        /// <summary>
        /// Profundidade maxima de resolucao recursiva. Cobre casos como
        ///   A -> B -> C -> HTML attachment -> contas
        /// mas impede exploracao ilimitada de chains. Constante documentada.
        /// </summary>
        public const int MaxResolutionDepth = 3;

        /// <summary>
        /// Numero maximo de tentativas em caso de FLOOD_WAIT antes de desistir.
        /// </summary>
        public const int MaxFloodWaitRetries = 2;

        internal static async Task<List<TelegramPublicationResolution>> ResolveAsync(
            IReadOnlyList<TelegramPublicationRef> refs,
            TelegramMessageFetcher fetcher,
            int maxDepth = MaxResolutionDepth,
            int floodWaitRetryCount = MaxFloodWaitRetries,
            CancellationToken ct = default)
        {
            var results = new List<TelegramPublicationResolution>();
            if (refs.Count == 0) return results;

            var seen = new HashSet<(long, int)>();
            foreach (var r in refs)
            {
                if (r.ChannelId.HasValue && r.MessageId.HasValue)
                {
                    if (!seen.Add((r.ChannelId.Value, r.MessageId.Value))) continue;
                }

                await ResolveOneAsync(r, fetcher, maxDepth, floodWaitRetryCount,
                    seen, results, depth: 0, ct);
            }

            return results;
        }

        private static async Task ResolveOneAsync(
            TelegramPublicationRef reference,
            TelegramMessageFetcher fetcher,
            int maxDepth,
            int floodWaitRetryCount,
            HashSet<(long, int)> seen,
            List<TelegramPublicationResolution> results,
            int depth,
            CancellationToken ct)
        {
            // So faz sentido resolver referencias Telegram com (channel, message).
            // URLs HTTP publicas serao processadas pelo caller via DownloadPlaylistContentAsync.
            if (!reference.ChannelId.HasValue || !reference.MessageId.HasValue)
            {
                return;
            }

            var channelId = reference.ChannelId.Value;
            var messageId = reference.MessageId.Value;

            ResolvedPublication? resolved = null;
            string? failureReason = null;

            for (int attempt = 0; attempt <= floodWaitRetryCount; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    resolved = await fetcher(channelId, messageId, ct);
                    break;
                }
                catch (WTelegram.WTException ex) when (ex.Message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase))
                {
                    int wait = ExtractFloodWaitSeconds(ex.Message);
                    failureReason = $"FLOOD_WAIT (attempt {attempt + 1}/{floodWaitRetryCount + 1}, waited {wait}s)";
                    if (attempt == floodWaitRetryCount) break;
                    try { await Task.Delay(TimeSpan.FromSeconds(wait + 1), ct); }
                    catch (TaskCanceledException) { break; }
                }
                catch (WTelegram.WTException ex)
                {
                    failureReason = SafeExceptionReason(ex);
                    break;
                }
                catch (Exception ex)
                {
                    failureReason = "unexpected: " + SafeExceptionReason(ex);
                    break;
                }
            }

            if (resolved == null)
            {
                results.Add(new TelegramPublicationResolution
                {
                    ReferenceUrl = reference.ReferenceUrl,
                    ChannelId = channelId,
                    MessageId = messageId,
                    OriginalSource = reference.OriginalSource,
                    State = PublicationState.ResolutionFailed,
                    Reason = failureReason ?? "fetcher devolveu null",
                    Depth = depth
                });
                return;
            }

            var childRefs = new List<TelegramPublicationRef>();
            string? attachmentKind = null;
            IReadOnlyList<XtreamAccountInfo> accounts = Array.Empty<XtreamAccountInfo>();

            // 1. Texto da mensagem -> tentar extrair sub-publicacoes (recursivo).
            if (!string.IsNullOrEmpty(resolved.Text))
            {
                var textRefs = TelegramPublicationDiscovery.DiscoverFromText(
                    resolved.Text, reference.OriginalSource);
                foreach (var sub in textRefs)
                {
                    // Sub-publicacao Telegram (com channel/message id) precisa de
                    // dedup por (channel, message). Sub-publicacao HTTP e' apenas
                    // reportada para o caller; a deduplicacao de URL HTTP acontece
                    // no caller atraves do seen set de URLs.
                    if (sub.ChannelId.HasValue && sub.MessageId.HasValue)
                    {
                        if (!seen.Add((sub.ChannelId.Value, sub.MessageId.Value)))
                        {
                            continue;
                        }
                    }
                    childRefs.Add(sub);
                }
            }

            // 2. Attachment (filename + media content) -> classificar e processar.
            if (resolved.HasAttachment && !string.IsNullOrEmpty(resolved.Filename))
            {
                attachmentKind = ClassifyAttachment(resolved.Filename);
                if (attachmentKind == "html attachment")
                {
                    var html = Encoding.UTF8.GetString(resolved.MediaContent!);
                    accounts = XtreamPublicationResolver.ResolveFromHtml(
                        html, reference.ReferenceUrl);
                }
            }

            // 3. Decidir estado final.
            var state = ClassifyState(resolved, attachmentKind, accounts, childRefs);

            results.Add(new TelegramPublicationResolution
            {
                ReferenceUrl = reference.ReferenceUrl,
                ChannelId = channelId,
                MessageId = messageId,
                OriginalSource = reference.OriginalSource,
                State = state,
                Reason = state == PublicationState.Resolved ? null
                       : SanitizeReason(resolved, attachmentKind, accounts),
                Text = resolved.Text,
                Filename = resolved.Filename,
                AttachmentKind = attachmentKind,
                XtreamAccounts = accounts,
                ChildPublications = childRefs,
                Depth = depth
            });

            // 4. Recursao controlada: visitar sub-publicacoes Telegram ate' maxDepth.
            //    Sub-publicacoes HTTP sao apenas reportadas; o caller faz download e
            //    ingestion atraves do pipeline existente.
            if (depth + 1 <= maxDepth)
            {
                foreach (var child in childRefs.Where(c => c.ChannelId.HasValue && c.MessageId.HasValue))
                {
                    await ResolveOneAsync(
                        child, fetcher, maxDepth, floodWaitRetryCount,
                        seen, results, depth: depth + 1, ct);
                }
            }
        }

        private static string ClassifyAttachment(string filename)
        {
            var f = filename.ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(f, @"\.html?$")) return "html attachment";
            if (System.Text.RegularExpressions.Regex.IsMatch(f, @"\.m3u8?$")) return "m3u attachment";
            return "other attachment";
        }

        private static PublicationState ClassifyState(
            ResolvedPublication resolved,
            string? attachmentKind,
            IReadOnlyList<XtreamAccountInfo> accounts,
            IReadOnlyList<TelegramPublicationRef> childRefs)
        {
            // Resolved apenas se produz conteudo que sabemos processar:
            //   - contas Xtream extraidas do HTML
            //   - attachment M3U/M3U8
            //   - sub-publicacoes uteis no texto (telegram refs ou http urls)
            if (attachmentKind == "html attachment" && accounts.Count > 0) return PublicationState.Resolved;
            if (attachmentKind == "m3u attachment" || attachmentKind == "m3u8 attachment") return PublicationState.Resolved;
            if (childRefs.Count > 0) return PublicationState.Resolved;

            // HTML attachment sem cards Xtream: foi analisado mas inconclusivo.
            // Requer revisao explicita em vez de desaparecer.
            if (attachmentKind == "html attachment")
            {
                return PublicationState.RequiresReview;
            }

            // Attachment de tipo desconhecido (e.g. .pdf) -> revisao explicita.
            if (attachmentKind != null)
            {
                return PublicationState.RequiresReview;
            }

            // Mensagem sem nada util.
            return PublicationState.Unsupported;
        }

        private static string SanitizeReason(
            ResolvedPublication resolved,
            string? attachmentKind,
            IReadOnlyList<XtreamAccountInfo> accounts)
        {
            if (attachmentKind == "html attachment" && accounts.Count == 0)
            {
                return "html attachment sem cards Xtream";
            }
            if (!string.IsNullOrEmpty(resolved.Filename))
            {
                return $"attachment kind={attachmentKind ?? "unknown"} nao analisavel";
            }
            return "conteudo nao analisavel";
        }

        private static int ExtractFloodWaitSeconds(string message)
        {
            var digits = new string(message.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var seconds) && seconds > 0) return seconds;
            return message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase) ? 180 : 30;
        }

        private static string SafeExceptionReason(Exception ex)
        {
            // Sanitizar message para nao vazar paths locais / queries com credenciais.
            var msg = ex.Message ?? string.Empty;
            if (msg.Length > 200) msg = msg.Substring(0, 200) + "...";
            return CredentialSanitizer.SanitizeText(msg);
        }
    }
}

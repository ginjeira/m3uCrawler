using System.Text.RegularExpressions;
using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    /// <summary>
    /// Camada de discovery de publicacoes potencialmente relevantes.
    /// Identifica referencias Telegram (t.me/c/...) e URLs HTTP genericas
    /// numa mensagem. NAO faz I/O — apenas parsing deterministico.
    /// </summary>
    internal static class TelegramPublicationDiscovery
    {
        // https://t.me/c/<channel_id>/<message_id> (com query opcional ignorada)
        // channel_id >= 0 (canais publicos) ou >= 1000000000000 (supergrupos).
        // Aceita http/https. Query strings (e.g. ?comment=42) sao ignoradas.
        // O regex usa {1,15} para channel e {1,15} para message para permitir
        // tanto canais reais (13 digitos em supergrupos) como IDs de teste
        // com poucos digitos. A validacao semantica de tamanho (e.g. supergrupo
        // >= 1e12) acontece implicitamente no canal: WTelegram devolve
        // CHANNEL_INVALID para IDs fora do alcance.
        private static readonly Regex _tmeRefRegex = new(
            @"https?://t\.me/c/(?<channel>\d{1,15})/(?<message>\d{1,15})(?:\?\#\w+)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // URLs HTTP/HTTPS normais, com exclusao explicita do t.me (ja tratado acima).
        // Captura tudo ate whitespace ou delimitadores comuns.
        private static readonly Regex _httpUrlRegex = new(
            @"https?://(?!t\.me/)[^\s<>""'()]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Devolve a lista de publicacoes descobertas num texto. NAO deduplica;
        /// a deduplicacao por (channelId, messageId) ou URL acontece no caller
        /// (resolver) com um seen set adequado.
        /// </summary>
        internal static IReadOnlyList<TelegramPublicationRef> DiscoverFromText(
            string? text, string source)
        {
            var refs = new List<TelegramPublicationRef>();
            if (string.IsNullOrWhiteSpace(text)) return refs;

            var sanitizedSource = CredentialSanitizer.SanitizeText(text);

            foreach (Match m in _tmeRefRegex.Matches(text))
            {
                if (!long.TryParse(m.Groups["channel"].Value, out var channelId)) continue;
                if (!int.TryParse(m.Groups["message"].Value, out var msgId)) continue;

                refs.Add(new TelegramPublicationRef
                {
                    ReferenceUrl = m.Value,
                    Kind = "telegram message link",
                    ChannelId = channelId,
                    MessageId = msgId,
                    DiscoveredFromText = sanitizedSource,
                    OriginalSource = source ?? string.Empty,
                    State = PublicationState.Discovered
                });
            }

            foreach (Match m in _httpUrlRegex.Matches(text))
            {
                var url = m.Value.TrimEnd('.', ',', ')', ']', ';');
                if (url.Length == 0) continue;
                refs.Add(new TelegramPublicationRef
                {
                    ReferenceUrl = url,
                    Kind = "http publication url",
                    ChannelId = null,
                    MessageId = null,
                    DiscoveredFromText = sanitizedSource,
                    OriginalSource = source ?? string.Empty,
                    State = PublicationState.Discovered
                });
            }

            return refs;
        }
    }
}

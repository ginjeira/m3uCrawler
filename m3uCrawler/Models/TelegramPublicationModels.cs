namespace m3uCrawler.Models
{
    /// <summary>
    /// Estado de triagem de uma publicacao no pipeline de descoberta/resolucao.
    /// Permite distinguir "encontrei mas nao consegui analisar" de "encontrei
    /// e analisei" — em vez de a publicacao desaparecer silenciosamente.
    /// </summary>
    public enum PublicationState
    {
        /// <summary>
        /// Publicacao identificada pelo discovery; ainda nao foi resolvida.
        /// </summary>
        Discovered = 0,

        /// <summary>
        /// Conteudo obtido com sucesso (texto, media ou attachment HTML/M3U).
        /// </summary>
        Resolved = 1,

        /// <summary>
        /// Tentativa de resolucao falhou (FLOOD_WAIT persistente, canal
        /// inexistente, mensagem inacessivel, etc.). Reason preenchido.
        /// </summary>
        ResolutionFailed = 2,

        /// <summary>
        /// Conteudo analisado e nao continha nada util (sem URLs, sem
        /// attachments, sem referencias). Reason preenchido.
        /// </summary>
        Unsupported = 3,

        /// <summary>
        /// Conteudo analisado mas inconclusivo (e.g. HTML sem cards Xtream).
        /// Reason preenchido. Nao e' uma falha — e' um sinal para revisao.
        /// </summary>
        RequiresReview = 4
    }

    /// <summary>
    /// Representa uma referencia Telegram descoberta durante o scan. Pode ser
    /// resolvida pelo TelegramPublicationResolver para obter conteudo real.
    ///
    /// Modelo intermediate do pipeline (descovery -> resolution -> ingestion);
    /// nunca serializado, logado sem sanitizacao nem persistido.
    /// </summary>
    internal sealed class TelegramPublicationRef
    {
        /// <summary>
        /// URL original como encontrada no texto. Pode ser:
        ///   - "https://t.me/c/&lt;channel_id&gt;/&lt;message_id&gt;" (Telegram ref)
        ///   - "https://example.com/page.html"            (HTTP publication URL)
        /// </summary>
        public string ReferenceUrl { get; init; } = string.Empty;

        /// <summary>
        /// "telegram message link" ou "http publication url".
        /// </summary>
        public string Kind { get; init; } = string.Empty;

        /// <summary>
        /// Para t.me/c/: canal da mensagem referenciada (preenchido pelo parser).
        /// Para outros tipos: null.
        /// </summary>
        public long? ChannelId { get; init; }

        /// <summary>
        /// Para t.me/c/: id da mensagem referenciada (preenchido pelo parser).
        /// </summary>
        public int? MessageId { get; init; }

        /// <summary>
        /// Texto original (sanitizado contra credenciais) onde a referencia
        /// foi encontrada. Usado para diagnostico.
        /// </summary>
        public string DiscoveredFromText { get; init; } = string.Empty;

        /// <summary>
        /// Identificador do chat/canal onde a referencia foi descoberta.
        /// </summary>
        public string OriginalSource { get; init; } = string.Empty;

        public PublicationState State { get; init; } = PublicationState.Discovered;

        /// <summary>
        /// Razao textual (sanitizada) quando o estado nao e' Resolved.
        /// </summary>
        public string? Reason { get; init; }

        public override string ToString()
        {
            if (Kind == "telegram message link" && ChannelId.HasValue && MessageId.HasValue)
                return $"TelegramPub[ch={ChannelId} msg={MessageId}]";
            return $"TelegramPub[ref={ReferenceUrl}]";
        }
    }

    /// <summary>
    /// Conteudo obtido pelo TelegramPublicationResolver ao aplicar
    /// Channels_GetMessages / Messages_GetMessages sobre uma TelegramPublicationRef.
    ///
    /// Transporta o texto da mensagem + (opcionalmente) o conteudo binario do
    /// attachment. O resolver classifica-o (M3U / HTML / outro) e, no caso de
    /// HTML, aplica XtreamPublicationResolver para extrair contas Xtream.
    /// </summary>
    internal sealed class ResolvedPublication
    {
        public string? Text { get; init; }
        public string? Filename { get; init; }
        public byte[]? MediaContent { get; init; }

        /// <summary>
        /// "text", "html attachment", "m3u attachment", "m3u8 attachment",
        /// "other attachment" — classificacao feita pelo resolver com base
        /// no filename (quando ha attachment) e nao no conteudo (para evitar
        /// parsing HTML aqui).
        /// </summary>
        public string Kind { get; init; } = "text";

        public long? ChannelId { get; init; }
        public int? MessageId { get; init; }

        public bool HasAttachment => MediaContent != null && MediaContent.Length > 0;
    }

    /// <summary>
    /// Resultado da resolucao de uma publicacao: estado + (opcionalmente)
    /// contas Xtream extraidas + (opcionalmente) sub-publicacoes descobertas
    /// que o resolver visitou recursivamente ate ao limite de profundidade.
    /// </summary>
    internal sealed class TelegramPublicationResolution
    {
        public string ReferenceUrl { get; init; } = string.Empty;
        public long? ChannelId { get; init; }
        public int? MessageId { get; init; }
        public string? OriginalSource { get; init; }

        public PublicationState State { get; init; } = PublicationState.Discovered;
        public string? Reason { get; init; }

        /// <summary>
        /// Texto da mensagem resolvida (se Resolution for Resolved).
        /// </summary>
        public string? Text { get; init; }

        /// <summary>
        /// Filename do attachment (se for uma mensagem com media).
        /// </summary>
        public string? Filename { get; init; }

        /// <summary>
        /// Tipo de attachment detectado pelo resolver: "html attachment",
        /// "m3u attachment", "m3u8 attachment", "other attachment".
        /// </summary>
        public string? AttachmentKind { get; init; }

        /// <summary>
        /// Contas Xtream extraidas pela passagem pelo XtreamPublicationResolver
        /// (se Resolved + attachmentKind == "html attachment"). Vazio caso contrario.
        /// </summary>
        public IReadOnlyList<XtreamAccountInfo> XtreamAccounts { get; init; } = Array.Empty<XtreamAccountInfo>();

        /// <summary>
        /// Sub-publicacoes descobertas dentro do texto da mensagem resolvida
        /// (URLs HTTP e referencias t.me que serao processadas na proxima
        /// iteracao / pelo caller).
        /// </summary>
        public IReadOnlyList<TelegramPublicationRef> ChildPublications { get; init; } = Array.Empty<TelegramPublicationRef>();

        /// <summary>
        /// Profundidade na cadeia de resolucao (0 = entrada directa).
        /// </summary>
        public int Depth { get; init; }
    }

    /// <summary>
    /// Delegate que abstrai a chamada a WTelegram Channels_GetMessages /
    /// Messages_GetMessages. Injetado no TelegramPublicationResolver para
    /// permitir testes deterministicos sem sessao Telegram.
    /// </summary>
    internal delegate Task<ResolvedPublication?> TelegramMessageFetcher(
        long channelId, int messageId, CancellationToken ct);
}

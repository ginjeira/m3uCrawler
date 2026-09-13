using System.Text.Json.Serialization;

namespace m3uCrawler.Models;

/// <summary>
/// Regista uma falha de enumeracao de um dialogo Telegram durante o ciclo.
/// Quando Messages_GetHistory lanca uma excepcao nao-FLOOD_WAIT (e.g.
/// RpcError 500 RPC_CALL_FAIL), o dialogo e' marcado como incompleto
/// no RunReport em vez de matar o ciclo inteiro.
///
/// Invariantes:
/// - Cada entrada representa exactamente um dialogo que NAO foi
///   completamente enumerado.
/// - Se uma excepcao ocorre a meio da paginacao, MessagesProcessedInDialog
///   reflecte quantas mensagens foram efectivamente visitadas antes da falha.
/// - O ciclo continua para os dialogos seguintes; o relatorio final
///   continua a ser produzido mesmo que existam DialogErrors.
/// </summary>
public class DialogError
{
    /// <summary>
    /// Titulo legivel do chat/canal (username ou nome).
    /// </summary>
    public string ChatTitle { get; set; } = string.Empty;

    /// <summary>
    /// Identificador do peer (user_id, chat_id, ou channel_id). Nulo
    /// se nao foi possivel resolver antes da excepcao.
    /// </summary>
    public long? PeerId { get; set; }

    /// <summary>
    /// Tipo do peer: "User", "Chat", "Channel" ou "Unknown".
    /// </summary>
    public string PeerType { get; set; } = "Unknown";

    /// <summary>
    /// Nome simples do tipo da excepcao (ex. "RpcException", "WTException").
    /// </summary>
    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>
    /// Mensagem da excepcao, truncada para evitar credenciais grandes.
    /// </summary>
    public string ExceptionMessage { get; set; } = string.Empty;

    /// <summary>
    /// Pagina (offsetId) em que a excepcao ocorreu. -1 se excepcao
    /// antes da primeira chamada.
    /// </summary>
    public int FailedAtOffsetId { get; set; } = -1;

    /// <summary>
    /// Numero de mensagens efectivamente visitadas neste dialogo antes da
    /// excepcao.
    /// </summary>
    public int MessagesProcessedInDialog { get; set; }

    /// <summary>
    /// Instante UTC em que o erro foi registado.
    /// </summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

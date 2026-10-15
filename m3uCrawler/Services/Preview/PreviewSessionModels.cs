namespace m3uCrawler.Services.Preview;

/// <summary>Tipo de sessão de preview.</summary>
public enum PreviewSessionKind
{
    /// <summary>Remux HLS ao vivo (stream tocável pelo browser): manifest + segmentos.</summary>
    Hls,

    /// <summary>Snapshot: um único frame (thumbnail) para streams não suportados pelo browser.</summary>
    Snapshot,
}

/// <summary>
/// Resultado de <see cref="PreviewSessionManager.StartAsync"/>. É o contrato consumido pela
/// camada HTTP (onda seguinte). <see cref="Reason"/> é preenchido apenas em caminhos não-ideais
/// (ex.: <c>"busy"</c> quando o limite de sessões concorrentes foi atingido).
/// </summary>
/// <param name="SessionId">Identificador da sessão criada; vazio quando não foi criada.</param>
/// <param name="Kind">Tipo de sessão: <c>"hls"</c> ou <c>"snapshot"</c>.</param>
/// <param name="ManifestUrl">Path relativo do manifest HLS (só para <c>hls</c>).</param>
/// <param name="ThumbnailUrl">Path relativo do thumbnail JPEG, se aplicável.</param>
/// <param name="Probe">Resultado da sondagem, quando disponível.</param>
/// <param name="Playable"><c>true</c> se o stream é tocável pelo browser (remux HLS).</param>
/// <param name="Reason">Motivo quando a sessão não arranca em modo normal (ex.: <c>"busy"</c>).</param>
public sealed record PreviewSessionStartResult(
    string SessionId,
    string Kind,
    string? ManifestUrl,
    string? ThumbnailUrl,
    StreamProbe? Probe,
    bool Playable,
    string? Reason);

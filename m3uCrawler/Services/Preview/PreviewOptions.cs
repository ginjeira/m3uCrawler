using m3uCrawler.Services.Validation;

namespace m3uCrawler.Services.Preview;

/// <summary>
/// Configuração operacional do motor de preview de streams (Fase 1 — núcleo backend).
///
/// Todos os valores são imutáveis (<c>init</c>) e têm defaults seguros; a onda de
/// wiring pode injetar uma instância afinada. O motor é puro: não abre sockets nem
/// depende de <c>ffmpeg</c> para ser testado (ver <see cref="IProcessRunner"/>).
/// </summary>
public sealed class PreviewOptions
{
    /// <summary>Número máximo de sessões de preview simultâneas (default 2).</summary>
    public int MaxConcurrentSessions { get; init; } = 2;

    /// <summary>Inactividade após a qual a sessão é terminada (default 20 s).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Duração máxima absoluta de uma sessão, independente de actividade (default 5 min).</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Executável ffmpeg (remux HLS / snapshot). Default: <c>ffmpeg</c> (PATH).</summary>
    public string FfmpegPath { get; init; } = "ffmpeg";

    /// <summary>Executável ffprobe (sondagem de codecs). Default: <c>ffprobe</c> (PATH).</summary>
    public string FfprobePath { get; init; } = "ffprobe";

    /// <summary>Timeout da execução de ffprobe (default 8 s).</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// TTL da cache em memória de resultados de ffprobe, indexada por hash SHA-256
    /// do URL (o URL em claro nunca é usado como chave). Default: 10 min.
    /// </summary>
    public TimeSpan ProbeCacheTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// User-Agent usado nas invocações de ffmpeg/ffprobe. Reutiliza o mesmo valor
    /// do pipeline de validação (<see cref="StreamValidationOptions.DefaultUserAgent"/>).
    /// </summary>
    public string UserAgent { get; init; } = StreamValidationOptions.DefaultUserAgent;

    /// <summary>
    /// Quando <c>true</c>, streams cujo codec/container o browser não suporta são
    /// transcodificados (H.264/AAC) para HLS em vez de capturar um snapshot. É
    /// <b>opt-in</b> (default <c>false</c>) porque o transcode consome CPU
    /// significativamente mais do que o remux.
    /// </summary>
    public bool TranscodeNonPlayable { get; init; } = false;

    /// <summary>
    /// Tempo máximo de espera pelo artefacto de arranque da sessão: <c>index.m3u8</c>
    /// (sessões HLS) ou <c>thumb.jpg</c> (snapshot). O ffmpeg cria o manifest apenas
    /// 1–1,4 s depois de arrancar, pelo que devolver a URL antes disso produz 404 no
    /// <c>hls.js</c>. Default: 12 s — acima do timeout de TCP típico dos provedores
    /// (~5,35 s), para dar tempo a que o <c>ffmpeg</c> reporte o erro de rede em vez
    /// de ser classificado como um simples timeout de arranque.
    /// </summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Número de retentativas de <c>ffprobe</c> após a primeira tentativa falhar
    /// (total de tentativas = <c>ProbeRetries + 1</c>). Absorve falhas transitórias
    /// (ex.: 403 intermitente do upstream) sem degradar para snapshot. Default: 1.
    /// </summary>
    public int ProbeRetries { get; init; } = 1;
}

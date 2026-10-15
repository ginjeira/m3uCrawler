namespace m3uCrawler.Services.Preview;

/// <summary>
/// Resultado da sondagem (<c>ffprobe</c>) de um stream. Representa apenas os atributos
/// técnicos relevantes para decidir se o browser consegue tocar o stream sem transcodificação.
/// </summary>
public sealed record StreamProbe
{
    /// <summary>Codec de vídeo reportado pelo ffprobe (ex.: <c>h264</c>, <c>hevc</c>).</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Codec de áudio reportado pelo ffprobe (ex.: <c>aac</c>, <c>ac3</c>).</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Largura do primeiro stream de vídeo, se disponível.</summary>
    public int? Width { get; init; }

    /// <summary>Altura do primeiro stream de vídeo, se disponível.</summary>
    public int? Height { get; init; }

    /// <summary>Nome(s) de container do ffprobe (ex.: <c>mpegts</c>, <c>hls</c>).</summary>
    public string? FormatName { get; init; }

    /// <summary>Duração em segundos, quando o ffprobe a reporta (streams VOD).</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>
    /// Heurística: o browser consegue tocar o stream directamente (após remux para HLS)
    /// sem transcodificação? Containers HLS/MPEG-TS são irrelevantes aqui — o remux resolve-os.
    /// </summary>
    public bool PlayableByBrowser => IsBrowserPlayable(VideoCodec, AudioCodec);

    /// <summary>
    /// Heurística de codecs suportados nativamente: vídeo ∈ {h264, vp8, vp9, av1};
    /// áudio ∈ {aac, opus, vorbis, mp3}. Sem faixa de áudio é considerado tocável.
    /// </summary>
    public static bool IsBrowserPlayable(string? videoCodec, string? audioCodec)
    {
        var video = Normalize(videoCodec);
        if (video is null || !SupportedVideoCodecs.Contains(video))
        {
            return false;
        }

        var audio = Normalize(audioCodec);
        return audio is null || SupportedAudioCodecs.Contains(audio);
    }

    private static readonly HashSet<string> SupportedVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264", "vp8", "vp9", "av1",
    };

    private static readonly HashSet<string> SupportedAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "opus", "vorbis", "mp3",
    };

    private static string? Normalize(string? codec) =>
        string.IsNullOrWhiteSpace(codec) ? null : codec.Trim();
}

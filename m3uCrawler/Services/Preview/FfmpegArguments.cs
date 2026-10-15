using System.Globalization;

namespace m3uCrawler.Services.Preview;

/// <summary>
/// Construtores puros de argumentos de <c>ffmpeg</c>/<c>ffprobe</c>. Não executam nada —
/// apenas produzem a lista de argumentos, o que os torna testáveis sem a ferramenta instalada.
///
/// O URL do stream aparece sempre como valor de <c>-i</c>/posicional e NUNCA é registado
/// por esta classe (a sanitização de logs é responsabilidade de quem consome o resultado).
/// </summary>
public static class FfmpegArguments
{
    /// <summary>
    /// Remux de um stream (HLS nativo ou MPEG-TS) para HLS segmentado, sem reencode
    /// (<c>-c copy</c>). Produz <c>index.m3u8</c> + segmentos <c>seg%05d.ts</c> em <paramref name="outDir"/>.
    /// </summary>
    public static IReadOnlyList<string> HlsRemux(string inputUrl, string outDir, string userAgent) => new[]
    {
        "-hide_banner",
        "-loglevel", "error",
        "-user_agent", userAgent,
        "-i", inputUrl,
        "-c", "copy",
        "-f", "hls",
        "-hls_time", "2",
        "-hls_list_size", "6",
        "-hls_flags", "delete_segments+append_list+omit_endlist",
        "-hls_segment_filename", Path.Combine(outDir, "seg%05d.ts"),
        Path.Combine(outDir, "index.m3u8"),
    };

    /// <summary>
    /// Transcode de um stream para HLS segmentado em H.264/AAC (codecs suportados
    /// pelos browsers actuais). Usado como fallback opt-in quando o stream de origem
    /// não é reproduzível directamente (<c>-c:v libx264 -c:a aac</c>). Produz
    /// <c>index.m3u8</c> + segmentos <c>seg%05d.ts</c> em <paramref name="outDir"/>.
    /// </summary>
    public static IReadOnlyList<string> HlsTranscode(string inputUrl, string outDir, string userAgent) => new[]
    {
        "-hide_banner",
        "-loglevel", "error",
        "-user_agent", userAgent,
        "-i", inputUrl,
        "-c:v", "libx264",
        "-preset", "veryfast",
        "-tune", "zerolatency",
        "-c:a", "aac",
        "-b:a", "128k",
        "-f", "hls",
        "-hls_time", "2",
        "-hls_list_size", "6",
        "-hls_flags", "delete_segments+append_list+omit_endlist",
        "-hls_segment_filename", Path.Combine(outDir, "seg%05d.ts"),
        Path.Combine(outDir, "index.m3u8"),
    };

    /// <summary>
    /// Captura de um único frame (snapshot) a partir de <paramref name="at"/>,
    /// redimensionado para 480 px de largura. Produz <paramref name="outPath"/> (JPEG).
    /// </summary>
    public static IReadOnlyList<string> Thumbnail(string inputUrl, string outPath, string userAgent, TimeSpan at) => new[]
    {
        "-hide_banner",
        "-loglevel", "error",
        "-user_agent", userAgent,
        "-ss", FormatSeconds(at),
        "-i", inputUrl,
        "-frames:v", "1",
        "-vf", "scale=480:-1",
        "-y", outPath,
    };

    /// <summary>
    /// Sondagem de codecs/container via <c>ffprobe</c>, em JSON (<c>-show_streams -show_format</c>).
    /// </summary>
    public static IReadOnlyList<string> Probe(string inputUrl, string userAgent) => new[]
    {
        "-v", "error",
        "-user_agent", userAgent,
        "-print_format", "json",
        "-show_streams",
        "-show_format",
        inputUrl,
    };

    private static string FormatSeconds(TimeSpan at)
    {
        var seconds = at.TotalSeconds;
        if (seconds < 0)
        {
            seconds = 0;
        }
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

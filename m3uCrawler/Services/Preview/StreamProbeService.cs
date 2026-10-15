using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace m3uCrawler.Services.Preview;

/// <summary>
/// Executa <c>ffprobe</c> (via <see cref="IProcessRunner"/>) e traduz o JSON para
/// <see cref="StreamProbe"/>. Apenas os resultados <b>bem-sucedidos</b> ficam em cache em
/// memória, indexada pelo hash SHA-256 do URL — o URL em claro nunca é usado como chave de
/// cache nem registado. Sondagens falhadas (<c>null</c>) não são memorizadas para não
/// mascarar recuperações transitórias do upstream durante o TTL.
/// </summary>
public sealed class StreamProbeService
{
    private readonly IProcessRunner _runner;
    private readonly string _ffprobePath;
    private readonly string _userAgent;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cacheTtl;
    private readonly int _probeRetries;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public StreamProbeService(IProcessRunner runner, PreviewOptions options)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentNullException.ThrowIfNull(options);
        _ffprobePath = options.FfprobePath;
        _userAgent = options.UserAgent;
        _timeout = options.ProbeTimeout;
        _cacheTtl = options.ProbeCacheTtl;
        _probeRetries = Math.Max(0, options.ProbeRetries);
    }

    /// <summary>
    /// Sonda <paramref name="url"/> e devolve o <see cref="StreamProbe"/>, ou <c>null</c>
    /// se o ffprobe falhar / não devolver JSON válido em todas as tentativas. Tenta até
    /// <c>ProbeRetries + 1</c> vezes para absorver falhas transitórias (ex.: 403
    /// intermitente). Nunca lança por falha de processo; propaga apenas cancelamento.
    /// </summary>
    public async Task<StreamProbe?> ProbeAsync(string url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        var key = HashUrl(url);
        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        StreamProbe? probe = null;
        var attempts = _probeRetries + 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var args = FfmpegArguments.Probe(url, _userAgent);
                var result = await _runner.RunAsync(_ffprobePath, args, _timeout, ct).ConfigureAwait(false);
                if (!result.TimedOut && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
                {
                    probe = Parse(result.StdOut);
                    if (probe is not null)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                probe = null;
            }
        }

        // Não cachear resultados negativos: um `null` (ffprobe falhou) guardado durante todo
        // o TTL mascararia uma recuperação transitória do upstream (ex.: 403 intermitente).
        // Só sondagens bem-sucedidas são memorizadas.
        if (probe is not null)
        {
            Store(key, probe);
        }
        return probe;
    }

    /// <summary>Esvazia a cache em memória (diagnóstico / testes).</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Hash SHA-256 (hex minúsculas) do URL. Usado como chave de cache para que o URL em
    /// claro nunca seja materializado como chave.
    /// </summary>
    public static string HashUrl(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Parseia o JSON de <c>ffprobe -show_streams -show_format</c>. Devolve <c>null</c>
    /// se o JSON for inválido ou não contiver qualquer stream/formato reconhecível.
    /// </summary>
    public static StreamProbe? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string? videoCodec = null;
            string? audioCodec = null;
            int? width = null;
            int? height = null;

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = GetString(stream, "codec_type");
                    if (videoCodec is null && string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        videoCodec = GetString(stream, "codec_name");
                        width = GetInt(stream, "width");
                        height = GetInt(stream, "height");
                    }
                    else if (audioCodec is null && string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        audioCodec = GetString(stream, "codec_name");
                    }
                }
            }

            string? formatName = null;
            double? duration = null;
            if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
            {
                formatName = GetString(format, "format_name");
                var rawDuration = GetString(format, "duration");
                if (rawDuration is not null &&
                    double.TryParse(rawDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    duration = parsed;
                }
            }

            if (videoCodec is null && audioCodec is null && formatName is null)
            {
                return null;
            }

            return new StreamProbe
            {
                VideoCodec = videoCodec,
                AudioCodec = audioCodec,
                Width = width,
                Height = height,
                FormatName = formatName,
                DurationSeconds = duration,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    private static int? GetInt(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private bool TryGetCached(string key, out StreamProbe? probe)
    {
        probe = null;
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                probe = entry.Probe;
                return true;
            }
            _cache.TryRemove(key, out _);
        }
        return false;
    }

    private void Store(string key, StreamProbe probe)
    {
        _cache[key] = new CacheEntry
        {
            Probe = probe,
            ExpiresUtc = DateTimeOffset.UtcNow + _cacheTtl,
        };
    }

    private sealed class CacheEntry
    {
        public required StreamProbe Probe { get; init; }
        public DateTimeOffset ExpiresUtc { get; init; }
    }
}

using System.Net;
using System.Net.Http;
using System.Text;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// W2 — execução HTTP protegida: redirects manuais reclassificados pela
/// política SSRF e leitura de corpo com limites/validação de encoding.
///
/// Não duplica a lógica de classificação: usa
/// <see cref="AcquisitionFailureClassifier"/> (que delega em
/// <see cref="StreamFailureClassifier"/>).
/// </summary>
internal static class GuardedHttpRequest
{
    internal sealed record SendResult(
        HttpResponseMessage? Response,
        string FinalUrl,
        AcquisitionFailureKind FailureKind,
        string? Detail);

    internal sealed record BodyResult(
        string? Content,
        AcquisitionFailureKind FailureKind,
        string? Detail);

    /// <summary>
    /// Envia o request seguindo redirects MANUALMENTE. Cada salto é
    /// validado por <see cref="SsrfGuard"/>; schemes que não sejam
    /// http/https são rejeitados. O tecto de saltos é finito
    /// (<see cref="StreamValidationOptions.MaxRedirects"/>) para terminar.
    /// </summary>
    internal static async Task<SendResult> SendFollowingRedirectsAsync(
        HttpClient client,
        string startUrl,
        SsrfGuard guard,
        StreamValidationOptions options,
        Func<Uri, HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        Uri current;
        try
        {
            current = new Uri(startUrl, UriKind.Absolute);
        }
        catch
        {
            return new SendResult(null, startUrl, AcquisitionFailureKind.Network, "invalid-url");
        }

        for (var hop = 0; ; hop++)
        {
            try
            {
                await guard.ValidateAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch (SsrfBlockedException ex)
            {
                return new SendResult(null, current.ToString(), AcquisitionFailureKind.Security, ex.Reason);
            }

            HttpResponseMessage response;
            using (var request = requestFactory(current))
            {
                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }

            var status = (int)response.StatusCode;
            if (status >= 300 && status < 400)
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    // 3xx sem Location: devolvido como resposta normal (o
                    // caller classifica-o como falha 4xx/terminal).
                    return new SendResult(response, current.ToString(), AcquisitionFailureKind.None, null);
                }

                Uri target;
                try
                {
                    target = location.IsAbsoluteUri ? location : new Uri(current, location);
                }
                catch
                {
                    response.Dispose();
                    return new SendResult(null, current.ToString(), AcquisitionFailureKind.Redirect, "invalid-redirect-target");
                }

                // HTTPS->HTTP não pode contornar a política: o scheme do
                // alvo é validado (apenas http/https) e o alvo volta a ser
                // reclassificado no topo do ciclo.
                if (!IsHttpOrHttps(target.Scheme))
                {
                    response.Dispose();
                    return new SendResult(null, target.ToString(), AcquisitionFailureKind.Security, "redirect-scheme-not-allowed");
                }

                if (hop >= options.MaxRedirects)
                {
                    response.Dispose();
                    return new SendResult(null, target.ToString(), AcquisitionFailureKind.Redirect, "redirect-limit");
                }

                response.Dispose();
                current = target;
                continue;
            }

            return new SendResult(response, current.ToString(), AcquisitionFailureKind.None, null);
        }
    }

    /// <summary>
    /// Lê e valida o corpo: vazio é terminal (Empty); acima do limite é
    /// terminal (Oversized); bytes que não descodificam com o charset
    /// declarado/utf-8 são terminais (InvalidEncoding).
    /// </summary>
    internal static async Task<BodyResult> ReadBodyAsync(
        HttpResponseMessage response,
        StreamValidationOptions options,
        CancellationToken cancellationToken)
    {
        var max = options.MaxResponseBytes;
        var declared = response.Content.Headers.ContentLength;
        if (declared.HasValue && max > 0 && declared.Value > max)
        {
            return new BodyResult(null, AcquisitionFailureKind.Oversized, $"declared-bytes={declared.Value}");
        }

        byte[] bytes;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (max > 0 && total > max)
                {
                    return new BodyResult(null, AcquisitionFailureKind.Oversized, $"body-bytes>{max}");
                }
                buffer.Write(chunk, 0, read);
            }

            if (total == 0)
            {
                return new BodyResult(null, AcquisitionFailureKind.Empty, "empty-body");
            }

            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new BodyResult(null, AcquisitionFailureKind.Network, "body-read-failed");
        }

        string text;
        try
        {
            text = DecodeStrict(bytes, response.Content.Headers.ContentType?.CharSet);
        }
        catch
        {
            return new BodyResult(null, AcquisitionFailureKind.InvalidEncoding, null);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new BodyResult(null, AcquisitionFailureKind.Empty, "empty-body");
        }

        return new BodyResult(text, AcquisitionFailureKind.None, null);
    }

    internal static bool IsHttpOrHttps(string scheme)
        => string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
           || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string DecodeStrict(byte[] bytes, string? charset)
    {
        Encoding encoding;
        if (string.IsNullOrWhiteSpace(charset))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }
        else
        {
            var baseEncoding = Encoding.GetEncoding(charset);
            encoding = Encoding.GetEncoding(
                baseEncoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }

        return encoding.GetString(bytes);
    }
}

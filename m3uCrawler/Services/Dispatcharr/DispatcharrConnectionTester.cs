using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services.Dispatcharr;

namespace m3uCrawler.Services.Dispatcharr;

public enum DispatcharrConnectionStatus
{
    Connected,
    AuthenticationFailed,
    Unreachable,
    InvalidConfiguration,
    Error,
}

public sealed record DispatcharrConnectionTestResult(
    DispatcharrConnectionStatus Status,
    string? Version,
    int? HttpStatusCode,
    string? SanitizedDetail);

/// <summary>
/// Prova de conectividade read-only ao Dispatcharr. Faz um único
/// <c>GET /api/core/version/</c> através da mesma pipeline de autenticação
/// usada pelo sync (<see cref="DispatcharrClientFactory"/>), sem qualquer
/// chamada de escrita nem sincronização.
///
/// <para>
/// O detalhe devolvido é sempre sanitizado por
/// <see cref="CredentialSanitizer.SanitizeUrl"/> e nunca inclui a API key
/// nem credenciais (que viajam apenas em headers).
/// </para>
/// </summary>
public sealed class DispatcharrConnectionTester
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<HttpMessageHandler>? _transportFactory;

    /// <param name="transportFactory">
    /// Opcional, apenas para testes: substitui o transporte de rede mantendo
    /// o <see cref="DispatcharrAuthHandler"/> e a semântica de autenticação.
    /// </param>
    public DispatcharrConnectionTester(Func<HttpMessageHandler>? transportFactory = null)
    {
        _transportFactory = transportFactory;
    }

    public async Task<DispatcharrConnectionTestResult> TestAsync(
        DispatcharrConfig config, CancellationToken ct = default)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        if (!config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return new DispatcharrConnectionTestResult(
                DispatcharrConnectionStatus.InvalidConfiguration,
                Version: null,
                HttpStatusCode: null,
                SanitizedDetail: "dispatcharr base_url is not configured");
        }

        HttpClient http;
        try
        {
            http = BuildClient(config);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return new DispatcharrConnectionTestResult(
                DispatcharrConnectionStatus.InvalidConfiguration,
                Version: null,
                HttpStatusCode: null,
                SanitizedDetail: Sanitize(ex.Message, config));
        }
        catch (Exception ex)
        {
            return new DispatcharrConnectionTestResult(
                DispatcharrConnectionStatus.Error,
                Version: null,
                HttpStatusCode: null,
                SanitizedDetail: Sanitize(ex.Message, config));
        }

        try
        {
            using var resp = await http.GetAsync("/api/core/version/", ct);
            var status = (int)resp.StatusCode;

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                return new DispatcharrConnectionTestResult(
                    DispatcharrConnectionStatus.Connected,
                    Version: await TryReadVersionAsync(resp, ct),
                    HttpStatusCode: status,
                    SanitizedDetail: null);
            }

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new DispatcharrConnectionTestResult(
                    DispatcharrConnectionStatus.AuthenticationFailed,
                    Version: null,
                    HttpStatusCode: status,
                    SanitizedDetail: "authentication failed");
            }

            return new DispatcharrConnectionTestResult(
                DispatcharrConnectionStatus.Error,
                Version: null,
                HttpStatusCode: status,
                SanitizedDetail: $"unexpected HTTP {status}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DispatcharrConnectionTestResult(
                DispatcharrConnectionStatus.Unreachable,
                Version: null,
                HttpStatusCode: null,
                SanitizedDetail: "request timed out");
        }
        catch (HttpRequestException ex)
        {
            return new DispatcharrConnectionTestResult(
                DispatcharrConnectionStatus.Unreachable,
                Version: null,
                HttpStatusCode: null,
                SanitizedDetail: Sanitize(ex.Message, config));
        }
        catch (Exception ex)
        {
            return new DispatcharrConnectionTestResult(
                DispatcharrConnectionStatus.Error,
                Version: null,
                HttpStatusCode: null,
                SanitizedDetail: Sanitize(ex.Message, config));
        }
        finally
        {
            http.Dispose();
        }
    }

    private HttpClient BuildClient(DispatcharrConfig config)
    {
        if (_transportFactory is not null)
        {
            var (http, _, _, _, _, _) = DispatcharrClientFactory.BuildWithTransport(
                config.BaseUrl, config.ApiKey, config.Username, config.Password, _transportFactory());
            return http;
        }

        var (real, _, _, _, _, _) = DispatcharrClientFactory.Build(
            config.BaseUrl, config.ApiKey, config.Username, config.Password);
        return real;
    }

    private static async Task<string?> TryReadVersionAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var dto = await resp.Content.ReadFromJsonAsync<VersionDto>(JsonOptions, ct);
            return dto?.Version;
        }
        catch
        {
            return null;
        }
    }

    private static string Sanitize(string? detail, DispatcharrConfig config)
    {
        var s = CredentialSanitizer.SanitizeUrl(detail ?? string.Empty);
        if (!string.IsNullOrEmpty(config.ApiKey))
            s = s.Replace(config.ApiKey, "***", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(config.Password))
            s = s.Replace(config.Password, "***", StringComparison.Ordinal);
        return s;
    }
}

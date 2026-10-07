using System.Net;
using System.Net.Sockets;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// Resolve um hostname para os endereços efectivos. A abstracção existe para
/// permitir testes determinísticos de SSRF/DNS rebinding sem depender de DNS
/// público. A implementação de produção é <see cref="SystemDnsResolver"/>.
/// </summary>
public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Resolver de produção: <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.</summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    public static readonly SystemDnsResolver Instance = new();

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        => await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Excepção tipada de bloqueio SSRF. Fail-closed: a aquisição é recusada.
///
/// Nunca inclui credenciais, query strings, userinfo nem a URL completa na
/// mensagem: apenas o host e a razão de classificação
/// (<c>17-SECURITY.md §1</c>).
/// </summary>
public sealed class SsrfBlockedException : Exception
{
    public string Reason { get; }

    public SsrfBlockedException(string reason, string? host = null)
        : base(host is null
            ? $"SSRF policy blocked the request: {reason}"
            : $"SSRF policy blocked host '{host}': {reason}")
    {
        Reason = reason;
    }
}

/// <summary>Resultado de uma validação SSRF bem-sucedida.</summary>
public readonly record struct SsrfValidationResult(Uri Uri, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// Política SSRF central (<c>docs/Reestructure/17-SECURITY.md §2:29-37</c>).
///
/// Garantias:
/// <list type="bullet">
///   <item>Apenas <c>http</c>/<c>https</c>;</item>
///   <item>Host obrigatório;</item>
///   <item>IP literal é classificado directamente;</item>
///   <item>Hostname é resolvido (uma vez) e TODOS os endereços são validados:
///         se QUALQUER for proibido, falha (fail-closed);</item>
///   <item>Falha de resolução → fail-closed;</item>
///   <item>Nunca emite credenciais em mensagens de excepção.</item>
/// </list>
/// </summary>
public sealed class SsrfGuard
{
    private readonly IDnsResolver _resolver;
    private readonly bool _permitAnyAddress;

    public SsrfGuard(IDnsResolver? resolver = null)
        : this(resolver ?? SystemDnsResolver.Instance, permitAnyAddress: false)
    {
    }

    private SsrfGuard(IDnsResolver resolver, bool permitAnyAddress)
    {
        _resolver = resolver;
        _permitAnyAddress = permitAnyAddress;
    }

    /// <summary>Instância de produção, estrita e partilhada.</summary>
    public static SsrfGuard Default { get; } = new();

    public static SsrfGuard CreateDefault() => new();

    /// <summary>
    /// Seam exclusivo de testes locais: mantém a validação de scheme/host e a
    /// resolução, mas não restringe classes de endereço. Permite que testes
    /// determinísticos usem listeners em loopback sem enfraquecer os defaults
    /// de produção (que continuam estritos).
    /// </summary>
    internal static SsrfGuard CreatePermissiveForLocalTests(IDnsResolver? resolver = null)
        => new(resolver ?? SystemDnsResolver.Instance, permitAnyAddress: true);

    internal bool IsAddressPermitted(IPAddress address)
        => _permitAnyAddress || AddressClassifier.IsAllowed(address);

    internal IDnsResolver Resolver => _resolver;

    /// <summary>
    /// Valida uma URI absoluta e devolve os endereços efectivos autorizados.
    /// Lança <see cref="SsrfBlockedException"/> em qualquer incerteza.
    /// </summary>
    public async Task<SsrfValidationResult> ValidateAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            throw new SsrfBlockedException("uri-not-absolute");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new SsrfBlockedException($"scheme-not-allowed:{uri.Scheme}", SafeHost(uri));
        }

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new SsrfBlockedException("host-missing");
        }

        // IP literal: classificar directamente (sem DNS).
        if (IPAddress.TryParse(host, out var literal))
        {
            if (!IsAddressPermitted(literal))
            {
                throw new SsrfBlockedException(
                    $"address-class:{AddressClassifier.Classify(literal)}", host);
            }
            return new SsrfValidationResult(uri, new[] { literal });
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fail-closed e sem propagar detalhes internos do resolver.
            throw new SsrfBlockedException("dns-resolution-failed", host);
        }

        if (addresses is null || addresses.Length == 0)
        {
            throw new SsrfBlockedException("dns-no-addresses", host);
        }

        // TODOS os endereços têm de ser permitidos; qualquer um proibido falha.
        foreach (var address in addresses)
        {
            if (!IsAddressPermitted(address))
            {
                throw new SsrfBlockedException(
                    $"address-class:{AddressClassifier.Classify(address)}", host);
            }
        }

        return new SsrfValidationResult(uri, addresses);
    }

    /// <summary>
    /// Verificação síncrona para hosts que são IP literais, reutilizada por
    /// consumidores que não fazem I/O (ex.: parsing de publicações Xtream).
    /// Hostname textual devolve <c>false</c> (a decisão exige resolução).
    /// </summary>
    public static bool IsAllowedLiteralHost(string? host)
        => !string.IsNullOrWhiteSpace(host)
           && IPAddress.TryParse(host, out var ip)
           && AddressClassifier.IsAllowed(ip);

    private static string SafeHost(Uri uri)
    {
        try { return uri.Host; } catch { return "?"; }
    }
}

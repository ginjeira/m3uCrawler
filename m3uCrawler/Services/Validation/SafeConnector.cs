using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// <see cref="SocketsHttpHandler.ConnectCallback"/> endurecido contra SSRF e
/// DNS rebinding (<c>docs/Reestructure/17-SECURITY.md §2:29-37</c>).
///
/// Fluxo por ligação:
/// <list type="number">
///   <item>Recebe o <see cref="SocketsHttpConnectionContext.DnsEndPoint"/>
///         (hostname textual do request, sem substituição);</item>
///   <item>Resolve e valida com <see cref="SsrfGuard"/> (mesma política);
///         se qualquer endereço for proibido ou a resolução falhar → falha
///         fail-closed com <see cref="SsrfBlockedException"/>;</item>
///   <item>Liga um <see cref="Socket"/> a um dos endereços JÁ validados,
///         sem uma segunda resolução;</item>
///   <item>Devolve <see cref="NetworkStream"/> com <c>ownsSocket: true</c>.</item>
/// </list>
///
/// O hostname original permanece no request, preservando TLS/SNI/Host.
///
/// O <see cref="IDnsResolver"/> e o delegate de ligação são injectáveis
/// (só para testes), permitindo simular DNS rebinding de forma determinística
/// sem enfraquecer os defaults de produção.
/// </summary>
internal static class SafeConnector
{
    /// <summary>Delegate de ligação, injectável apenas em testes.</summary>
    internal delegate ValueTask<Socket> SocketConnectAsync(
        IPAddress address, int port, CancellationToken cancellationToken);

    /// <summary>Ligação real, usada por omissão em produção.</summary>
    internal static async ValueTask<Socket> DefaultConnectAsync(
        IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Callback de produção: guard estrito com resolver do sistema.</summary>
    internal static ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => ConnectAsync(context, cancellationToken, SsrfGuard.Default, DefaultConnectAsync);

    /// <summary>Callback com guard/connect injectáveis (testes).</summary>
    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken,
        SsrfGuard guard,
        SocketConnectAsync connect)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.DnsEndPoint;
        return await ConnectResolvedAsync(
            endpoint.Host, endpoint.Port, guard, connect, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Núcleo testável: resolve+valida e liga ao endereço validado. Não há
    /// segunda resolução entre validação e ligação.
    /// </summary>
    internal static async ValueTask<Stream> ConnectResolvedAsync(
        string host,
        int port,
        SsrfGuard guard,
        SocketConnectAsync connect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(connect);

        var probeUri = BuildProbeUri(host, port);
        var validation = await guard.ValidateAsync(probeUri, cancellationToken).ConfigureAwait(false);

        Exception? last = null;
        foreach (var address in validation.Addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var socket = await connect(address, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        // Todos os endereços validados falharam na ligação, ou a lista estava
        // vazia. Reenviar a última falha (classificada como Network) para que
        // o retry técnico a possa tratar; nunca mascarar como sucesso.
        if (last is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(last).Throw();
        }
        throw new SsrfBlockedException("no-validated-address");
    }

    private static Uri BuildProbeUri(string host, int port)
    {
        var h = host ?? string.Empty;
        // IPv6 literal necessita de brackets num URI. DnsEndPoint.Host vem
        // tipicamente sem brackets, mas aceitamos as duas formas.
        if (IPAddress.TryParse(h.Trim('[', ']'), out var ip)
            && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            h = "[" + h.Trim('[', ']') + "]";
        }
        return new Uri($"http://{h}:{port}/");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W2 (2026-09-19) — provas determinísticas da política SSRF
/// (<c>docs/Reestructure/17-SECURITY.md §2:29-37</c>): classificação de
/// endereços, resolução/validação, anti-DNS-rebinding no conector e
/// controlo manual de redirects. Não depende de Internet/DNS público.
/// </summary>
public class WaveW2SsrfGuardTests
{
    // ════════════════════════════════════════════════════════════════
    // A. Classificação de endereços
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("127.0.0.1", IpAddressClass.Loopback)]
    [InlineData("127.255.255.254", IpAddressClass.Loopback)]
    [InlineData("10.1.2.3", IpAddressClass.Private)]
    [InlineData("172.16.0.1", IpAddressClass.Private)]
    [InlineData("172.31.255.255", IpAddressClass.Private)]
    [InlineData("192.168.1.1", IpAddressClass.Private)]
    [InlineData("169.254.1.1", IpAddressClass.LinkLocal)]
    [InlineData("169.254.169.254", IpAddressClass.LinkLocal)]
    [InlineData("0.0.0.0", IpAddressClass.Other)]
    [InlineData("0.1.2.3", IpAddressClass.Other)]
    [InlineData("255.255.255.255", IpAddressClass.Other)]
    [InlineData("8.8.8.8", IpAddressClass.Allowed)]
    [InlineData("172.32.0.1", IpAddressClass.Allowed)]
    [InlineData("93.184.216.34", IpAddressClass.Allowed)]
    [InlineData("::1", IpAddressClass.Loopback)]
    [InlineData("fe80::1", IpAddressClass.LinkLocal)]
    [InlineData("fc00::1", IpAddressClass.Private)]
    [InlineData("fd12:3456::1", IpAddressClass.Private)]
    [InlineData("::", IpAddressClass.Other)]
    [InlineData("2001:4860:4860::8888", IpAddressClass.Allowed)]
    [InlineData("::ffff:127.0.0.1", IpAddressClass.Loopback)]
    [InlineData("::ffff:10.0.0.1", IpAddressClass.Private)]
    [InlineData("::ffff:169.254.169.254", IpAddressClass.LinkLocal)]
    [InlineData("::ffff:8.8.8.8", IpAddressClass.Allowed)]
    public void AddressClassifier_classifies_core_classes(string address, IpAddressClass expected)
    {
        var ip = IPAddress.Parse(address);
        Assert.Equal(expected, AddressClassifier.Classify(ip));
        Assert.Equal(expected == IpAddressClass.Allowed, AddressClassifier.IsAllowed(ip));
    }

    // ════════════════════════════════════════════════════════════════
    // B. SsrfGuard: scheme, IP literal, resolução e fail-closed
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Guard_rejects_non_http_schemes()
    {
        var guard = new SsrfGuard(Resolver(("public.test", IPAddress.Parse("93.184.216.34"))));
        await Assert.ThrowsAsync<SsrfBlockedException>(
            () => guard.ValidateAsync(new Uri("ftp://public.test/file"), CancellationToken.None));
    }

    [Fact]
    public async Task Guard_rejects_prohibited_ip_literal_without_dns()
    {
        var resolver = new CountingResolver();
        var guard = new SsrfGuard(resolver);
        await Assert.ThrowsAsync<SsrfBlockedException>(
            () => guard.ValidateAsync(new Uri("http://127.0.0.1/"), CancellationToken.None));
        Assert.Equal(0, resolver.Calls); // IP literal não passa por DNS
    }

    [Fact]
    public async Task Guard_allows_public_ip_literal_without_dns()
    {
        var resolver = new CountingResolver();
        var guard = new SsrfGuard(resolver);
        var result = await guard.ValidateAsync(new Uri("http://93.184.216.34/"), CancellationToken.None);
        Assert.Single(result.Addresses);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Guard_blocks_hostname_resolving_to_prohibited_address()
    {
        var guard = new SsrfGuard(Resolver(("hostile.test", IPAddress.Parse("10.0.0.7"))));
        await Assert.ThrowsAsync<SsrfBlockedException>(
            () => guard.ValidateAsync(new Uri("http://hostile.test/"), CancellationToken.None));
    }

    [Fact]
    public async Task Guard_blocks_when_any_resolved_address_is_prohibited()
    {
        var guard = new SsrfGuard(Resolver(
            ("mixed.test", IPAddress.Parse("93.184.216.34")),
            ("mixed.test", IPAddress.Parse("127.0.0.1"))));
        await Assert.ThrowsAsync<SsrfBlockedException>(
            () => guard.ValidateAsync(new Uri("http://mixed.test/"), CancellationToken.None));
    }

    [Fact]
    public async Task Guard_fails_closed_when_resolution_fails()
    {
        var guard = new SsrfGuard(new ThrowingResolver());
        await Assert.ThrowsAsync<SsrfBlockedException>(
            () => guard.ValidateAsync(new Uri("http://unresolvable.test/"), CancellationToken.None));
    }

    [Fact]
    public async Task Guard_returns_all_addresses_when_all_allowed()
    {
        var guard = new SsrfGuard(Resolver(
            ("ok.test", IPAddress.Parse("93.184.216.34")),
            ("ok.test", IPAddress.Parse("8.8.8.8"))));
        var result = await guard.ValidateAsync(new Uri("http://ok.test/path?x=1"), CancellationToken.None);
        Assert.Equal(2, result.Addresses.Count);
    }

    [Fact]
    public async Task Guard_exception_message_does_not_leak_url_or_credentials()
    {
        var guard = new SsrfGuard(Resolver(("hostile.test", IPAddress.Parse("127.0.0.1"))));
        var ex = await Assert.ThrowsAsync<SsrfBlockedException>(
            () => guard.ValidateAsync(
                new Uri("http://user:topsecret@hostile.test/get.php?password=topsecret"),
                CancellationToken.None));
        Assert.DoesNotContain("topsecret", ex.Message);
        Assert.DoesNotContain("get.php", ex.Message);
    }

    // ════════════════════════════════════════════════════════════════
    // C. SafeConnector: resolve→validate→connect sem 2ª resolução
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SafeConnector_connects_to_validated_address_and_resolves_once()
    {
        var resolver = new SequenceResolver(
            new[] { IPAddress.Parse("93.184.216.34") },
            new[] { IPAddress.Parse("127.0.0.1") }); // se houvesse 2ª resolução, mudava
        var guard = new SsrfGuard(resolver);

        // Listener local apenas para produzir um Socket ligado (o delegate de
        // ligação é injectado e ignora o endereço de destino do teste).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            IPAddress? connected = null;
            var connectCalls = 0;
            SafeConnector.SocketConnectAsync connect = (address, port, ct) =>
            {
                connectCalls++;
                connected = address;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(IPAddress.Loopback, listenerPort);
                return ValueTask.FromResult(socket);
            };

            using var stream = (NetworkStream)await SafeConnector.ConnectResolvedAsync(
                "rebind.test", 80, guard, connect, CancellationToken.None);

            Assert.Equal(1, resolver.Calls);            // uma única resolução
            Assert.Equal(1, connectCalls);
            Assert.Equal(IPAddress.Parse("93.184.216.34"), connected); // endereço validado, não o 2º
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SafeConnector_never_connects_when_any_address_prohibited()
    {
        var resolver = new SequenceResolver(new[]
        {
            IPAddress.Parse("93.184.216.34"),
            IPAddress.Parse("169.254.169.254"),
        });
        var guard = new SsrfGuard(resolver);

        var connectCalls = 0;
        SafeConnector.SocketConnectAsync connect = (address, port, ct) =>
        {
            connectCalls++;
            return ValueTask.FromResult(new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp));
        };

        await Assert.ThrowsAsync<SsrfBlockedException>(
            async () => await SafeConnector.ConnectResolvedAsync(
                "mixed.test", 80, guard, connect, CancellationToken.None));
        Assert.Equal(0, connectCalls);
    }

    // ════════════════════════════════════════════════════════════════
    // D. Redirects manuais reclassificados
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.1.10/")]
    [InlineData("http://172.16.0.9/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    public async Task Redirect_to_prohibited_target_is_blocked(string target)
    {
        var handler = new StubHandler(_ => Redirect(target));
        var client = new HttpClient(handler);
        var options = new StreamValidationOptions();
        var guard = new SsrfGuard(Resolver(("public.test", IPAddress.Parse("93.184.216.34"))));

        var result = await GuardedHttpRequest.SendFollowingRedirectsAsync(
            client, "http://public.test/start", guard, options,
            uri => new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);

        Assert.Equal(AcquisitionFailureKind.Security, result.FailureKind);
        Assert.Null(result.Response);
        Assert.Equal(1, handler.Calls); // não reemite para o alvo proibido
    }

    [Fact]
    public async Task Redirect_chain_of_public_targets_succeeds()
    {
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path switch
            {
                "/start" => Redirect("http://a.public.test/next"),
                "/next" => Redirect("http://b.public.test/final"),
                _ => Ok("#EXTM3U\n"),
            };
        });
        var client = new HttpClient(handler);
        var guard = new SsrfGuard(Resolver(
            ("public.test", IPAddress.Parse("93.184.216.34")),
            ("a.public.test", IPAddress.Parse("93.184.216.34")),
            ("b.public.test", IPAddress.Parse("93.184.216.34"))));

        var result = await GuardedHttpRequest.SendFollowingRedirectsAsync(
            client, "http://public.test/start", guard, new StreamValidationOptions(),
            uri => new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);

        Assert.Equal(AcquisitionFailureKind.None, result.FailureKind);
        Assert.NotNull(result.Response);
        Assert.Equal(3, handler.Calls);
        Assert.Contains("b.public.test", result.FinalUrl);
        result.Response!.Dispose();
    }

    [Fact]
    public async Task Https_to_http_downgrade_cannot_bypass_policy()
    {
        var handler = new StubHandler(_ => Redirect("http://169.254.169.254/latest/meta-data"));
        var client = new HttpClient(handler);
        var guard = new SsrfGuard(Resolver(("secure.public.test", IPAddress.Parse("93.184.216.34"))));

        var result = await GuardedHttpRequest.SendFollowingRedirectsAsync(
            client, "https://secure.public.test/start", guard, new StreamValidationOptions(),
            uri => new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);

        Assert.Equal(AcquisitionFailureKind.Security, result.FailureKind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Redirect_limit_terminates_with_finite_hops()
    {
        var handler = new StubHandler(_ => Redirect("http://public.test/next"));
        var client = new HttpClient(handler);
        var options = new StreamValidationOptions { MaxRedirects = 2 };
        var guard = new SsrfGuard(Resolver(("public.test", IPAddress.Parse("93.184.216.34"))));

        var result = await GuardedHttpRequest.SendFollowingRedirectsAsync(
            client, "http://public.test/start", guard, options,
            uri => new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);

        Assert.Equal(AcquisitionFailureKind.Redirect, result.FailureKind);
        Assert.Equal("redirect-limit", result.Detail);
        Assert.Equal(3, handler.Calls); // inicial + 2 saltos permitidos
    }

    // ════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════

    private static FakeResolver Resolver(params (string Host, IPAddress Address)[] entries)
        => new(entries);

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage((HttpStatusCode)302);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Ok(string body, string contentType = "application/x-mpegurl")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private sealed class FakeResolver : IDnsResolver
    {
        private readonly Dictionary<string, IPAddress[]> _map;
        public int Calls;

        public FakeResolver(params (string Host, IPAddress Address)[] entries)
        {
            _map = entries
                .GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Address).ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (_map.TryGetValue(host, out var addresses))
            {
                return Task.FromResult(addresses);
            }
            return Task.FromException<IPAddress[]>(
                new SocketException((int)SocketError.HostNotFound));
        }
    }

    private sealed class CountingResolver : IDnsResolver
    {
        public int Calls;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
        }
    }

    private sealed class ThrowingResolver : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
            => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.TryAgain));
    }

    private sealed class SequenceResolver : IDnsResolver
    {
        private readonly IPAddress[][] _responses;
        public int Calls;

        public SequenceResolver(params IPAddress[][] responses) => _responses = responses;

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref Calls) - 1;
            var clamped = Math.Min(index, _responses.Length - 1);
            return Task.FromResult(_responses[clamped]);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int Calls;
        public List<Uri> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            lock (Requests) Requests.Add(request.RequestUri!);
            var response = _responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}

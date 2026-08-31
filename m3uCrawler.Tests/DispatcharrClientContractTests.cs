using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using m3uCrawler.Services.Dispatcharr;
using Xunit;

namespace m3uCrawler.Tests;

public class DispatcharrClientContractTests
{
    [Fact]
    public async Task List_channels_returns_domain_objects()
    {
        var handler = new JsonHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/api/channels/channels/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        count = 1,
                        results = new[]
                        {
                            new
                            {
                                id = 42L,
                                name = "CNN",
                                channel_number = 101.0,
                                tvg_id = "cnn.us",
                                streams = new long[] { 7, 8 }
                            }
                        }
                    }), Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var c = new DispatcharrChannelClient(client);
        var list = await c.ListAsync(CancellationToken.None);
        Assert.Single(list);
        Assert.Equal(42, list[0].Id);
        Assert.Equal("CNN", list[0].Name);
        Assert.Equal(new long[] { 7, 8 }, list[0].StreamIds);
    }

    [Fact]
    public async Task Create_stream_sends_payload_and_returns_id()
    {
        long? captured = null;
        var handler = new JsonHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/channels/streams/"))
            {
                captured = 99;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { id = 99L, name = "x", url = "http://u/x", is_custom = true }),
                        Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var s = new DispatcharrStreamClient(client);
        var id = await s.CreateAsync(new NewStreamRequest { Name = "x", Url = "http://u/x" }, CancellationToken.None);
        Assert.Equal(99, id);
        Assert.Equal(99, captured);
    }

    [Fact]
    public async Task Update_streams_sends_full_ordered_list()
    {
        long[]? captured = null;
        var handler = new JsonHandler(req =>
        {
            if (req.Method == HttpMethod.Patch && req.RequestUri!.AbsolutePath.Contains("/api/channels/channels/1/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                var doc = JsonSerializer.Deserialize<JsonElement>(body);
                captured = doc.GetProperty("streams").EnumerateArray().Select(e => e.GetInt64()).ToArray();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var c = new DispatcharrChannelClient(client);
        await c.UpdateStreamsAsync(1, new long[] { 5, 3, 9 }, CancellationToken.None);
        Assert.Equal(new long[] { 5, 3, 9 }, captured);
    }

    [Fact]
    public async Task Error_response_throws_DispatcharrException_with_sanitized_message()
    {
        var handler = new JsonHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("user=alice&password=secret&token=tk", Encoding.UTF8, "text/plain"),
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var s = new DispatcharrStreamClient(client);
        var ex = await Assert.ThrowsAsync<DispatcharrException>(() =>
            s.CreateAsync(new NewStreamRequest { Name = "x", Url = "http://u/x" }, CancellationToken.None));
        Assert.Equal(400, ex.StatusCode);
        Assert.DoesNotContain("secret", ex.SanitizedMessage);
    }

    [Fact]
    public async Task Version_endpoint_returns_string_when_present()
    {
        var handler = new JsonHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/api/core/version/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"version\":\"0.30.0\"}", Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var m = new DispatcharrM3UClient(client);
        Assert.Equal("0.30.0", await m.GetVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task List_groups_accepts_paginated_envelope()
    {
        var handler = new JsonHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/api/channels/groups/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        count = 2,
                        @next = (string?)null,
                        @previous = (string?)null,
                        results = new[]
                        {
                            new { id = 11L, name = "Portugal" },
                            new { id = 12L, name = "Cinema" },
                        }
                    }), Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var m = new DispatcharrM3UClient(client);
        var groups = await m.ListGroupsAsync(CancellationToken.None);
        Assert.Equal(2, groups.Count);
        Assert.Equal(11L, groups[0].Id);
        Assert.Equal("Portugal", groups[0].Name);
        Assert.Equal(12L, groups[1].Id);
        Assert.Equal("Cinema", groups[1].Name);
    }

    [Fact]
    public async Task List_groups_accepts_bare_json_array()
    {
        var handler = new JsonHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/api/channels/groups/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new object[]
                    {
                        new { id = 21L, name = "Sports" },
                        new { id = 22L, name = "News" },
                        new { id = 23L, name = "Kids" },
                    }), Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var m = new DispatcharrM3UClient(client);
        var groups = await m.ListGroupsAsync(CancellationToken.None);
        Assert.Equal(3, groups.Count);
        Assert.Equal(21L, groups[0].Id);
        Assert.Equal("Sports", groups[0].Name);
        Assert.Equal(23L, groups[2].Id);
        Assert.Equal("Kids", groups[2].Name);
    }

    [Fact]
    public async Task List_groups_tolerates_leading_whitespace_before_array()
    {
        var handler = new JsonHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/api/channels/groups/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("   \n  " + JsonSerializer.Serialize(new object[]
                    {
                        new { id = 31L, name = "Docs" },
                    }), Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var m = new DispatcharrM3UClient(client);
        var groups = await m.ListGroupsAsync(CancellationToken.None);
        Assert.Single(groups);
        Assert.Equal(31L, groups[0].Id);
    }

    [Fact]
    public async Task List_groups_returns_empty_when_both_shapes_empty()
    {
        var handler = new JsonHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/api/channels/groups/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new object[] { }), Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var m = new DispatcharrM3UClient(client);
        var groups = await m.ListGroupsAsync(CancellationToken.None);
        Assert.Empty(groups);
    }
}

internal sealed class JsonHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
    public JsonHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        => Task.FromResult(_impl(req));
}

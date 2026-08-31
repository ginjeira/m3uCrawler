using m3uCrawler.Services.Dispatcharr;
using Xunit;

namespace m3uCrawler.Tests;

public class DispatcharrExceptionTests
{
    [Fact]
    public void Message_includes_status_code_when_available()
    {
        var ex = new DispatcharrException("/api/channels/groups/", "validation-failed", statusCode: 400);
        Assert.Contains("HTTP 400", ex.Message);
        Assert.Contains("/api/channels/groups/", ex.Message);
    }

    [Fact]
    public void Message_includes_response_body_when_available()
    {
        var ex = new DispatcharrException("/api/channels/groups/", "name required", statusCode: 400);
        Assert.Contains("name required", ex.Message);
    }

    [Fact]
    public void Message_indicates_empty_body_when_body_is_empty()
    {
        var ex = new DispatcharrException("/api/channels/groups/", string.Empty, statusCode: 401);
        Assert.Contains("HTTP 401", ex.Message);
        Assert.Contains("(empty body)", ex.Message);
    }

    [Fact]
    public void Message_indicates_unknown_status_when_status_is_null()
    {
        var ex = new DispatcharrException("/api/channels/groups/", "boom");
        Assert.Contains("/api/channels/groups/", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void Properties_expose_status_endpoint_and_sanitized_message()
    {
        var ex = new DispatcharrException("/api/channels/groups/", "name required", statusCode: 422);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("/api/channels/groups/", ex.Endpoint);
        Assert.Equal("name required", ex.SanitizedMessage);
    }

    [Fact]
    public void Inner_exception_is_preserved()
    {
        var inner = new InvalidOperationException("kaboom");
        var ex = new DispatcharrException("/api/channels/groups/", "io", statusCode: 500, inner: inner);
        Assert.Same(inner, ex.InnerException);
    }
}

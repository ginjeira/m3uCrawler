using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class XtreamPublicationResolverTests
{
    private const string PubUrl = "https://example.com/some-page";

    [Fact]
    public void Resolves_single_account_with_m3u_url_explicit()
    {
        var html = @"<html><body>
            <div class='card'>
                <span>Host</span>: example.com:80<br>
                <span>User</span>: alice<br>
                <span>Pass</span>: secret1<br>
                <span>M3U</span>: <a href='http://example.com:80/get.php?username=alice&password=secret1&type=m3u_plus'>link</a>
            </div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal("example.com", result[0].Host);
        Assert.Equal(80, result[0].Port);
        Assert.Equal("alice", result[0].Username);
        Assert.Equal("secret1", result[0].Password);
        Assert.NotNull(result[0].M3uUrl);
        Assert.Contains("username=alice", result[0].M3uUrl!);
        Assert.Contains("type=m3u_plus", result[0].M3uUrl!);
    }

    [Fact]
    public void Resolves_single_account_with_host_user_pass_no_m3u_url()
    {
        var html = @"<html><body>
            <table>
                <tr><td>Host</td><td>server.example:8080</td></tr>
                <tr><td>User</td><td>bob</td></tr>
                <tr><td>Pass</td><td>pw2</td></tr>
            </table>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal("server.example", result[0].Host);
        Assert.Equal(8080, result[0].Port);
        Assert.Equal("bob", result[0].Username);
        Assert.Equal("pw2", result[0].Password);
        Assert.Null(result[0].M3uUrl);
    }

    [Fact]
    public void Resolves_multiple_accounts_in_same_html()
    {
        var html = @"<html><body>
            <div class='card'>
                Host: srv.example:80<br>
                User: u1<br>
                Pass: p1
            </div>
            <div class='card'>
                Host: srv.example:80<br>
                User: u2<br>
                Pass: p2
            </div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, a => a.Username == "u1");
        Assert.Contains(result, a => a.Username == "u2");
    }

    [Fact]
    public void Preserves_multiple_accounts_on_same_server_as_independent_sources()
    {
        var html = @"<html><body>
            <div>Host = shared.server:80</div>
            <div>User = A</div>
            <div>Pass = pa</div>
            <hr>
            <div>Host = shared.server:80</div>
            <div>User = B</div>
            <div>Pass = pb</div>
            <hr>
            <div>Host = shared.server:80</div>
            <div>User = C</div>
            <div>Pass = pc</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Equal(3, result.Count);
        var users = result.Select(a => a.Username).OrderBy(u => u).ToList();
        Assert.Equal(new[] { "A", "B", "C" }, users);
        foreach (var a in result)
        {
            Assert.Equal("shared.server", a.Host);
            Assert.Equal(80, a.Port);
        }
    }

    [Fact]
    public void Deduplicates_repeated_account_on_same_server()
    {
        var block = @"<div class='card'>
            Host: dup.server:80<br>User: sameuser<br>Pass: pwX
        </div>";
        var html = $"<html><body>{block}{block}<hr>{block}</body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal("sameuser", result[0].Username);
    }

    [Fact]
    public void Normalizes_host_case_and_default_port()
    {
        var html = @"<html><body>
            <div>Host: Example.COM</div>
            <div>User: u1</div>
            <div>Pass: p1</div>
            <hr>
            <div>Host: example.com</div>
            <div>User: u2</div>
            <div>Pass: p2</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Equal(2, result.Count);
        Assert.Equal("example.com", result[0].Host);
        Assert.Equal(80, result[0].Port);
        Assert.Equal("example.com", result[1].Host);
        Assert.Equal(80, result[1].Port);
    }

    [Fact]
    public void Preserves_distinct_hosts_with_same_username()
    {
        var html = @"<html><body>
            <div>Host: a.example:80</div><div>User: same</div><div>Pass: pa</div>
            <hr>
            <div>Host: b.example:80</div><div>User: same</div><div>Pass: pb</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Handles_case_insensitive_labels_host_user_pass()
    {
        var html = @"<html><body>
            <div>HOST: case.example:80</div>
            <div>USER: cu</div>
            <div>PASS: cp</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal("cu", result[0].Username);
        Assert.Equal("cp", result[0].Password);
    }

    [Fact]
    public void Parses_m3u_url_inside_anchor_href()
    {
        var html = @"<html><body>
            <div>
                <p>Host: h.example:80</p>
                <p>User: u</p>
                <p>Pass: p</p>
                <p>M3U: <a href='http://h.example:80/get.php?username=u&password=p&type=m3u_plus'>here</a></p>
            </div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.NotNull(result[0].M3uUrl);
        Assert.Contains("username=u", result[0].M3uUrl!);
    }

    [Fact]
    public void Parses_epg_url_inside_anchor_href()
    {
        var html = @"<html><body>
            <div>Host: h.example:80</div>
            <div>User: u</div>
            <div>Pass: p</div>
            <div>EPG: <a href='http://h.example:80/xmltv.php?username=u&password=p'>xmltv</a></div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.NotNull(result[0].EpgUrl);
        Assert.Contains("xmltv.php", result[0].EpgUrl!);
    }

    [Fact]
    public void Decodes_html_entities_in_values()
    {
        var html = @"<html><body>
            <div>Host: ent.example:80</div>
            <div>User: us&amp;er</div>
            <div>Pass: p&lt;s</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal("us&er", result[0].Username);
        Assert.Equal("p<s", result[0].Password);
    }

    [Fact]
    public void Returns_empty_for_missing_host()
    {
        var html = @"<html><body>
            <div>User: u</div>
            <div>Pass: p</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Empty(result);
    }

    [Fact]
    public void Returns_empty_for_missing_username()
    {
        var html = @"<html><body>
            <div>Host: x.example:80</div>
            <div>Pass: p</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Empty(result);
    }

    [Fact]
    public void Returns_empty_for_missing_password()
    {
        var html = @"<html><body>
            <div>Host: x.example:80</div>
            <div>User: u</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Empty(result);
    }

    [Fact]
    public void Returns_empty_for_invalid_html()
    {
        var html = "<html><body><div>totally unrelated content with no labels at all</div></body></html<broken>";
        var html2 = "<not really html at all";
        var result1 = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        var result2 = XtreamPublicationResolver.ResolveFromHtml(html2, PubUrl);
        Assert.Empty(result1);
        Assert.Empty(result2);
    }

    [Fact]
    public void Returns_empty_for_empty_html()
    {
        Assert.Empty(XtreamPublicationResolver.ResolveFromHtml("", PubUrl));
        Assert.Empty(XtreamPublicationResolver.ResolveFromHtml("   ", PubUrl));
        Assert.Empty(XtreamPublicationResolver.ResolveFromHtml(null!, PubUrl));
    }

    [Fact]
    public void Returns_empty_when_no_xstream_cards_present()
    {
        var html = @"<html><body>
            <h1>MEDIA LIST</h1>
            <ul><li>SIC</li><li>TVI</li><li>RTP</li></ul>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Empty(result);
    }

    [Fact]
    public void Preserves_m3u_url_when_both_explicit_and_host_user_pass_present()
    {
        var html = @"<html><body>
            <div>Host: h.example:80</div>
            <div>User: u</div>
            <div>Pass: p</div>
            <div>M3U: http://h.example:80/get.php?username=u&password=p&type=m3u_plus</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.NotNull(result[0].M3uUrl);
        Assert.Contains("get.php", result[0].M3uUrl!);
    }

    [Fact]
    public void Ignores_media_list_as_channels_when_present_alongside_cards()
    {
        var html = @"<html><body>
            <h2>MEDIA LIST</h2>
            <ul><li>SIC</li><li>TVI</li></ul>
            <hr>
            <div class='card'>
                Host: real.example:80<br>
                User: realu<br>
                Pass: realp
            </div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal("realu", result[0].Username);
    }

    [Fact]
    public void Parses_expires_at_in_iso_format()
    {
        var html = @"<html><body>
            <div>Host: h.example:80</div>
            <div>User: u</div>
            <div>Pass: p</div>
            <div>Expires: 2026-12-31</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.NotNull(result[0].ExpiresAt);
        Assert.Equal(new DateTime(2026, 12, 31), result[0].ExpiresAt);
    }

    [Fact]
    public void Parses_expires_at_in_dd_mm_yyyy_format()
    {
        var html = @"<html><body>
            <div>Host: h.example:80</div>
            <div>User: u</div>
            <div>Pass: p</div>
            <div>Expiration: 15/03/2027</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.NotNull(result[0].ExpiresAt);
        Assert.Equal(new DateTime(2027, 3, 15), result[0].ExpiresAt);
    }

    [Fact]
    public void Parses_expires_at_in_textual_format()
    {
        var html = @"<html><body>
            <div>Host: h.example:80</div>
            <div>User: u</div>
            <div>Pass: p</div>
            <div>Expiry: Mar 15, 2027</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.NotNull(result[0].ExpiresAt);
        Assert.Equal(2027, result[0].ExpiresAt!.Value.Year);
    }

    [Fact]
    public void Leaves_expires_at_null_when_unparseable()
    {
        var html = @"<html><body>
            <div>Host: h.example:80</div>
            <div>User: u</div>
            <div>Pass: p</div>
            <div>Expires: not-a-date</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Null(result[0].ExpiresAt);
    }

    [Fact]
    public void Captures_max_connections_and_other_metadata()
    {
        var html = @"<html><body>
            <div>Host: meta.example:80</div>
            <div>User: u</div>
            <div>Pass: p</div>
            <div>Max Connections: 2</div>
            <div>Active Connections: 0</div>
            <div>Channels: 1234</div>
            <div>VOD: 5678</div>
            <div>Series: 90</div>
            <div>Server: meta-server-name</div>
            <div>IP: 1.2.3.4</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal(2, result[0].MaxConnections);
        Assert.Equal(0, result[0].ActiveConnections);
        Assert.Equal(1234, result[0].ChannelCount);
        Assert.Equal(5678, result[0].VodCount);
        Assert.Equal(90, result[0].SeriesCount);
        Assert.Equal("meta-server-name", result[0].ServerName);
        Assert.Equal("1.2.3.4", result[0].ServerIp);
    }

    [Fact]
    public void Identity_does_not_include_password()
    {
        var html = @"<html><body>
            <div>Host: sameid.example:80</div>
            <div>User: iduser</div>
            <div>Pass: pw1</div>
            <hr>
            <div>Host: sameid.example:80</div>
            <div>User: iduser</div>
            <div>Pass: pwDifferent</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal("iduser", result[0].Username);
        var identity = result[0].LogicalIdentity;
        Assert.DoesNotContain("pw1", identity);
        Assert.DoesNotContain("pwDifferent", identity);
    }

    [Fact]
    public void Accepts_alias_username_and_password_labels()
    {
        var html = @"<html><body>
            <div>Server: aliased.example:80</div>
            <div>Username: usr1</div>
            <div>Password: pwd1</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal("usr1", result[0].Username);
        Assert.Equal("pwd1", result[0].Password);
    }

    [Fact]
    public void Captures_telegram_metadata_when_provided()
    {
        var html = @"<html><body>
            <div>Host: meta.example:80</div>
            <div>User: u</div>
            <div>Pass: p</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl,
            sourceTelegramMessageId: "42", sourceTelegramChannel: "@iptv_channel");
        Assert.Single(result);
        Assert.Equal("42", result[0].SourceTelegramMessageId);
        Assert.Equal("@iptv_channel", result[0].SourceTelegramChannel);
        Assert.Equal(PubUrl, result[0].SourcePublicationUrl);
    }
}

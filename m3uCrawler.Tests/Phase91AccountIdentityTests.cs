using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9A.1 (2026-09-16): testes estruturais para AccountIdentity e
/// a regra da unidade de serializacao.
/// </summary>
public class Phase91AccountIdentityTests
{
    [Fact]
    public void Same_url_and_username_yields_same_identity()
    {
        var id1 = AccountIdentity.FromXtreamUrl(
            "http://host.example:8080/get.php?username=u1&password=p1");
        var id2 = AccountIdentity.FromXtreamUrl(
            "http://host.example:8080/get.php?username=u1&password=p2");
        // Password different nao deve mudar identidade
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Different_username_yields_different_identity()
    {
        var id1 = AccountIdentity.Compute(
            "http://host.example:8080/get.php?username=u1&password=p",
            "u1");
        var id2 = AccountIdentity.Compute(
            "http://host.example:8080/get.php?username=u2&password=p",
            "u2");
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Different_host_yields_different_identity()
    {
        var id1 = AccountIdentity.Compute(
            "http://hostA.example:8080/get.php?username=u1&password=p",
            "u1");
        var id2 = AccountIdentity.Compute(
            "http://hostB.example:8080/get.php?username=u1&password=p",
            "u1");
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Identity_is_short_hex()
    {
        var id = AccountIdentity.Compute(
            "http://host.example/get.php?username=u&password=p",
            "u");
        Assert.Matches("^[0-9a-f]{16}$", id);
    }

    [Fact]
    public void FromXtreamUrl_returns_null_for_non_xtream_url()
    {
        Assert.Null(AccountIdentity.FromXtreamUrl("http://example.com/file.m3u8"));
        Assert.Null(AccountIdentity.FromXtreamUrl(""));
        Assert.Null(AccountIdentity.FromXtreamUrl(null));
    }

    [Fact]
    public void FromXtreamUrl_returns_identity_for_xtream_url()
    {
        var id = AccountIdentity.FromXtreamUrl(
            "http://host.example:8080/get.php?username=u1&password=p1&type=m3u_plus");
        Assert.NotNull(id);
        Assert.Matches("^[0-9a-f]{16}$", id);
    }

    [Fact]
    public void FromXtreamUrl_same_account_different_password_same_identity()
    {
        var id1 = AccountIdentity.FromXtreamUrl(
            "http://host.example:8080/get.php?username=u1&password=p1&type=m3u_plus");
        var id2 = AccountIdentity.FromXtreamUrl(
            "http://host.example:8080/get.php?username=u1&password=p2&type=m3u_plus");
        // Mesma identity - password nao conta.
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ExtractUsername_decodes_url_encoded_values()
    {
        var u = AccountIdentity.ExtractUsername(
            "http://host.example/get.php?username=al%40ice&password=p&type=m3u_plus");
        Assert.Equal("al@ice", u);
    }

    [Fact]
    public void ComputeSafeUrl_strips_password_parameter()
    {
        var safe = AccountIdentity.ComputeSafeUrl(
            "http://host.example/get.php?username=u1&password=SECRET&type=m3u_plus");
        Assert.DoesNotContain("SECRET", safe);
        Assert.DoesNotContain("password", safe);
        Assert.Contains("username=u1", safe);
        Assert.Contains("type=m3u_plus", safe);
    }
}

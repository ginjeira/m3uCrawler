using System;
using System.Collections.Generic;
using System.Linq;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W4 (2026-09-19) — fingerprint canónico de stream
/// (<c>docs/Reestructure/04-PLAYLIST-STREAM.md §4</c>,
/// <c>32-DOMAIN-SCHEMA.md</c> Stream, DL-108).
///
/// <para>
/// Os golden vectors foram calculados <b>independentemente</b> da
/// implementação sob teste (script Python isolado que implementa o
/// contrato canónico e <c>hashlib.sha256("sfp1\n" + canonical)</c>) e
/// congelados aqui. Não são derivados do código de produção.
/// </para>
/// </summary>
public class WaveW4StreamFingerprintTests
{
    // Ordem de teste: URL original → canónico esperado → fingerprint esperado.
    // Proveniência: cálculo one-off independente (Python hashlib), 2026-09-19.
    [Theory]
    [InlineData("HTTP://Example.COM:80/Path/Case%2FKeep#frag",
        "http://example.com/Path/Case%2FKeep",
        "4c217b8284bb3543de6b33d06ea948280702e9ce5f4d4bec136c0ba254877c48")]
    [InlineData("https://Example.COM.:443/a",
        "https://example.com/a",
        "5a817654cfe97cd8e7bbc660bd7e6661d0ce413589a6b146e50747fdfd3069ac")]
    [InlineData("https://Host.EXAMPLE.com:8443/a",
        "https://host.example.com:8443/a",
        "f92734079f3c959dab264860b13b48ad1913757d925d54abec6e4b82090f7549")]
    [InlineData("http://user:secret@host/a",
        "http://host/a",
        "92ffd2c2f3e24776731af732b22dda9f470a0b6370f975e1a2141b1f4e00f08e")]
    [InlineData("http://host/live/USERNAME/PASSWORD/12345",
        "http://host/live/***/***/12345",
        "aa2694b1cc545e72a87fac84a94ebdb18fc2975730d05c89f21c11cf285b2d48")]
    [InlineData("http://host/live/u/p/1?username=u&password=p&token=t",
        "http://host/live/***/***/1",
        "861f8189377eb38d7583196b6c14b8b99f190d0b64f240a89c65a12d229b1b03")]
    [InlineData("http://host/a?b=2&a=1",
        "http://host/a?b=2&a=1",
        "f2dc4c5bd92efae23fde517fae631be2b89dc90f08f2344dde44911077f6a7cf")]
    [InlineData("http://host/a?x=1&username=u&y=2",
        "http://host/a?x=1&y=2",
        "3277a7eaca539a72620b385418f28935f681424482940a72429c08b4c67407a3")]
    [InlineData("http://host/movie/u/p/999?token=abc&lang=en",
        "http://host/movie/***/***/999?lang=en",
        "f702e06c363293ff723b187afe7a65f0158c62ed9936dee2d70dc8543b0a9d66")]
    [InlineData("http://host/series/u/p/42",
        "http://host/series/***/***/42",
        "d0a969d32e285df26bba0b6442f8d32283e1ee93ac04f5151013c800e42ff193")]
    [InlineData("HTTP://HOST:80/a",
        "http://host/a",
        "92ffd2c2f3e24776731af732b22dda9f470a0b6370f975e1a2141b1f4e00f08e")]
    [InlineData("http://host/a",
        "http://host/a",
        "92ffd2c2f3e24776731af732b22dda9f470a0b6370f975e1a2141b1f4e00f08e")]
    [InlineData("http://host/b",
        "http://host/b",
        "35bbdff1711d959fc395092199a517af24b2a8b598aea84cf0bf742b8d5ea20d")]
    [InlineData("http://host/a%2Fb",
        "http://host/a%2Fb",
        "5fa87b9bb52a7bdbc8b7e4460f988cc2d170ea8d550633d66ed4c10f55bd7646")]
    [InlineData("http://host/a/b",
        "http://host/a/b",
        "05f5088a3fdc125244e3c8da45759bb29638dec54bf501b5afac7484c525719e")]
    [InlineData("http://host/a/",
        "http://host/a/",
        "5973ca32ad11020832343853c6b7cb9f041b0c6842aa73fe4f6bda11e1b32ea6")]
    [InlineData("http://host",
        "http://host/",
        "aa001559ac70d598c124ae08fbddcc4a64f38d5cb9b0dc9c9e6d278dcf0cb875")]
    [InlineData("http://host/a?authorization=Bearer%20xyz",
        "http://host/a",
        "92ffd2c2f3e24776731af732b22dda9f470a0b6370f975e1a2141b1f4e00f08e")]
    [InlineData("http://host/get.php?username=u&password=p&type=m3u_plus&output=ts",
        "http://host/get.php?type=m3u_plus&output=ts",
        "9ae9e75cbdaca89ddc24e7ddfa9c91288fbeb9259b6a962efd4895f5af34114e")]
    [InlineData("http://host/a%2f",
        "http://host/a%2f",
        "60efaab5cba20ade93c0e2ef1ecb842f2febae7a9b9eb191d22725db99bca981")]
    [InlineData("https://host:443/a?b=1#frag",
        "https://host/a?b=1",
        "15fbb060fc884614dd41793f6326efe112598f016431d6a135a1ec714e55cab6")]
    [InlineData("http://host/live/u/p",
        "http://host/live/***/***",
        "22d6e7b92a799235c9c9749c29840d3f6e93b1b993400630d6920bfbfcfcdc89")]
    [InlineData("http://host/live/foo",
        "http://host/live/foo",
        "d09dfffe0df20ce547926135eac617da273bc515a9544a48a690f08454305495")]
    [InlineData("http://user:p%40ss@host:8080/Path",
        "http://host:8080/Path",
        "6b1cbbb89e72e8ba5ee2b9e079c829804aaacb115304697ee4892ce98d243b72")]
    [InlineData("http://host:80/a",
        "http://host/a",
        "92ffd2c2f3e24776731af732b22dda9f470a0b6370f975e1a2141b1f4e00f08e")]
    [InlineData("http://host:8080/a",
        "http://host:8080/a",
        "14dc84e66e7170bb105ee2dfa104c0bf3215b7b0effb5d89cefc01d54c852001")]
    [InlineData("http://host/a?token=t",
        "http://host/a",
        "92ffd2c2f3e24776731af732b22dda9f470a0b6370f975e1a2141b1f4e00f08e")]
    [InlineData("http://host/movie/u/p/999?username=u&password=p",
        "http://host/movie/***/***/999",
        "f873aa0faeb956838d68193fb0193567ed7882ab754b3a25fff3b92cd39fe7a0")]
    [InlineData("https://host/get.php?username=u&password=p&type=m3u_plus&output=ts",
        "https://host/get.php?type=m3u_plus&output=ts",
        "ee13cc39dac6555b7b0e92165cc7edc50619bf5c77c12c2982071b040e79c804")]
    [InlineData("http://host/a?username=u",
        "http://host/a",
        "92ffd2c2f3e24776731af732b22dda9f470a0b6370f975e1a2141b1f4e00f08e")]
    [InlineData("http://host/live/u/p/1?x=1&username=a&password=b&y=2&token=c",
        "http://host/live/***/***/1?x=1&y=2",
        "351163bf4d8e98b786eead235f768e6dafa7d0052f2a87de86e2384caa17c04e")]
    public void Golden_vectors_are_frozen(string url, string expectedCanonical, string expectedFingerprint)
    {
        var created = StreamFingerprint.TryCreate(url, out var canonical, out var fingerprint);

        Assert.True(created);
        Assert.Equal(expectedCanonical, canonical);
        Assert.Equal(expectedFingerprint, fingerprint);
        Assert.Equal(StreamFingerprint.HashLength, fingerprint!.Length);
        Assert.Equal(fingerprint, fingerprint.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://host/a")]
    [InlineData("rtsp://host/a")]
    public void Non_http_or_unparseable_input_produces_no_fingerprint(string? url)
    {
        var created = StreamFingerprint.TryCreate(url, out var canonical, out var fingerprint);

        Assert.False(created);
        Assert.Null(canonical);
        Assert.Null(fingerprint);
        Assert.Null(StreamFingerprint.TryComputeFingerprint(url));
    }

    [Fact]
    public void Version_is_part_of_the_algorithm_and_persisted_with_the_hash()
    {
        Assert.Equal("sfp1", StreamFingerprint.Version);
    }

    [Fact]
    public void Same_url_always_yields_the_same_fingerprint()
    {
        const string url = "https://Host.Example:8443/Live/U/P/9?b=2&a=1";

        var first = StreamFingerprint.TryComputeFingerprint(url);
        var second = StreamFingerprint.TryComputeFingerprint(url);

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Scheme_and_host_case_and_default_port_are_equivalent()
    {
        var baseline = StreamFingerprint.TryComputeFingerprint("http://host/a");

        Assert.Equal(baseline, StreamFingerprint.TryComputeFingerprint("HTTP://HOST:80/a"));
        Assert.Equal(baseline, StreamFingerprint.TryComputeFingerprint("http://host:80/a"));
        Assert.Equal(baseline, StreamFingerprint.TryComputeFingerprint("http://HOST/a"));
    }

    [Fact]
    public void Non_default_port_changes_the_fingerprint()
    {
        var baseline = StreamFingerprint.TryComputeFingerprint("http://host/a");
        var withPort = StreamFingerprint.TryComputeFingerprint("http://host:8080/a");

        Assert.NotNull(baseline);
        Assert.NotNull(withPort);
        Assert.NotEqual(baseline, withPort);
    }

    [Fact]
    public void Fragment_is_ignored()
    {
        Assert.Equal(
            StreamFingerprint.TryComputeFingerprint("http://host/a?b=1"),
            StreamFingerprint.TryComputeFingerprint("http://host/a?b=1#section"));
        Assert.Equal(
            StreamFingerprint.TryComputeFingerprint("http://host/a"),
            StreamFingerprint.TryComputeFingerprint("http://host/a#frag"));
    }

    [Fact]
    public void Percent_encoding_is_not_decoded()
    {
        Assert.NotEqual(
            StreamFingerprint.TryComputeFingerprint("http://host/a%2Fb"),
            StreamFingerprint.TryComputeFingerprint("http://host/a/b"));
    }

    [Fact]
    public void Query_order_is_preserved_and_not_sorted()
    {
        StreamFingerprint.TryCreate("http://host/a?b=2&a=1", out var canonical, out _);

        Assert.Equal("http://host/a?b=2&a=1", canonical);
    }

    [Fact]
    public void Credential_variations_with_same_functional_id_share_fingerprint()
    {
        var alice = StreamFingerprint.TryComputeFingerprint("http://host/live/alice/secretA/12345");
        var bob = StreamFingerprint.TryComputeFingerprint("http://host/live/bob/secretB/12345");

        Assert.NotNull(alice);
        Assert.Equal(alice, bob);
    }

    [Fact]
    public void Credential_variations_of_different_ids_are_distinct()
    {
        var id1 = StreamFingerprint.TryComputeFingerprint("http://host/live/alice/secretA/1");
        var id2 = StreamFingerprint.TryComputeFingerprint("http://host/live/alice/secretA/2");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Secrets_never_appear_in_the_canonical_input_or_fingerprint()
    {
        var secretUrls = new[]
        {
            "http://alice:supersecret@host/a",
            "http://host/live/alice/supersecret/12345",
            "http://host/get.php?username=alice&password=supersecret&token=tok_abc",
            "http://host/a?authorization=Bearer%20supersecret",
        };

        foreach (var url in secretUrls)
        {
            var created = StreamFingerprint.TryCreate(url, out var canonical, out var fingerprint);

            Assert.True(created);
            Assert.DoesNotContain("supersecret", canonical);
            Assert.DoesNotContain("alice", canonical);
            Assert.DoesNotContain("tok_abc", canonical);
            Assert.DoesNotContain("Bearer", canonical);
            Assert.Equal(StreamFingerprint.HashLength, fingerprint!.Length);
            Assert.DoesNotContain("supersecret", fingerprint);
            Assert.DoesNotContain("alice", fingerprint);
        }
    }

    [Fact]
    public void Credential_only_query_is_removed_with_its_separator()
    {
        StreamFingerprint.TryCreate("http://host/a?username=u&password=p&token=t", out var canonical, out _);

        Assert.Equal("http://host/a", canonical);
        Assert.DoesNotContain("?", canonical);
    }

    [Fact]
    public void Xtream_id_is_preserved_while_user_and_pass_are_masked()
    {
        StreamFingerprint.TryCreate("http://host/live/alice/secret/987654", out var canonical, out _);

        Assert.Equal("http://host/live/***/***/987654", canonical);
        Assert.Contains("987654", canonical);
    }

    [Fact]
    public void Non_xtream_path_is_untouched()
    {
        StreamFingerprint.TryCreate("http://host/video/season/episode.mp4", out var canonical, out _);

        Assert.Equal("http://host/video/season/episode.mp4", canonical);
    }

    [Fact]
    public void TryCreate_never_throws_for_hostile_input()
    {
        var hostile = new[]
        {
            "http://",
            "http://[::1",
            "http://host:/a",
            "http://host:99999999/a",
            "://host/a",
            "http://user@:8080/a",
            new string('a', 5000),
        };

        foreach (var url in hostile)
        {
            var ex = Record.Exception(() => StreamFingerprint.TryCreate(url, out _, out _));
            Assert.Null(ex);
        }
    }
}

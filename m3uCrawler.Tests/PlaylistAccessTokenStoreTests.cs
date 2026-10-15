using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

public sealed class PlaylistAccessTokenStoreTests : IDisposable
{
    private readonly string _dir;

    public PlaylistAccessTokenStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"pat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string TokenPath => Path.Combine(_dir, "playlist_access_token.json");

    [Fact]
    public void No_file_means_no_token()
    {
        var store = new PlaylistAccessTokenStore(_dir);

        Assert.False(store.HasToken());
        Assert.Null(store.GetHash());
        Assert.False(store.Matches("x"));
    }

    [Fact]
    public void Set_then_matches_only_the_same_token()
    {
        var store = new PlaylistAccessTokenStore(_dir);
        const string token = "abc-0123456789-xyz";

        store.Set(token);

        Assert.True(store.HasToken());
        Assert.True(store.Matches(token));
        Assert.False(store.Matches("outro-token"));
    }

    [Fact]
    public void Token_is_persisted_and_reloaded_by_a_new_store()
    {
        const string token = "persisted-secret-token";
        new PlaylistAccessTokenStore(_dir).Set(token);

        var reloaded = new PlaylistAccessTokenStore(_dir);

        Assert.True(reloaded.HasToken());
        Assert.True(reloaded.Matches(token));
        Assert.False(reloaded.Matches("other"));
    }

    [Fact]
    public void Clear_removes_the_token()
    {
        var store = new PlaylistAccessTokenStore(_dir);
        const string token = "clear-me";
        store.Set(token);

        store.Clear();

        Assert.False(store.HasToken());
        Assert.Null(store.GetHash());
        Assert.False(store.Matches(token));
        Assert.False(File.Exists(TokenPath));
    }

    [Fact]
    public void Plaintext_token_never_appears_in_the_file()
    {
        var store = new PlaylistAccessTokenStore(_dir);
        const string token = "super-secret-plaintext-token";

        store.Set(token);

        var contents = File.ReadAllText(TokenPath);
        Assert.DoesNotContain(token, contents);
        Assert.Contains("tokenHash", contents);
    }

    [Fact]
    public void Corrupt_file_is_treated_as_no_token()
    {
        File.WriteAllText(TokenPath, "{not json");
        var store = new PlaylistAccessTokenStore(_dir);

        Assert.False(store.HasToken());
        Assert.Null(store.GetHash());
        Assert.False(store.Matches("x"));
    }

    [Fact]
    public void Null_or_empty_token_is_rejected()
    {
        var store = new PlaylistAccessTokenStore(_dir);

        Assert.Throws<ArgumentException>(() => store.Set(null!));
        Assert.Throws<ArgumentException>(() => store.Set(string.Empty));
        Assert.Throws<ArgumentException>(() => store.Set("   "));
    }

    [Fact]
    public void Unix_file_mode_is_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;

        var store = new PlaylistAccessTokenStore(_dir);
        store.Set("mode-token");

        var mode = File.GetUnixFileMode(TokenPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        Assert.Equal(0, (int)(mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)));
        Assert.Equal(0, (int)(mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)));
    }
}

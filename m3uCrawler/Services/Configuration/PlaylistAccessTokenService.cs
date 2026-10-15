using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Token de máquina que autoriza exclusivamente as playlists M3U
/// (<c>/api/playlist</c>, <c>/api/playlist_temp</c>,
/// <c>/api/playlist_acquired</c>), guardado de forma cifrada na base de
/// dados através de <see cref="AppSecretStore"/>.
///
/// <para>
/// Substitui o antigo ficheiro <c>playlist_access_token.json</c>. O valor
/// em claro nunca é impresso nem registado; apenas é devolvido por
/// <see cref="GetTokenAsync"/> para a construção de links prontos a
/// copiar. A comparação em <see cref="MatchesAsync"/> é feita em tempo
/// constante (<see cref="CryptographicOperations.FixedTimeEquals"/> sobre
/// UTF-8).
/// </para>
/// </summary>
public sealed class PlaylistAccessTokenService
{
    /// <summary>Chave lógica do segredo na tabela <c>app_secrets</c>.</summary>
    public const string SecretKey = "playlist_access_token";

    private readonly AppSecretStore _store;

    public PlaylistAccessTokenService(AppSecretStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Verdadeiro se existir um token persistido e não vazio.</summary>
    public async Task<bool> HasTokenAsync()
    {
        var token = await _store.GetAsync(SecretKey);
        return !string.IsNullOrEmpty(token);
    }

    /// <summary>
    /// Token em claro (decifrado), ou <c>null</c> se não existir. Nunca
    /// deve ser registado nem devolvido por endpoints de leitura de estado.
    /// </summary>
    public Task<string?> GetTokenAsync() => _store.GetAsync(SecretKey);

    /// <summary>
    /// Persiste (cifrado) o token fornecido. Token nulo/vazio/whitespace →
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public async Task SetTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token must not be null, empty or whitespace.", nameof(token));

        await _store.SetAsync(SecretKey, token);
    }

    /// <summary>Remove o token persistido (idempotente).</summary>
    public Task ClearAsync() => _store.DeleteAsync(SecretKey);

    /// <summary>
    /// Compara o token apresentado com o persistido em tempo constante.
    /// Devolve <c>false</c> se não houver token persistido ou se o
    /// apresentado for nulo/vazio.
    /// </summary>
    public async Task<bool> MatchesAsync(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;

        var stored = await _store.GetAsync(SecretKey);
        if (string.IsNullOrEmpty(stored))
            return false;

        var storedBytes = Encoding.UTF8.GetBytes(stored);
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(storedBytes, presentedBytes);
    }
}

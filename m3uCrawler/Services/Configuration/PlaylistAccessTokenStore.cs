using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Persistência do token de máquina que autoriza exclusivamente as
/// playlists M3U (<c>/api/playlist</c>, <c>/api/playlist_temp</c>,
/// <c>/api/playlist_acquired</c>).
///
/// <para>
/// O token em claro <b>nunca</b> é guardado: apenas o seu hash SHA-256
/// (base64) e metadados. A leitura tolera ficheiro ausente, JSON
/// inválido ou base64 corrompido, tratando-os como "sem token". A
/// escrita é atómica (temp + move) e aplica <c>chmod 600</c> em Unix.
/// Nunca regista o token nem o hash.
/// </para>
/// </summary>
public sealed class PlaylistAccessTokenStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public PlaylistAccessTokenStore(string runtimeDataDir)
    {
        if (runtimeDataDir is null) throw new ArgumentNullException(nameof(runtimeDataDir));
        _path = Path.Combine(runtimeDataDir, "playlist_access_token.json");
    }

    /// <summary>
    /// Verdadeiro se existir um hash não vazio persistido. Nunca lança
    /// por ficheiro ausente/corrompido (trata como "sem token").
    /// </summary>
    public bool HasToken()
    {
        var stored = GetHash();
        return !string.IsNullOrEmpty(stored);
    }

    /// <summary>
    /// Hash base64 persistido, ou <c>null</c> se não existir token ou o
    /// ficheiro estiver ausente/corrompido.
    /// </summary>
    public string? GetHash()
    {
        lock (_gate)
        {
            var record = ReadRecord();
            var hash = record?.TokenHash;
            return string.IsNullOrEmpty(hash) ? null : hash;
        }
    }

    /// <summary>
    /// Compara o token apresentado com o hash persistido em tempo
    /// constante. Retorna <c>false</c> se não houver hash, se o
    /// apresentado for nulo/vazio, ou se o hash guardado estiver
    /// corrompido. Nunca lança.
    /// </summary>
    public bool Matches(string? presentedToken)
    {
        if (string.IsNullOrEmpty(presentedToken))
            return false;

        var storedHash = GetHash();
        if (string.IsNullOrEmpty(storedHash))
            return false;

        byte[] storedBytes;
        try
        {
            storedBytes = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var presentedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(presentedToken));
        return CryptographicOperations.FixedTimeEquals(presentedBytes, storedBytes);
    }

    /// <summary>
    /// Grava o hash SHA-256 (base64) do token, de forma atómica. Token
    /// nulo/vazio/whitespace → <see cref="ArgumentException"/>. Pode
    /// propagar <see cref="IOException"/> reais da escrita.
    /// </summary>
    public void Set(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token must not be null, empty or whitespace.", nameof(token));

        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var record = new PlaylistAccessTokenRecord
            {
                SchemaVersion = CurrentSchemaVersion,
                TokenHash = hash,
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };

            var json = JsonSerializer.Serialize(record, JsonOptions);
            WriteAtomic(_path, json);
        }
    }

    /// <summary>
    /// Remove o ficheiro de token, se existir (idempotente). Pode
    /// propagar <see cref="IOException"/> reais da remoção.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
    }

    private PlaylistAccessTokenRecord? ReadRecord()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<PlaylistAccessTokenRecord>(json, JsonOptions);
        }
        catch (JsonException)
        {
            Console.WriteLine(
                $"[PlaylistAccessTokenStore] Ignoring corrupt access token file at {_path}.");
            return null;
        }
    }

    private static void WriteAtomic(string target, string contents)
    {
        var directory = Path.GetDirectoryName(target) ?? ".";
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Permissões restritivas são best-effort e não fatais.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Limpeza de temp é best-effort.
        }
    }

    private sealed class PlaylistAccessTokenRecord
    {
        public int SchemaVersion { get; set; }

        public string? TokenHash { get; set; }

        public string? UpdatedAtUtc { get; set; }
    }
}

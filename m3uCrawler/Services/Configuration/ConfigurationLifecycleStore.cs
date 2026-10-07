using System.Text.Json;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// PHASE 9C.1 — Persistência do estado do lifecycle de configuração.
///
/// <para>
/// Reutiliza o mecanismo de estado persistente já existente no projecto
/// (ficheiro JSON na pasta de runtime-data, ao lado do
/// <c>channel-catalog.db</c> e do <c>stream_validation_policy.json</c>),
/// em vez de introduzir uma nova base de dados ou uma tabela nova no
/// schema do catálogo. O ficheiro vive no mesmo volume persistente
/// (<c>/data</c> em produção), pelo que sobrevive a restart e a
/// recreação do container desde que os dados persistentes sejam mantidos.
/// </para>
///
/// <para>
/// Escrita atómica: ficheiro temporário + <see cref="File.Move(string,string,bool)"/>
/// para nunca deixar um ficheiro meio escrito.
/// </para>
/// </summary>
public sealed class ConfigurationLifecycleStore
{
    /// <summary>Nome canónico do ficheiro dentro do directório persistente.</summary>
    public const string FileName = "configuration_lifecycle.json";

    private const string CurrentSchemaVersion = "1.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();

    public ConfigurationLifecycleStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("filePath é obrigatório.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    /// <summary>
    /// Constrói um store ao lado do ficheiro SQLite do catálogo, no mesmo
    /// directório persistente.
    /// </summary>
    public static ConfigurationLifecycleStore ForCatalogDatabase(string catalogDbPath)
    {
        if (string.IsNullOrWhiteSpace(catalogDbPath))
        {
            throw new ArgumentException("catalogDbPath é obrigatório.", nameof(catalogDbPath));
        }

        var full = Path.GetFullPath(catalogDbPath);
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        return new ConfigurationLifecycleStore(Path.Combine(directory, FileName));
    }

    /// <summary>
    /// Lê o estado persistido. Devolve <c>null</c> se o ficheiro não
    /// existir ou não puder ser lido/interpretado (nesse caso o
    /// chamador volta a avaliar o bootstrap).
    /// </summary>
    public ConfigurationLifecycleSnapshot? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                var persisted = JsonSerializer.Deserialize<PersistedLifecycle>(json, JsonOptions);
                if (persisted == null)
                {
                    return null;
                }

                if (!ConfigurationLifecycleStateNames.TryParse(persisted.State, out var state))
                {
                    return null;
                }

                return new ConfigurationLifecycleSnapshot(
                    state,
                    persisted.AdoptedFromLegacy,
                    persisted.AdoptedAtUtc,
                    persisted.LastReason ?? string.Empty,
                    persisted.UpdatedAtUtc == default ? DateTime.UtcNow : persisted.UpdatedAtUtc);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Persiste o estado de forma atómica.
    /// </summary>
    public void Save(ConfigurationLifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var persisted = new PersistedLifecycle
            {
                SchemaVersion = CurrentSchemaVersion,
                State = snapshot.State.ToWireName(),
                AdoptedFromLegacy = snapshot.AdoptedFromLegacy,
                AdoptedAtUtc = snapshot.AdoptedAtUtc,
                LastReason = snapshot.LastReason,
                UpdatedAtUtc = snapshot.UpdatedAtUtc,
            };

            var json = JsonSerializer.Serialize(persisted, JsonOptions);
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, FilePath, overwrite: true);
        }
    }

    private sealed class PersistedLifecycle
    {
        public string SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string State { get; set; } = ConfigurationLifecycleStateNames.NotConfigured;
        public bool AdoptedFromLegacy { get; set; }
        public DateTime? AdoptedAtUtc { get; set; }
        public string? LastReason { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}

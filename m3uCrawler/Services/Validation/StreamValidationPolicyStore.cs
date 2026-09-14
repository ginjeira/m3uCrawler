using System.Text.Json;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// Persiste a política operacional de validação de streams num ficheiro JSON
/// versionado dentro de <c>runtime-data</c>. Os defaults são os definidos
/// em <see cref="StreamValidationOptions"/>.
/// </summary>
public sealed class StreamValidationPolicyStore
{
    private const string CurrentFileVersion = "1.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public StreamValidationPolicyStore(string runtimeDataDir)
    {
        _path = Path.Combine(runtimeDataDir, "stream_validation_policy.json");
    }

    public string FilePath => _path;

    public StreamValidationOptions Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                var fresh = new StreamValidationOptions();
                Save(fresh);
                return fresh;
            }

            try
            {
                var json = File.ReadAllText(_path);
                var policy = JsonSerializer.Deserialize<StreamValidationOptions>(json, JsonOptions);
                if (policy == null)
                {
                    var fresh = new StreamValidationOptions();
                    Save(fresh);
                    return fresh;
                }
                policy.Sanitize();
                return policy;
            }
            catch
            {
                var fresh = new StreamValidationOptions();
                Save(fresh);
                return fresh;
            }
        }
    }

    public StreamValidationOptions Save(StreamValidationOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        options.Sanitize();
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(options, JsonOptions);
            File.WriteAllText(_path, json);
        }
        return options;
    }

    public string FileVersion => CurrentFileVersion;
}

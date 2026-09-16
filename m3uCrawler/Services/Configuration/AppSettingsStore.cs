using System.Text.Json;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Configuração operacional global da aplicação, persistida em
/// <c>runtime-data/app_settings.json</c> (mesmo padrão de
/// <c>StreamValidationPolicyStore</c>).
///
/// <para>
/// <see cref="AffinityVariantDelimiter"/> é o separador usado na
/// UI/API para dividir o campo único de variantes de uma afinidade.
/// É apenas uma convenção de input/edição: as variantes continuam
/// persistidas como <c>AffinityMember</c>, uma por registo. Alterar
/// o delimiter não exige migration dos dados existentes.
/// </para>
/// </summary>
public sealed class AppSettings
{
    /// <summary>Separador de variantes. Default: <c>,</c>.</summary>
    public string AffinityVariantDelimiter { get; set; } = ",";

    /// <summary>
    /// Normaliza o delimiter: trim, vazio/inválido → default. Aceita
    /// apenas 1..3 caracteres sem quebras de linha.
    /// </summary>
    public void Sanitize()
    {
        var value = AffinityVariantDelimiter ?? string.Empty;
        value = value.Trim();
        if (value.Length == 0
            || value.Length > 3
            || value.Contains('\n')
            || value.Contains('\r'))
        {
            value = ",";
        }
        AffinityVariantDelimiter = value;
    }
}

/// <summary>
/// Store JSON idempotente para <see cref="AppSettings"/>. Cria o
/// ficheiro com defaults na primeira leitura; nunca falha o arranque
/// por conteúdo inválido (recupera para defaults).
/// </summary>
public sealed class AppSettingsStore
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

    public AppSettingsStore(string runtimeDataDir)
    {
        _path = Path.Combine(runtimeDataDir, "app_settings.json");
    }

    public string FilePath => _path;

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                var fresh = new AppSettings();
                Save(fresh);
                return fresh;
            }

            try
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings == null)
                {
                    var fresh = new AppSettings();
                    Save(fresh);
                    return fresh;
                }
                settings.Sanitize();
                return settings;
            }
            catch
            {
                var fresh = new AppSettings();
                Save(fresh);
                return fresh;
            }
        }
    }

    public AppSettings Save(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        settings.Sanitize();
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_path, json);
        }
        return settings;
    }

    public string FileVersion => CurrentFileVersion;
}

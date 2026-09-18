using System.Reflection;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Provisiona os ficheiros de configuração de país (<c>*.json</c>) no
/// arranque, para que uma instalação nova tenha a baseline disponível
/// mesmo antes do bind mount de <c>runtime-data</c> ser populado.
/// Nunca sobrepõe ficheiros existentes.
/// </summary>
public static class CountryConfigProvisioner
{
    // O MSBuild sanitiza 'runtime-data' para 'runtime_data' no nome do
    // manifest resource, pelo que aceitamos as duas formas.
    private static readonly string[] EmbeddedResourceMarkers =
    {
        ".runtime-data.countries.",
        ".runtime_data.countries.",
    };

    private const string JsonSuffix = ".json";

    internal const string PortugalBaselineFileName = "pt.json";

    /// <summary>
    /// Baseline embutida de <c>pt.json</c>. É usada como fallback quando o
    /// assembly não expõe o EmbeddedResource (por exemplo no build Docker,
    /// onde <c>.dockerignore</c> exclui <c>runtime-data/</c> do contexto).
    /// </summary>
    internal const string PortugalBaselineJson =
        """
        {
          "country": "pt",
          "channels": [
            "RTP1",
            "RTP 1",
            "RTP1 HD",
            "RTP 1 HD",
            "RTP2",
            "RTP 2",
            "SIC",
            "SIC HD",
            "TVI",
            "TVI HD",
            "TVI24",
            "SPORT TV",
            "SPORTTV",
            "SPORT TV 1",
            "SPORTTV1",
            "BTV",
            "BTV HD",
            "BENFICATV",
            "BENFICA TV",
            "CANAL 11",
            "C11",
            "RTP3",
            "RTP 3"
          ]
        }
        """;

    /// <summary>
    /// Garante que <paramref name="countriesDirectory"/> contém a baseline
    /// de países. Cria o diretório se necessário. Nunca sobrepõe ficheiros
    /// já existentes. Falhas por ficheiro são registadas e não abortam o
    /// arranque.
    /// </summary>
    public static void EnsureProvisioned(string countriesDirectory)
    {
        if (string.IsNullOrWhiteSpace(countriesDirectory))
            return;

        try
        {
            Directory.CreateDirectory(countriesDirectory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Não foi possível criar a pasta de configuração de países: {ex.Message}");
            return;
        }

        ProvisionEmbeddedResources(countriesDirectory);
        ProvisionBuiltInBaselines(countriesDirectory);
    }

    private static void ProvisionEmbeddedResources(string countriesDirectory)
    {
        var assembly = typeof(CountryConfigProvisioner).Assembly;
        string[] names;
        try
        {
            names = assembly.GetManifestResourceNames();
        }
        catch
        {
            return;
        }

        foreach (var name in names)
        {
            var markerIndex = FindMarkerIndex(name);
            if (markerIndex < 0)
                continue;
            if (!name.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var fileName = name[(markerIndex + EmbeddedResourceMarkers[0].Length)..];
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            ProvisionFromStream(countriesDirectory, fileName, () => assembly.GetManifestResourceStream(name));
        }
    }

    private static int FindMarkerIndex(string resourceName)
    {
        foreach (var marker in EmbeddedResourceMarkers)
        {
            var index = resourceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                return index;
        }

        return -1;
    }

    private static void ProvisionBuiltInBaselines(string countriesDirectory)
    {
        ProvisionFromStream(
            countriesDirectory,
            PortugalBaselineFileName,
            () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(PortugalBaselineJson)));
    }

    private static void ProvisionFromStream(
        string countriesDirectory,
        string fileName,
        Func<Stream?> streamFactory)
    {
        var target = Path.Combine(countriesDirectory, fileName);
        if (File.Exists(target))
            return;

        try
        {
            using var stream = streamFactory();
            if (stream is null)
                return;

            WriteNewFileAtomic(target, stream);
        }
        catch (Exception ex)
        {
            // Mensagem não sensível: apenas nome do ficheiro e causa.
            Console.WriteLine($"⚠️ Falha ao provisionar configuração de país '{fileName}': {ex.Message}");
        }
    }

    private static void WriteNewFileAtomic(string target, Stream content)
    {
        var directory = Path.GetDirectoryName(target) ?? ".";
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fileStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                content.CopyTo(fileStream);
            }

            // Corrida: nunca sobrepõe um ficheiro criado entretanto.
            if (File.Exists(target))
                return;

            File.Move(temp, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }
}

using m3uCrawler.Models;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Acesso persistente a <c>wtelegram.config</c>. A leitura reutiliza o
/// parser existente (<see cref="WtelegramConfigFile.Read"/>) para manter
/// um único contrato de parsing. A escrita é in-place, preservando
/// comentários, chaves desconhecidas e a ordem das linhas.
/// </summary>
public interface IWtelegramConfigStore
{
    /// <summary>Lê o par chave/valor efetivo (chaves case-insensitive).</summary>
    IReadOnlyDictionary<string, string> Read();

    /// <summary>
    /// Resolve o ficheiro a usar para escrita: o primeiro existente de
    /// <c>AppContext.BaseDirectory/wtelegram.config</c> e
    /// <c>Directory.GetCurrentDirectory()/wtelegram.config</c>;
    /// caso nenhum exista, <c>Directory.GetCurrentDirectory()/wtelegram.config</c>.
    /// </summary>
    string ResolvePathForWrite();

    /// <summary>
    /// Atualiza as chaves indicadas preservando o resto do ficheiro. Chaves
    /// existentes são substituídas in-place (match case-insensitive); chaves
    /// novas são acrescentadas no fim. Escrita atómica (temp + move).
    /// </summary>
    void Upsert(IReadOnlyDictionary<string, string> values);
}

/// <summary>
/// Implementação file-system do <see cref="IWtelegramConfigStore"/>.
/// Nunca regista nem expõe valores.
/// </summary>
public sealed class WtelegramConfigStore : IWtelegramConfigStore
{
    private const string FileName = "wtelegram.config";

    private readonly string? _explicitPath;
    private readonly object _gate = new();

    public WtelegramConfigStore()
    {
    }

    /// <summary>
    /// Construtor para testes: fixa o ficheiro alvo em vez de o resolver
    /// a partir de <c>AppContext.BaseDirectory</c>/<c>CurrentDirectory</c>.
    /// </summary>
    internal WtelegramConfigStore(string explicitPath)
    {
        _explicitPath = explicitPath;
    }

    public IReadOnlyDictionary<string, string> Read()
    {
        if (_explicitPath is not null)
            return ParseFile(_explicitPath);

        return WtelegramConfigFile.Read();
    }

    public string ResolvePathForWrite()
    {
        if (_explicitPath is not null)
            return _explicitPath;

        var besideExecutable = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(besideExecutable))
            return besideExecutable;

        return Path.Combine(Directory.GetCurrentDirectory(), FileName);
    }

    public void Upsert(IReadOnlyDictionary<string, string> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));

        lock (_gate)
        {
            var target = ResolvePathForWrite();
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var lines = File.Exists(target)
                ? new List<string>(File.ReadAllLines(target))
                : new List<string>();

            var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = trimmed[..separator].Trim();
                if (applied.Contains(key))
                    continue;

                if (values.TryGetValue(key, out var value))
                {
                    // Preserva o estilo/casing da chave já presente no ficheiro.
                    lines[i] = $"{key}={value}";
                    applied.Add(key);
                }
            }

            foreach (var pair in values)
            {
                if (!applied.Contains(pair.Key))
                    lines.Add($"{pair.Key}={pair.Value}");
            }

            WriteAtomic(target, lines);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseFile(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return dict;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            dict[key] = value;
        }

        return dict;
    }

    private static void WriteAtomic(string target, IReadOnlyList<string> lines)
    {
        var directory = Path.GetDirectoryName(target) ?? ".";
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(temp, lines);
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
}

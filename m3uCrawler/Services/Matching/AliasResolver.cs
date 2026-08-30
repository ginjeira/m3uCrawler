using m3uCrawler.Models;

namespace m3uCrawler.Services.Matching
{
    public sealed class AliasResolver
    {
        private readonly IReadOnlyDictionary<string, string> _byNormalizedAlias;
        private readonly IReadOnlyDictionary<string, string> _byCanonicalKey;

        public AliasResolver(IReadOnlyDictionary<string, string>? aliasMap = null)
        {
            aliasMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _byCanonicalKey = aliasMap;

            var reverse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in aliasMap)
            {
                var canonicalNorm = ChannelNormalizer.Normalize(kv.Value);
                if (string.IsNullOrWhiteSpace(canonicalNorm)) continue;
                if (!reverse.ContainsKey(canonicalNorm))
                    reverse[canonicalNorm] = canonicalNorm;
                foreach (var alias in kv.Key.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var aliasNorm = ChannelNormalizer.Normalize(alias);
                    if (string.IsNullOrWhiteSpace(aliasNorm)) continue;
                    reverse[aliasNorm] = canonicalNorm;
                }
            }
            _byNormalizedAlias = reverse;
        }

        public static AliasResolver FromFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new AliasResolver(new Dictionary<string, string>());

            try
            {
                var raw = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new InvalidDataException($"Alias file root must be an object: {path}");
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    string canonical = prop.Name.Trim();
                    if (string.IsNullOrWhiteSpace(canonical)) continue;
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var aliases = prop.Value.EnumerateArray()
                            .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                            .Select(e => e.GetString() ?? string.Empty)
                            .Where(a => !string.IsNullOrWhiteSpace(a))
                            .ToArray();
                        if (aliases.Length > 0)
                            map[string.Join("|", aliases)] = canonical;
                    }
                    else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var alias = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(alias))
                            map[alias] = canonical;
                    }
                }
                return new AliasResolver(map);
            }
            catch (System.Text.Json.JsonException jx)
            {
                throw new InvalidDataException($"Alias file is not valid JSON ({path}): {jx.Message}", jx);
            }
        }

        public AliasResolution? Resolve(string name)
        {
            var norm = ChannelNormalizer.Normalize(name);
            if (string.IsNullOrWhiteSpace(norm)) return null;
            if (_byNormalizedAlias.TryGetValue(norm, out var canonical))
                return new AliasResolution(canonical, "alias");
            return null;
        }
    }

    public sealed record AliasResolution(string Canonical, string Reason);
}

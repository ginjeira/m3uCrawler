namespace m3uCrawler.Models
{
    public sealed class DispatcharrConfig
    {
        public bool Enabled { get; init; }
        public string BaseUrl { get; init; } = string.Empty;
        public string? ApiKey { get; init; }
        public string? Username { get; init; }
        public string? Password { get; init; }
        public bool DryRun { get; init; } = true;
        public int MatchThreshold { get; init; } = 80;
        public string? AliasFile { get; init; }
        public IReadOnlyList<string> ProviderPriority { get; init; } = Array.Empty<string>();
        public bool AutoCreateGroups { get; init; } = true;
        public string? TargetGroupName { get; init; }

        public static DispatcharrConfig Disabled() => new() { Enabled = false };
    }

    public static class DispatcharrConfigLoader
    {
        public static DispatcharrConfig Load()
        {
            var values = WtelegramConfigFile.Read();
            bool enabled = values.TryGetValue("dispatcharr_enabled", out var enRaw)
                           && ParseBool(enRaw);

            if (!enabled)
                return DispatcharrConfig.Disabled();

            var baseUrl = values.TryGetValue("dispatcharr_base_url", out var b) ? b.Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(baseUrl))
                return DispatcharrConfig.Disabled();

            string? apiKey = values.TryGetValue("dispatcharr_api_key", out var ak) && !string.IsNullOrWhiteSpace(ak)
                ? ak.Trim() : null;
            string? username = values.TryGetValue("dispatcharr_username", out var u) && !string.IsNullOrWhiteSpace(u)
                ? u.Trim() : null;
            string? password = values.TryGetValue("dispatcharr_password", out var p) && !string.IsNullOrWhiteSpace(p)
                ? p.Trim() : null;

            bool dryRun = true;
            if (values.TryGetValue("dispatcharr_dry_run", out var dr) && !string.IsNullOrWhiteSpace(dr))
                dryRun = ParseBool(dr);

            int threshold = 80;
            if (values.TryGetValue("dispatcharr_match_threshold", out var th)
                && int.TryParse(th, out var parsedTh)
                && parsedTh >= 0 && parsedTh <= 100)
                threshold = parsedTh;

            string? aliasFile = values.TryGetValue("dispatcharr_alias_file", out var af) && !string.IsNullOrWhiteSpace(af)
                ? af.Trim() : null;

            IReadOnlyList<string> providers = Array.Empty<string>();
            if (values.TryGetValue("dispatcharr_provider_priority", out var pp)
                && !string.IsNullOrWhiteSpace(pp))
            {
                providers = pp.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            bool autoGroups = true;
            if (values.TryGetValue("dispatcharr_auto_create_groups", out var ag) && !string.IsNullOrWhiteSpace(ag))
                autoGroups = ParseBool(ag);

            string? targetGroup = values.TryGetValue("dispatcharr_target_group_name", out var tg) && !string.IsNullOrWhiteSpace(tg)
                ? tg.Trim() : null;

            return new DispatcharrConfig
            {
                Enabled = true,
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                Username = username,
                Password = password,
                DryRun = dryRun,
                MatchThreshold = threshold,
                AliasFile = aliasFile,
                ProviderPriority = providers,
                AutoCreateGroups = autoGroups,
                TargetGroupName = targetGroup,
            };
        }

        private static bool ParseBool(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim();
            return raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("1", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class WtelegramConfigFile
    {
        public static IReadOnlyDictionary<string, string> Read()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "wtelegram.config"),
                Path.Combine(Directory.GetCurrentDirectory(), "wtelegram.config"),
            };
            string? path = null;
            foreach (var c in candidates)
            {
                if (File.Exists(c)) { path = c; break; }
            }
            if (path == null) return dict;

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                int idx = trimmed.IndexOf('=');
                if (idx <= 0) continue;
                string key = trimmed[..idx].Trim();
                string value = trimmed[(idx + 1)..].Trim();
                dict[key] = value;
            }
            return dict;
        }
    }
}

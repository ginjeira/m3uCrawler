using System.Globalization;
using System.Reflection;

namespace m3uCrawler.Build
{
    /// <summary>
    /// Identidade imutável e derivada de build desta aplicação.
    ///
    /// Os valores por defeito são embutidos em compile-time via reflexão
    /// sobre assembly attributes geradas pelo sistema de build
    /// (<c>Directory.Build.targets</c> + <c>Directory.Build.props</c>).
    /// Em runtime, <see cref="OverrideForTesting"/> permite substituir
    /// estes valores em testes, sem alterar o estado da aplicação.
    ///
    /// Esta classe é o ponto de exposição único para a versão da
    /// aplicação, evitando fontes manuais duplicadas em runtime.
    /// </summary>
    public sealed class BuildInfo
    {
        public const string Application = "m3uCrawler";

        internal const string FallbackVersion = "0.0.0-dev";
        internal const string FallbackCommit = "unknown";
        internal const int FallbackBuildNumber = 0;
        internal static readonly DateTimeOffset FallbackBuildDate =
            new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static readonly Lazy<BuildInfo> _initial = new(LoadFromAssembly);

        private static BuildInfo? _override;

        public string Version { get; }
        public string Commit { get; }
        public int BuildNumber { get; }
        public DateTimeOffset BuildDate { get; }

        public static BuildInfo Current => _override ?? _initial.Value;

        public BuildInfo(string version, string commit, int buildNumber, DateTimeOffset buildDate)
        {
            Version = string.IsNullOrWhiteSpace(version) ? FallbackVersion : version;
            Commit = string.IsNullOrWhiteSpace(commit) ? FallbackCommit : commit;
            BuildNumber = buildNumber < 0 ? 0 : buildNumber;
            BuildDate = buildDate == default ? FallbackBuildDate : buildDate;
        }

        /// <summary>
        /// Substitui os valores correntes (apenas testes, não produção).
        /// </summary>
        internal static void OverrideForTesting(string version, string commit, int buildNumber, DateTimeOffset buildDate)
        {
            _override = new BuildInfo(version, commit, buildNumber, buildDate);
        }

        /// <summary>
        /// Repõe os valores derivados do assembly. Apenas testes.
        /// </summary>
        internal static void ResetForTesting()
        {
            _override = null;
        }

        public string ToCliLine()
        {
            return $"{Application} {Version} ({Commit}, build {BuildNumber}, {BuildDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)})";
        }

        internal static BuildInfo LoadFromAssembly()
        {
            try
            {
                var asm = typeof(BuildInfo).Assembly;

                string version = FallbackVersion;
                string commit = FallbackCommit;
                int buildNumber = FallbackBuildNumber;
                DateTimeOffset buildDate = FallbackBuildDate;

                var inf = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(inf))
                {
                    var semantic = ParseInformationalVersion(inf, out var sha, out var build, out var date);
                    if (!string.IsNullOrWhiteSpace(semantic)) version = semantic;
                    if (!string.IsNullOrWhiteSpace(sha)) commit = sha;
                    if (build.HasValue) buildNumber = build.Value;
                    if (date.HasValue) buildDate = date.Value;
                }

                return new BuildInfo(version, commit, buildNumber, buildDate);
            }
            catch
            {
                return new BuildInfo(FallbackVersion, FallbackCommit, FallbackBuildNumber, FallbackBuildDate);
            }
        }

        /// <summary>
        /// Faz o parsing do formato "<semver>+sha.&lt;commit&gt;+build.&lt;N&gt;+date.&lt;ISO-8601&gt;".
        /// Tolerante a campos em falta.
        /// </summary>
        internal static string ParseInformationalVersion(
            string informacional,
            out string sha,
            out int? build,
            out DateTimeOffset? date)
        {
            sha = string.Empty;
            build = null;
            date = null;
            if (string.IsNullOrWhiteSpace(informacional)) return string.Empty;

            var semver = informacional;
            var plusIdx = informacional.IndexOf('+');
            if (plusIdx >= 0)
            {
                semver = informacional[..plusIdx];
                var meta = informacional[(plusIdx + 1)..];
                foreach (var part in meta.Split('+'))
                {
                    var eqIdx = part.IndexOf('.');
                    if (eqIdx <= 0) continue;
                    var key = part[..eqIdx];
                    var val = part[(eqIdx + 1)..];
                    if (key.Equals("sha", StringComparison.OrdinalIgnoreCase))
                    {
                        sha = val;
                    }
                    else if (key.Equals("build", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                            build = b;
                    }
                    else if (key.Equals("date", StringComparison.OrdinalIgnoreCase))
                    {
                        if (DateTimeOffset.TryParse(
                            val,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var d))
                            date = d;
                    }
                }
            }

            return semver;
        }
    }
}

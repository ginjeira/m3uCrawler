namespace m3uCrawler.Models
{
    /// <summary>
    /// Representa uma conta Xtream descoberta a partir de uma publicacao HTML.
    /// Modelo intermediario: nunca e serializado, logado nem persistido. Existe
    /// apenas para transportar metadados entre o XtreamPublicationResolver e o
    /// ponto de promocao para CandidatePlaylist dentro do mesmo processo.
    /// A password e efemera e nunca participa na identidade logica da conta.
    /// </summary>
    internal sealed class XtreamAccountInfo
    {
        public string Host { get; init; } = string.Empty;
        public int Port { get; init; }
        public string Scheme { get; init; } = "http";
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;

        public string? M3uUrl { get; init; }
        public string? EpgUrl { get; init; }

        public DateTime? ExpiresAt { get; init; }
        public int? MaxConnections { get; init; }
        public int? ActiveConnections { get; init; }

        public int? ChannelCount { get; init; }
        public int? VodCount { get; init; }
        public int? SeriesCount { get; init; }

        public string? ServerIp { get; init; }
        public string? ServerName { get; init; }

        public string SourcePublicationUrl { get; init; } = string.Empty;
        public string SourceTelegramMessageId { get; init; } = string.Empty;
        public string SourceTelegramChannel { get; init; } = string.Empty;

        /// <summary>
        /// Identidade logica da conta. Endpoint normalizado + username.
        /// A password NAO participa da identidade (regra explicita do projeto).
        /// </summary>
        public string LogicalIdentity
        {
            get
            {
                var scheme = string.IsNullOrWhiteSpace(Scheme) ? "http" : Scheme.ToLowerInvariant();
                var host = (Host ?? string.Empty).ToLowerInvariant();
                var user = Username ?? string.Empty;
                return $"{scheme}://{host}:{Port}/{user}";
            }
        }

        /// <summary>
        /// ToString propositadamente nao expoe credenciais. Usado apenas para
        /// diagnosticos nao sensiveis; nunca em logs, reports ou persistencia.
        /// </summary>
        public override string ToString()
        {
            return $"XtreamAccount[endpoint={Scheme}://{Host}:{Port}, user={Username}]";
        }
    }
}

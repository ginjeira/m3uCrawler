using System.Text;
using System.Text.RegularExpressions;

namespace m3uCrawler.Services
{
    /// <summary>
    /// Sanitiza URLs que possam conter credenciais (Xtream Codes user/password/token),
    /// para que representações destinadas a logs, relatórios e UI nunca exponham segredos.
    /// A URL original permanece em memória apenas para a operação de download.
    /// </summary>
    public static class CredentialSanitizer
    {
        // user:password@ em userinfo.
        private static readonly Regex _userInfoRegex = new(
            @"^(https?://)([^:@/\s]+):([^@/\s]+)@",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Segmentos user/pass no path (Xtream: /live/USER/PASS/...).
        private static readonly Regex _pathCredsRegex = new(
            @"/(live|movie|series)/[^/\s]+/[^/\s]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Parâmetros username/password/token em query string.
        private static readonly Regex _queryCredsRegex = new(
            @"([?&])(username|password|token)=([^&\s]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string SanitizeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;

            var s = url;

            // user:password@ em userinfo para QUALQUER scheme (incl. schemes nao-http
            // como "xtream://" ou "rtsp://") — protege o caso onde o caller passa
            // uma string estilo URL com credenciais em qualquer formato.
            s = Regex.Replace(s, @"^([a-z][a-z0-9+.\-]*://)([^:@/\s]+):([^@/\s]+)@",
                "$1$2:***@", RegexOptions.IgnoreCase);

            // user/password@ em formato path-style ("scheme://user/pass@host...") — usado
            // em alguns formatos nao-RFC como "xtream://user/pass@host:port".
            s = Regex.Replace(s, @"^([a-z][a-z0-9+.\-]*://)([^:@/\s]+)/([^@/\s]+)@",
                "$1$2/***@", RegexOptions.IgnoreCase);

            s = _userInfoRegex.Replace(s, "$1$2:***@");
            s = _pathCredsRegex.Replace(s, "/$1/***/***");
            s = _queryCredsRegex.Replace(s, "$1$2=***");
            return s;
        }

        /// <summary>
        /// Sanitiza o conteúdo completo de uma playlist M3U: aplica <see cref="SanitizeUrl"/> a
        /// cada URL http(s) encontrada em linhas próprias, preservando o resto (cabeçalhos
        /// #EXTM3U/#EXTINF). Usado para pré-visualização de diagnóstico no dashboard, onde a
        /// playlist funcional NÃO deve ser exposta com credenciais.
        /// </summary>
        public static string SanitizeM3uContent(string? content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;

            var sb = new StringBuilder();
            foreach (var rawLine in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var trimmed = rawLine.TrimStart();
                if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(SanitizeUrl(rawLine.Trim()));
                }
                else
                {
                    sb.AppendLine(rawLine);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Sanitiza um texto arbitrario (e.g. caption de mensagem Telegram, mensagem de
        /// revisao) contra credenciais Xtream. Aplica <see cref="SanitizeUrl"/> a cada URL
        /// http(s) presente e preserva o resto do texto. Util para diagnostico do
        /// RunReport (mensagens rejeitadas, triage) sem nunca persistir credenciais.
        /// </summary>
        public static string SanitizeText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                if (i + 7 < text.Length &&
                    (text.Substring(i, 7).Equals("http://", StringComparison.OrdinalIgnoreCase) ||
                     text.Substring(i, 8).Equals("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    int start = i;
                    int end = i;
                    while (end < text.Length && !char.IsWhiteSpace(text[end]) &&
                           text[end] != '<' && text[end] != '>' &&
                           text[end] != '"' && text[end] != '\'' &&
                           text[end] != '(' && text[end] != ')')
                    {
                        end++;
                    }
                    var url = text.Substring(start, end - start);
                    sb.Append(SanitizeUrl(url));
                    i = end;
                }
                else
                {
                    sb.Append(text[i]);
                    i++;
                }
            }
            return sb.ToString();
        }
    }
}

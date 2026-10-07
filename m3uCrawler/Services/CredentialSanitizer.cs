using System.Collections.Generic;
using System.Text;
using System.Text.Json;
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

        // W9.A — Forma "bare" do Xtream: scheme://host:port/<username>/<password>/<stream-id>,
        // sem o marcador /live|movie|series/. Reconhecimento deliberadamente restritivo:
        // exactamente 3 segmentos de path, os dois primeiros com >= 4 caracteres (credenciais)
        // e o ultimo um stream id numerico (extensao opcional). URLs arbitrarias com 3 segmentos
        // cujo ultimo nao e numerico (ex.: /path/to/playlist.m3u8) NAO sao afetadas.
        private static readonly Regex _bareXtreamPathRegex = new(
            @"^(https?://[^/\s?#]+)/([^/\s?#]{4,})/([^/\s?#]{4,})/([0-9]+(?:\.[A-Za-z0-9]+)?)(/?)(?=[?#]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Parâmetros username/password/token em query string.
        private static readonly Regex _queryCredsRegex = new(
            @"([?&])(username|password|token)=([^&\s]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // W6a — Nomes de propriedade JSON considerados sensíveis (comparação
        // normalizada, sem separadores e case-insensitive). O valor é sempre
        // substituído por "***" antes de um registo de auditoria ser persistido.
        private static readonly HashSet<string> _sensitiveJsonKeys = new(StringComparer.Ordinal)
        {
            "password", "passwd", "pwd", "newpassword", "currentpassword",
            "apihash", "apikey", "token", "accesstoken", "refreshtoken",
            "csrf", "csrftoken", "secret", "clientsecret", "sessionid",
            "session", "authorization", "privatekey", "code", "authcode",
            "logincode", "codehash", "cookie",
        };

        // W6a — Redacção de pares chave=valor sensíveis em texto livre.
        private static readonly Regex _sensitiveKeyValueRegex = new(
            @"\b(password|passwd|pwd|api[_-]?hash|api[_-]?key|token|secret|session[_-]?id|csrf[_-]?token|authorization|auth[_-]?code|login[_-]?code|code)\b\s*[:=]\s*[^\s,;&""'}]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // W2 — Redacção de credenciais Bearer (Authorization: Bearer <token>),
        // que o regex de pares chave=valor não cobre porque o valor não é um
        // par único. Nunca persistir/logar o token.
        private static readonly Regex _bearerTokenRegex = new(
            @"\b(Bearer)\s+[A-Za-z0-9\-\._~\+/=]+",
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
            s = _bareXtreamPathRegex.Replace(s, "$1/***/***/$4$5");
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

        /// <summary>
        /// W6a — Sanitiza texto livre de diagnóstico: aplica <see cref="SanitizeText"/>
        /// (URLs com credenciais) e redige pares <c>chave=valor</c> de campos
        /// sensíveis (password, api_hash, api_key, token, secret, code, …).
        /// Usado para o detalhe de registos de auditoria.
        /// </summary>
        public static string SanitizeSensitiveText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sanitized = SanitizeText(text);
            sanitized = _bearerTokenRegex.Replace(sanitized, "$1 ***");
            return _sensitiveKeyValueRegex.Replace(sanitized, match => match.Groups[1].Value + "=***");
        }

        /// <summary>
        /// W6a — Sanitiza um documento JSON recursivamente: propriedades sensíveis
        /// (password/api_key/token/…) passam a <c>"***"</c> e cada valor de string é
        /// sujeito a <see cref="SanitizeText"/> (URLs com credenciais). Se o texto não
        /// for JSON válido, cai para <see cref="SanitizeSensitiveText"/>. Nunca lança.
        /// </summary>
        public static string SanitizeJson(string? json)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;
            try
            {
                using var document = JsonDocument.Parse(json);
                var sb = new StringBuilder(json.Length);
                WriteSanitizedElement(document.RootElement, sb);
                return sb.ToString();
            }
            catch (JsonException)
            {
                return SanitizeSensitiveText(json);
            }
        }

        private static void WriteSanitizedElement(JsonElement element, StringBuilder sb)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.Append('{');
                    var firstProperty = true;
                    foreach (var property in element.EnumerateObject())
                    {
                        if (!firstProperty) sb.Append(',');
                        firstProperty = false;
                        sb.Append(JsonSerializer.Serialize(property.Name));
                        sb.Append(':');
                        if (IsSensitiveJsonKey(property.Name))
                        {
                            sb.Append("\"***\"");
                        }
                        else
                        {
                            WriteSanitizedElement(property.Value, sb);
                        }
                    }
                    sb.Append('}');
                    break;

                case JsonValueKind.Array:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        WriteSanitizedElement(item, sb);
                    }
                    sb.Append(']');
                    break;

                case JsonValueKind.String:
                    sb.Append(JsonSerializer.Serialize(SanitizeSensitiveText(element.GetString())));
                    break;

                case JsonValueKind.Number:
                    sb.Append(element.GetRawText());
                    break;

                case JsonValueKind.True:
                    sb.Append("true");
                    break;

                case JsonValueKind.False:
                    sb.Append("false");
                    break;

                default:
                    sb.Append("null");
                    break;
            }
        }

        private static bool IsSensitiveJsonKey(string name)
        {
            var normalized = name.Replace("_", string.Empty).Replace("-", string.Empty);
            return _sensitiveJsonKeys.Contains(normalized.ToLowerInvariant());
        }
    }
}

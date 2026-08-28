using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using m3uCrawler.Models;
using TL;

namespace m3uCrawler.Services
{
    public class TelegramScraperService
    {
        private readonly WTelegram.Client _client;

        // Mesma regex usada no M3uCrawlerService, para manter consistência
        // na deteção de URLs M3U8 em todo o projeto.
        private static readonly Regex _m3u8Regex = new(
            @"https?://[^\s<>""']+\.m3u8(?:\?[^\s<>""']*)?",
            RegexOptions.IgnoreCase);

        private static readonly Regex _m3uFilenameRegex = new(
            @"\.m3u8?$",
            RegexOptions.IgnoreCase);

        public TelegramScraperService()
        {
            _client = new WTelegram.Client(Config);
        }

        private static readonly Dictionary<string, string> _fileConfig = LoadConfigFile();

        private static Dictionary<string, string> LoadConfigFile()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Procura o ficheiro junto ao executável e, em alternativa, na
            // pasta atual de trabalho (útil ao correr via "dotnet run").
            string[] candidatePaths =
            {
                Path.Combine(AppContext.BaseDirectory, "wtelegram.config"),
                Path.Combine(Directory.GetCurrentDirectory(), "wtelegram.config")
            };

            string? path = candidatePaths.FirstOrDefault(File.Exists);
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

        private static string? Config(string what)
        {
            // "ask" no ficheiro significa pedir interativamente na consola
            if (_fileConfig.TryGetValue(what, out var value))
            {
                if (value.Equals("ask", StringComparison.OrdinalIgnoreCase))
                    return AskConsole($"{what}: ");
                return value;
            }

            return what switch
            {
                "verification_code" => AskConsole("Código de verificação: "),
                "password" => AskConsole("Password 2FA (se aplicável): "),
                _ => null
            };
        }

        private static string AskConsole(string prompt)
        {
            Console.Write(prompt);
            return Console.ReadLine() ?? "";
        }

        // Chamado pelo Program.cs para autenticar antes de pesquisar
        public async Task LoginAsync()
        {
            const int maxAttempts = 3;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var me = await _client.LoginUserIfNeeded();
                    Console.WriteLine($"Autenticado como: {(me?.username ?? me?.first_name ?? "(sem nome)")}");
                    return;
                }
                catch (RpcException ex) when (ex.Code == 420 && ex.Message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase))
                {
                    int waitSeconds = ExtractFloodWaitSeconds(ex.Message);
                    Console.WriteLine($"Flood control no login: a aguardar {waitSeconds}s antes de tentar novamente ({attempt}/{maxAttempts})...");
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds + 1));
                }
            }

            throw new Exception("Falha de autenticação no Telegram devido a FLOOD_WAIT após múltiplas tentativas. Aguarde alguns minutos e tente novamente.");
        }

        private static string NormalizeUnicode(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            var normalized = input.Normalize(NormalizationForm.FormKD);
            var sb = new StringBuilder();

            foreach (var c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }

            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }

        // Resultado interno: cada URL encontrada junto com algum contexto
        // (chat de origem e texto/legenda) para usar como título da stream.
        private record FoundUrl(string Url, string ChatTitle, string SourceText, string OriginalExtInf = "");

        public async Task<List<string>> SearchM3UInTelegram(string keyword, int limit = 200, int historyHours = 48)
        {
            var (textResults, _) = await SearchM3UInTelegramInternal(keyword, limit, historyHours);
            return textResults;
        }

        // Extrai credenciais Xtream Codes embutidas em URLs do tipo
        // http://host:port/live/USERNAME/PASSWORD/ID.ext
        private static readonly Regex _xtreamCredsRegex = new(
            @"https?://[^/]+/(?:live|movie|series)/([^/]+)/([^/]+)/",
            RegexOptions.IgnoreCase);

        // Pesquisa no Telegram, extrai URLs M3U8 dos resultados, testa cada
        // uma com o M3uTesterService (reaproveitando a lógica já existente
        // no resto da aplicação) e devolve só as streams que funcionam.
        public async Task<List<M3uStream>> SearchAndTestM3UInTelegram(
            string keyword, int limit = 200, int maxConcurrency = 5, int maxUrlsToTest = 500, int historyHours = 48)
        {
            var (textResults, foundUrls) = await SearchM3UInTelegramInternal(keyword, limit, historyHours);

            if (maxUrlsToTest > 0 && foundUrls.Count > maxUrlsToTest)
            {
                Console.WriteLine($"A limitar teste para {maxUrlsToTest} URL(s) para evitar execuções muito longas.");
                foundUrls = foundUrls.Take(maxUrlsToTest).ToList();
            }

            Console.WriteLine($"Pesquisa Telegram: {textResults.Count} mensagem(ns) com correspondencia para '{keyword}'.");
            Console.WriteLine($"Pesquisa Telegram: {foundUrls.Count} URL(s) de stream unica(s) extraida(s).");

            // Detetar credenciais Xtream Codes embutidas nas URLs encontradas
            var xtreamServers = new Dictionary<string, (string user, string pass)>(StringComparer.OrdinalIgnoreCase);
            foreach (var found in foundUrls)
            {
                var m = _xtreamCredsRegex.Match(found.Url);
                if (!m.Success) continue;

                if (!Uri.TryCreate(found.Url, UriKind.Absolute, out var uri)) continue;
                var origin = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
                if (!xtreamServers.ContainsKey(origin))
                {
                    xtreamServers[origin] = (m.Groups[1].Value, m.Groups[2].Value);
                }
            }

            if (xtreamServers.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("🔑 Credenciais Xtream Codes detetadas automaticamente:");
                foreach (var kv in xtreamServers)
                {
                    var (user, pass) = kv.Value;
                    Console.WriteLine($"  Servidor : {kv.Key}");
                    Console.WriteLine($"  Utilizador: {user}");
                    Console.WriteLine($"  Password  : {pass}");
                    Console.WriteLine($"  Playlist  : {kv.Key}/get.php?username={user}&password={pass}&type=m3u_plus");
                    Console.WriteLine();
                }
            }

            if (foundUrls.Count == 0)
            {
                Console.WriteLine("Nenhuma URL M3U8 encontrada nas mensagens do Telegram.");
                return new List<M3uStream>();
            }

            Console.WriteLine($"A testar {foundUrls.Count} URL(s) encontradas no Telegram...");

            foreach (var preview in foundUrls.Take(5))
            {
                Console.WriteLine($"  - [{preview.ChatTitle}] {preview.Url}");
            }
            if (foundUrls.Count > 5)
            {
                Console.WriteLine($"  ... e mais {foundUrls.Count - 5} URL(s)");
            }

            var tester = new M3uTesterService();
            try
            {
                // Testa cada URL individualmente (em vez de usar TestMultipleStreams
                // diretamente) para podermos atribuir o título/grupo corretos a
                // partir do contexto da mensagem onde a URL foi encontrada.
                var semaphore = new SemaphoreSlim(maxConcurrency);
                var testTasks = foundUrls.Select(async found =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string title = !string.IsNullOrWhiteSpace(found.SourceText)
                            ? found.SourceText
                            : found.ChatTitle;
                        var tested = await tester.TestM3u8Stream(found.Url, title, found.ChatTitle);

                        if (!string.IsNullOrWhiteSpace(found.OriginalExtInf))
                        {
                            tested.OriginalExtInf = found.OriginalExtInf;
                        }

                        return tested;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var allTested = await Task.WhenAll(testTasks);
                var working = allTested.Where(s => s.IsWorking).ToList();
                var failing = allTested.Where(s => !s.IsWorking).ToList();

                Console.WriteLine($"Resultado: {working.Count}/{allTested.Length} stream(s) funcionais.");
                Console.WriteLine($"Resumo de testes: {failing.Count} com falha/timeout.");

                if (failing.Count > 0)
                {
                    foreach (var failed in failing.Take(3))
                    {
                        Console.WriteLine($"  x Falhou: {failed.Url}");
                    }
                }

                return working;
            }
            finally
            {
                tester.Dispose();
            }
        }

        private async Task<(List<string> textResults, List<FoundUrl> foundUrls)> SearchM3UInTelegramInternal(
            string keyword, int limit = 200, int historyHours = 48)
        {
            var results = new List<string>();
            var foundUrls = new List<FoundUrl>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            keyword = NormalizeUnicode(keyword);

            // Idempotente: se já autenticado, não faz nada
            var me = await _client.LoginUserIfNeeded();
            Console.WriteLine($"Autenticado como: {(me?.username ?? me?.first_name ?? "(sem nome)")}");

            // dialogsBase é do tipo base Messages_DialogsBase em todas as versões.
            // Para obter os arrays "dialogs" e "chats" de forma garantida em
            // qualquer versão da lib, fazemos pattern matching para os tipos
            // concretos (Messages_Dialogs / Messages_DialogsSlice), que são
            // os únicos que a API do Telegram efetivamente devolve.
            var dialogsBase = await _client.Messages_GetAllDialogs();

            Dialog[] dialogList;
            Dictionary<long, ChatBase> chatsDict;
            Dictionary<long, User> usersDict;

            switch (dialogsBase)
            {
                case Messages_Dialogs md:
                    dialogList = md.dialogs.OfType<Dialog>().ToArray();
                    chatsDict = md.chats;
                    usersDict = md.users;
                    break;
                default:
                    Console.WriteLine($"Nenhum diálogo encontrado (tipo de resposta: {dialogsBase?.GetType().Name}).");
                    return (results, foundUrls);
            }

            foreach (var dialog in dialogList)
            {
                var peer = dialog.Peer;

                // Resolve o Peer (que é só um ID) para o objeto concreto
                // User ou ChatBase, que pode ser passado diretamente onde
                // a lib espera um InputPeer (conversão implícita).
                object? resolvedPeer = peer switch
                {
                    PeerUser pu when usersDict.TryGetValue(pu.user_id, out var u) => u,
                    PeerChat pc when chatsDict.TryGetValue(pc.chat_id, out var c) => c,
                    PeerChannel pch when chatsDict.TryGetValue(pch.channel_id, out var c) => c,
                    _ => null
                };

                if (resolvedPeer == null) continue;

                string chatTitle = resolvedPeer switch
                {
                    ChatBase chat => chat.Title,
                    User user => user.username ?? user.first_name ?? "Utilizador",
                    _ => "Chat"
                };

                int offsetId = 0;
                var cutoffDate = DateTime.UtcNow.AddHours(-historyHours);
                bool reachedCutoff = false;

                while (!reachedCutoff)
                {
                    Messages_MessagesBase? history = null;

                    // Retry manual em caso de FLOOD_WAIT: o Telegram devolve
                    // quantos segundos é preciso esperar antes de repetir.
                    while (history == null)
                    {
                        try
                        {
                            history = resolvedPeer switch
                            {
                                User user => await _client.Messages_GetHistory(
                                    user, offset_id: offsetId, offset_date: default,
                                    add_offset: 0, limit: 100, max_id: 0, min_id: 0),
                                ChatBase chat => await _client.Messages_GetHistory(
                                    chat, offset_id: offsetId, offset_date: default,
                                    add_offset: 0, limit: 100, max_id: 0, min_id: 0),
                                _ => null
                            };
                        }
                        catch (WTelegram.WTException ex) when (ex.Message.Contains("FLOOD_WAIT"))
                        {
                            int waitSeconds = ExtractFloodWaitSeconds(ex.Message);
                            Console.WriteLine($"Flood control: a aguardar {waitSeconds}s antes de continuar...");
                            await Task.Delay(TimeSpan.FromSeconds(waitSeconds + 1));
                        }
                    }

                    if (history?.Messages == null || history.Messages.Length == 0)
                        break;

                    foreach (var msgBase in history.Messages)
                    {
                        if (msgBase is not Message m) continue;

                        // m.date é convertido pela lib para DateTime (UTC). O
                        // histórico vem por ordem decrescente (mais recente
                        // primeiro), por isso assim que encontramos uma
                        // mensagem fora da janela das 48h, paramos: não há
                        // mensagens mais recentes a seguir a esta.
                        if (m.date < cutoffDate)
                        {
                            reachedCutoff = true;
                            break;
                        }

                        // Em TL atual a legenda de um media é o próprio
                        // texto da mensagem (m.message); não existe um
                        // campo "caption" separado dentro de MessageMediaDocument.
                        string text = m.message ?? "";
                        string filename = "";

                        if (m.media is MessageMediaDocument mediaDoc &&
                            mediaDoc.document is Document doc)
                        {
                            foreach (var attr in doc.attributes)
                            {
                                if (attr is DocumentAttributeFilename fn)
                                    filename = fn.file_name ?? "";
                            }
                        }

                        string normText = NormalizeUnicode(text);
                        string normFilename = NormalizeUnicode(filename);

                        if (normText.Contains(keyword) || normFilename.Contains(keyword))
                        {
                            results.Add($"{chatTitle} :: {(filename != "" ? filename : text)}");

                            // Extrai quaisquer URLs M3U8 presentes no texto da
                            // mensagem (a legenda de um media é o próprio
                            // m.message, por isso isto cobre ambos os casos).
                            foreach (Match urlMatch in _m3u8Regex.Matches(text))
                            {
                                if (seenUrls.Add(urlMatch.Value))
                                {
                                    foundUrls.Add(new FoundUrl(urlMatch.Value, chatTitle, text));
                                }
                            }

                            // Se a mensagem tiver um anexo .m3u/.m3u8, descarrega e
                            // extrai URLs M3U8 diretamente do conteúdo do ficheiro.
                            if (m.media is MessageMediaDocument media &&
                                media.document is Document attachmentDocument &&
                                !string.IsNullOrWhiteSpace(filename) &&
                                _m3uFilenameRegex.IsMatch(filename))
                            {
                                try
                                {
                                    await ExtractM3u8FromTelegramDocumentAsync(
                                        attachmentDocument,
                                        filename,
                                        chatTitle,
                                        text,
                                        seenUrls,
                                        foundUrls);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Falha ao processar anexo '{filename}': {ex.Message}");
                                }
                            }
                        }
                    }

                    offsetId = history.Messages.Last().ID;

                    if (reachedCutoff || history.Messages.Length < 100) break;

                    // Pequena pausa entre pedidos para não disparar flood control
                    await Task.Delay(300);
                }

                // Pausa maior entre chats diferentes
                await Task.Delay(500);
            }

            return (results, foundUrls);
        }

        private async Task<int> ExtractM3u8FromTelegramDocumentAsync(
            Document document,
            string fileName,
            string chatTitle,
            string sourceText,
            HashSet<string> seenUrls,
            List<FoundUrl> foundUrls)
        {
            using var ms = new MemoryStream();
            await _client.DownloadFileAsync(document, ms);

            ms.Position = 0;
            using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync();

            int added = 0;

            string pendingExtInf = "";

            // Extrai pares EXTINF + URL para preservar metadados originais.
            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    pendingExtInf = trimmed;
                    continue;
                }

                if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (seenUrls.Add(trimmed))
                    {
                        foundUrls.Add(new FoundUrl(trimmed, chatTitle, sourceText, pendingExtInf));
                        added++;
                    }

                    pendingExtInf = "";
                }
            }

            // Fallback para URLs inline que não estejam em linhas próprias.
            foreach (Match urlMatch in _m3u8Regex.Matches(content))
            {
                if (seenUrls.Add(urlMatch.Value))
                {
                    foundUrls.Add(new FoundUrl(urlMatch.Value, chatTitle, sourceText));
                    added++;
                }
            }

            if (added > 0)
            {
                Console.WriteLine($"Extraidas {added} URL(s) de anexo: {fileName}");
            }
            else
            {
                Console.WriteLine($"Nenhuma URL encontrada no anexo: {fileName} ({content.Length} chars)");
            }

            return added;
        }

        private static int ExtractFloodWaitSeconds(string message)
        {
            // Mensagens do tipo "FLOOD_WAIT_26" ou "A wait of 26 seconds is required"
            var digits = new string(message.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var seconds) && seconds > 0)
            {
                return seconds;
            }

            // Algumas exceções chegam mascaradas como FLOOD_WAIT_X sem número.
            return message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase) ? 180 : 30;
        }
    }
}
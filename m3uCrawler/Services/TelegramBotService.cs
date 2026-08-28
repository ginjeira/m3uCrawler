using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using m3uCrawler.Services;

namespace m3uCrawler.Services;

public class TelegramBotService
{
    private readonly TelegramBotClient _bot;
    private readonly M3uCrawlerService _crawler;

    public TelegramBotService(string token, M3uCrawlerService crawler)
    {
        _bot = new TelegramBotClient(token);
        _crawler = crawler;
    }

    public void Start()
    {
        var cts = new CancellationTokenSource();

        _bot.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
            cancellationToken: cts.Token
        );

        Console.WriteLine("🤖 Telegram bot iniciado");
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text })
            return;

        long chatId = update.Message.Chat.Id;

        await bot.SendMessage(
            chatId,
            $"🔍 A pesquisar: {text}",
            cancellationToken: ct
        );

        // 1. Pesquisar URLs
        var urls = await _crawler.SearchM3u8Files(text, 200);

        await bot.SendMessage(
            chatId,
            $"📋 Encontradas {urls.Count} URLs M3U8\n🧪 A testar streams...",
            cancellationToken: ct
        );

        // 2. Testar streams
        var tester = new M3uTesterService();
        var tested = await tester.TestMultipleStreams(urls, 10);

        var working = tested.Where(s => s.IsWorking).ToList();

        await bot.SendMessage(
            chatId,
            $"✅ Funcionais: {working.Count}/{tested.Count}",
            cancellationToken: ct
        );

        // 3. Gerar playlist
        var playlistManager = new PlaylistManagerService();
        var outputDir = "output";
        playlistManager.CreateOutputDirectory(outputDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var playlistPath = Path.Combine(outputDir, $"playlist_{timestamp}.m3u");

        await playlistManager.SaveToM3uPlaylist(tested, playlistPath);

        // 4. Enviar ficheiro
        await using var fs = File.OpenRead(playlistPath);

        await bot.SendDocument(
            chatId,
            InputFile.FromStream(fs, Path.GetFileName(playlistPath)),
            caption: "📺 Playlist gerada",
            cancellationToken: ct
        );
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        Console.WriteLine($"Erro Telegram: {ex.Message}");
        return Task.CompletedTask;
    }
}

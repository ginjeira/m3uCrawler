using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Regressao contra BUG-SEC-002: o token Telegram Bot nao pode estar
/// hardcoded no codigo. O servico tem de aceitar o token externamente via
/// construtor e o Program.cs tem de o ler de configuracao (CLI / env var).
/// </summary>
public class TelegramBotServiceConfigurationTests
{
    [Fact]
    public void Constructor_accepts_token_via_parameter()
    {
        // Token placeholder intencional (formato Telegram.Bot valido). Apenas
        // verifica que o construtor nao falha por formato invalido e que a
        // injecao de dependencia funciona.
        var placeholderToken = "0000000000:PLACEHOLDER_TOKEN_FOR_TESTING_ONLY__";
        var crawler = new M3uCrawlerService();

        var bot = new TelegramBotService(placeholderToken, crawler);

        Assert.NotNull(bot);
    }
}

using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class WebDashboardServiceTests
{
    // ---- IsAuthorized (lógica pura) ----

    [Fact]
    public void IsAuthorized_no_token_configured_allows_everything()
    {
        // Sem token configurado: comportamento aberto (compatibilidade).
        Assert.True(WebDashboardService.IsAuthorized(null, null, null));
        Assert.True(WebDashboardService.IsAuthorized("", "", ""));
        Assert.True(WebDashboardService.IsAuthorized("Bearer anything", "anything", ""));
    }

    [Fact]
    public void IsAuthorized_with_token_rejects_missing_credentials()
    {
        Assert.False(WebDashboardService.IsAuthorized(null, null, "secret123"));
        Assert.False(WebDashboardService.IsAuthorized("", "", "secret123"));
    }

    [Fact]
    public void IsAuthorized_accepts_valid_bearer_header()
    {
        Assert.True(WebDashboardService.IsAuthorized("Bearer secret123", null, "secret123"));
        Assert.True(WebDashboardService.IsAuthorized("bearer secret123", null, "secret123")); // case-insensitive prefix
        Assert.True(WebDashboardService.IsAuthorized("Bearer  secret123 ", null, "secret123")); // trim
    }

    [Fact]
    public void IsAuthorized_accepts_valid_query_token()
    {
        Assert.True(WebDashboardService.IsAuthorized(null, "secret123", "secret123"));
        Assert.True(WebDashboardService.IsAuthorized(null, "  secret123  ", "secret123")); // trim
    }

    [Fact]
    public void IsAuthorization_accepts_either_header_or_query()
    {
        // Header tem prioridade, mas se query também servir deve passar.
        Assert.True(WebDashboardService.IsAuthorized("Bearer secret123", "secret123", "secret123"));
    }

    [Fact]
    public void IsAuthorized_rejects_wrong_token()
    {
        Assert.False(WebDashboardService.IsAuthorized("Bearer wrong", null, "secret123"));
        Assert.False(WebDashboardService.IsAuthorized(null, "wrong", "secret123"));
        Assert.False(WebDashboardService.IsAuthorized("Bearer ", null, "secret123")); // empty value
        Assert.False(WebDashboardService.IsAuthorized("Basic secret123", null, "secret123")); // wrong scheme
    }

    [Fact]
    public void IsAuthorized_rejects_partial_match()
    {
        // Ataque por prefixo/suffixo não deve passar (comparação exacta).
        Assert.False(WebDashboardService.IsAuthorized("Bearer secret1234", null, "secret123"));
        Assert.False(WebDashboardService.IsAuthorized("Bearer xsecret123", null, "secret123"));
        Assert.False(WebDashboardService.IsAuthorized("Bearer SECRET123", null, "secret123")); // case-sensitive value
    }

    [Fact]
    public void IsAuthorized_rejects_when_xtream_credential_in_header_or_query()
    {
        // Garante que uma password Xtream (se por algum motivo vier num header/query errado)
        // não é aceite como token do dashboard.
        var xtreamPassword = "alice:secret@host/live/alice/secret/1.ts";
        Assert.False(WebDashboardService.IsAuthorized($"Bearer {xtreamPassword}", null, "dashboardToken"));
        Assert.False(WebDashboardService.IsAuthorized(null, xtreamPassword, "dashboardToken"));
    }
}

using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Cobre a expansao do RunReport com contadores de triagem por publicacao
/// (Telegram references + URLs HTTP publicas), contas Xtream descobertas
/// e fan-out.
/// </summary>
public class RunReportPublicationCountersTests
{
    [Fact]
    public void Default_values_are_zero()
    {
        var rep = new RunReport();
        Assert.Equal(0, rep.PublicationsDiscovered);
        Assert.Equal(0, rep.PublicationsResolved);
        Assert.Equal(0, rep.PublicationsResolutionFailed);
        Assert.Equal(0, rep.PublicationsUnsupported);
        Assert.Equal(0, rep.PublicationsRequiresReview);
        Assert.Equal(0, rep.XtreamAccountsDiscovered);
        Assert.Equal(0, rep.XtreamAccountsAfterDedup);
        Assert.Equal(0, rep.XtreamAccountsForwarded);
    }

    [Fact]
    public void Triage_log_records_outcomes_sanitized()
    {
        // Nenhum dos "reason" deve conter password/token/credenciais em claro.
        // Este teste sera' complementado por testes de integracao. Aqui apenas
        // verificamos que a lista e' publicamente mutavel.
        var rep = new RunReport();
        rep.PublicationsTriageLog.Add(new PublicationTriageEntry
        {
            Kind = "telegram message link",
            Reference = "https://t.me/c/100/1",
            ChannelId = 100,
            MessageId = 1,
            State = PublicationState.Resolved,
            XtreamAccountsFound = 3
        });
        Assert.Single(rep.PublicationsTriageLog);
        var entry = rep.PublicationsTriageLog[0];
        Assert.Equal(PublicationState.Resolved, entry.State);
        Assert.Equal(3, entry.XtreamAccountsFound);
    }

    [Fact]
    public void Triage_entry_to_string_does_not_include_sensitive_payload()
    {
        var entry = new PublicationTriageEntry
        {
            Kind = "xtream account",
            Reference = "xtream://user/secretPwd99@host.example.com:80",
            ChannelId = null,
            MessageId = null,
            State = PublicationState.Resolved,
            Reason = "host=user endpoint=host.example.com",
            XtreamAccountsFound = 1
        };
        var s = entry.ToString();
        Assert.DoesNotContain("secretPwd99", s);
        Assert.DoesNotContain("password", s, StringComparison.OrdinalIgnoreCase);
    }
}

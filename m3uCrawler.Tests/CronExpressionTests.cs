using System;
using m3uCrawler.Services.Automation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 12 — Testes do parser <see cref="CronExpression"/>. Cobrem a
/// aceitação de 5 campos válidos, o cálculo de <c>NextOccurrence</c> para
/// wildcards/step/dia-da-semana e a rejeição de expressões inválidas
/// (campos a mais, valores fora de range, tokens não numéricos).
/// </summary>
public class CronExpressionTests
{
    [Fact]
    public void Parse_accepts_valid_five_field_expression()
    {
        var cron = CronExpression.Parse("0 8 * * *");
        Assert.Equal("0 8 * * *", cron.OriginalExpression);
    }

    [Fact]
    public void NextOccurrence_returns_known_value_for_daily_at_eight()
    {
        var cron = CronExpression.Parse("0 8 * * *");
        var from = new DateTime(2026, 1, 1, 7, 30, 0, DateTimeKind.Utc);
        var next = cron.NextOccurrence(from);
        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Parse_accepts_step_expression_and_computes_next()
    {
        var cron = CronExpression.Parse("0 */6 * * *");
        var from = new DateTime(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc), cron.NextOccurrence(from));
    }

    [Fact]
    public void Parse_accepts_day_of_week_expression()
    {
        var cron = CronExpression.Parse("30 2 * * 1");
        Assert.Equal("30 2 * * 1", cron.OriginalExpression);
    }

    [Fact]
    public void Parse_rejects_six_field_expression()
    {
        var ex = Assert.Throws<ArgumentException>(() => CronExpression.Parse("0 0 8 * * *"));
        Assert.Contains("5 campos", ex.Message);
    }

    [Fact]
    public void Parse_rejects_out_of_range_value()
    {
        Assert.Throws<ArgumentException>(() => CronExpression.Parse("60 0 * * *"));
    }

    [Fact]
    public void Parse_rejects_invalid_token()
    {
        Assert.Throws<ArgumentException>(() => CronExpression.Parse("abc 0 * * *"));
    }
}

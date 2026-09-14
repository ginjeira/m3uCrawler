using System;
using System.Collections.Generic;
using System.Globalization;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// PHASE 12 — Parser e calculadora de próximo tick para expressões
/// cron simplificadas (5 campos: minuto, hora, dia-do-mês, mês,
/// dia-da-semana). Suporta wildcards (<c>*</c>), listas
/// (<c>1,3,5</c>), ranges (<c>0-15</c>) e steps (<c>*/5</c>).
///
/// Limitações conhecidas:
/// <list type="bullet">
///   <item>Não suporta <c>L</c>, <c>W</c>, <c>#</c>.</item>
///   <item>Não suporta nomes (JAN, MON) — só números.</item>
///   <item>Timezone: UTC.</item>
/// </list>
/// </summary>
public sealed class CronExpression
{
    private readonly int[] _minutes;
    private readonly int[] _hours;
    private readonly int[] _daysOfMonth;
    private readonly int[] _months;
    private readonly int[] _daysOfWeek;

    public string OriginalExpression { get; }

    private CronExpression(string original, int[] minutes, int[] hours, int[] daysOfMonth, int[] months, int[] daysOfWeek)
    {
        OriginalExpression = original;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
    }

    public static CronExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("Expressão vazia.", nameof(expression));
        }
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            throw new ArgumentException($"Cron deve ter 5 campos, recebido {parts.Length}.", nameof(expression));
        }
        return new CronExpression(
            expression.Trim(),
            ParseField(parts[0], 0, 59),
            ParseField(parts[1], 0, 23),
            ParseField(parts[2], 1, 31),
            ParseField(parts[3], 1, 12),
            ParseField(parts[4], 0, 6));
    }

    /// <summary>
    /// Devolve o próximo instante em UTC em que a expressão
    /// corresponde. Pesquisa minuto-a-minuto até ao limite de
    /// <paramref name="maxIterations"/> iterações (defesa contra
    /// expressões impossíveis).
    /// </summary>
    public DateTime NextOccurrence(DateTime fromUtc, int maxIterations = 366 * 24 * 60)
    {
        var candidate = new DateTime(
            fromUtc.Year, fromUtc.Month, fromUtc.Day,
            fromUtc.Hour, fromUtc.Minute, 0, DateTimeKind.Utc)
            .AddMinutes(1);

        for (var i = 0; i < maxIterations; i++)
        {
            if (Matches(candidate))
            {
                return candidate;
            }
            candidate = candidate.AddMinutes(1);
        }
        throw new InvalidOperationException("Não foi possível calcular a próxima ocorrência dentro do limite.");
    }

    public bool Matches(DateTime t)
    {
        if (Array.IndexOf(_months, t.Month) < 0) return false;
        if (Array.IndexOf(_hours, t.Hour) < 0) return false;
        if (Array.IndexOf(_minutes, t.Minute) < 0) return false;
        var dom = Array.IndexOf(_daysOfMonth, t.Day) >= 0;
        var dow = Array.IndexOf(_daysOfWeek, (int)t.DayOfWeek) >= 0;
        // Cron semantics: ambos restritos → OU; caso contrário E.
        var domIsWild = IsAllRange(_daysOfMonth, 1, 31);
        var dowIsWild = IsAllRange(_daysOfWeek, 0, 6);
        if (domIsWild && dowIsWild) return true;
        if (domIsWild) return dow;
        if (dowIsWild) return dom;
        return dom || dow;
    }

    private static bool IsAllRange(int[] field, int min, int max)
    {
        if (field.Length != max - min + 1) return false;
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] != min + i) return false;
        }
        return true;
    }

    private static int[] ParseField(string token, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Campo vazio.");
        }
        token = token.Trim();
        if (token == "*" || token == "?")
        {
            return Enumerable.Range(min, max - min + 1).ToArray();
        }
        var bits = token.Split(',');
        var result = new SortedSet<int>();
        foreach (var bit in bits)
        {
            // Range puro: 0-15
            if (bit.Contains('-') && !bit.Contains('/'))
            {
                var dashIdx = bit.IndexOf('-');
                var lo = ParseScalar(bit[..dashIdx], min, max);
                var hi = ParseScalar(bit[(dashIdx + 1)..], min, max);
                if (hi < lo)
                {
                    throw new ArgumentException($"Range inválido: '{bit}'");
                }
                for (var v = lo; v <= hi; v++)
                {
                    result.Add(v);
                }
                continue;
            }
            var parts = bit.Split('/');
            int start, end, step;
            if (parts.Length == 1)
            {
                start = ParseScalar(parts[0], min, max);
                end = start;
                step = 1;
            }
            else if (parts.Length == 2)
            {
                var rangeToken = parts[0];
                if (rangeToken == "*") { start = min; end = max; }
                else if (rangeToken.Contains('-'))
                {
                    var dashIdx = rangeToken.IndexOf('-');
                    start = ParseScalar(rangeToken[..dashIdx], min, max);
                    end = ParseScalar(rangeToken[(dashIdx + 1)..], min, max);
                }
                else
                {
                    start = ParseScalar(rangeToken, min, max);
                    end = max;
                }
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out step) || step <= 0)
                {
                    throw new ArgumentException($"Step inválido: '{bit}'");
                }
            }
            else
            {
                throw new ArgumentException($"Token cron inválido: '{bit}'");
            }
            for (var v = start; v <= end; v += step)
            {
                result.Add(v);
            }
        }
        return result.ToArray();
    }

    private static int ParseScalar(string token, int min, int max)
    {
        if (token == "*") return min;
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            throw new ArgumentException($"Valor inválido: '{token}'");
        }
        if (n < min || n > max)
        {
            throw new ArgumentException($"Valor fora do range: '{token}' (esperado {min}-{max})");
        }
        return n;
    }
}

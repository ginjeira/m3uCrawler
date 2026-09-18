namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Wave C — Parâmetros operacionais de discovery (pesquisa Telegram).
///
/// <para>
/// Esta é a <b>fonte de verdade única</b> dos parâmetros de discovery:
/// janela de histórico, limite de streams testadas e termo de pesquisa.
/// É persistida em <c>runtime-data/app_settings.json</c> através do
/// <see cref="AppSettingsStore"/> existente (não existe um segundo
/// ficheiro de settings). CLI, dashboard manual, scheduler e modo
/// manutenção leem/gravam todos este mesmo objecto.
/// </para>
///
/// <para>
/// Precedência: overrides explícitos (CLI ou <c>POST /api/run/start</c>)
/// têm prioridade; na ausência deles usam-se os valores persistidos.
/// Ver <see cref="WithOverrides"/>.
/// </para>
/// </summary>
public sealed class DiscoverySettings
{
    /// <summary>Janela de histórico padrão, em horas.</summary>
    public const int DefaultHistoryHours = 24;

    /// <summary>Mínimo aceite para <see cref="HistoryHours"/>.</summary>
    public const int MinHistoryHours = 1;

    /// <summary>Máximo aceite para <see cref="HistoryHours"/> (30 dias).</summary>
    public const int MaxHistoryHours = 24 * 30;

    /// <summary>Limite de streams padrão por ciclo.</summary>
    public const int DefaultMaxStreams = 500;

    /// <summary>Mínimo aceite para <see cref="MaxStreams"/>.</summary>
    public const int MinMaxStreams = 1;

    /// <summary>
    /// Cap histórico aplicado apenas a overrides explícitos de CLI/API,
    /// para preservar o comportamento anterior às Waves C. Não limita o
    /// valor persistido directamente pelo operador via endpoint.
    /// </summary>
    public const int OverrideMaxStreamsCeiling = 5000;

    /// <summary>Termo de pesquisa padrão.</summary>
    public const string DefaultKeyword = "portugal";

    /// <summary>Janela de pesquisa Telegram, em horas. Default: 24.</summary>
    public int HistoryHours { get; set; } = DefaultHistoryHours;

    /// <summary>Limite de streams testadas por ciclo. Default: 500.</summary>
    public int MaxStreams { get; set; } = DefaultMaxStreams;

    /// <summary>Termo de pesquisa no Telegram. Default: <c>portugal</c>.</summary>
    public string Keyword { get; set; } = DefaultKeyword;

    public DiscoverySettings Clone() => new()
    {
        HistoryHours = HistoryHours,
        MaxStreams = MaxStreams,
        Keyword = Keyword,
    };

    /// <summary>
    /// Normaliza valores inválidos para os defaults. Nunca lança; é
    /// aplicada em cada leitura do store (recuperação de ficheiro
    /// corrompido/editado à mão).
    /// </summary>
    public void Sanitize()
    {
        if (HistoryHours < MinHistoryHours || HistoryHours > MaxHistoryHours)
        {
            HistoryHours = DefaultHistoryHours;
        }

        if (MaxStreams < MinMaxStreams)
        {
            MaxStreams = DefaultMaxStreams;
        }

        var keyword = Keyword?.Trim();
        Keyword = string.IsNullOrWhiteSpace(keyword) ? DefaultKeyword : keyword;
    }

    /// <summary>
    /// Valida os valores como contrato de entrada. Devolve <c>false</c>
    /// com uma mensagem segura (sem eco de conteúdo sensível) quando
    /// fora dos intervalos aceites.
    /// </summary>
    public bool TryValidate(out string? error)
    {
        if (HistoryHours < MinHistoryHours || HistoryHours > MaxHistoryHours)
        {
            error = $"historyHours deve estar entre {MinHistoryHours} e {MaxHistoryHours}.";
            return false;
        }

        if (MaxStreams < MinMaxStreams)
        {
            error = $"maxStreams deve ser >= {MinMaxStreams}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Keyword))
        {
            error = "keyword não pode estar vazio.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Resolve os valores efectivos de uma execução: overrides explícitos
    /// (CLI/API) substituem o valor persistido quando presentes e válidos;
    /// caso contrário o valor persistido é mantido.
    /// </summary>
    public DiscoverySettings WithOverrides(string? keyword, int? historyHours, int? maxStreams)
    {
        var effective = Clone();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            effective.Keyword = keyword.Trim();
        }

        if (historyHours is >= MinHistoryHours and <= MaxHistoryHours)
        {
            effective.HistoryHours = historyHours.Value;
        }

        if (maxStreams is >= MinMaxStreams)
        {
            effective.MaxStreams = maxStreams.Value;
        }

        return effective;
    }
}

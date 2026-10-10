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
    public const int MinValidHistoryHours = 1;

    /// <summary>
    /// Máximo aceite para <see cref="HistoryHours"/> (60 dias). É o
    /// limite superior (Max) da janela Min/Max e o tecto que suporta as
    /// faixas de teste até 1000h.
    /// </summary>
    public const int MaxValidHistoryHours = 24 * 60;

    /// <summary>
    /// Default do limite mínimo da janela; 0 equivale ao comportamento
    /// legacy (apenas o limite superior).
    /// </summary>
    public const int DefaultMinHistoryHours = 0;

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

    /// <summary>
    /// Default do fallback canónico na aquisição: aceitar um stream que
    /// resolve para um canal canónico existente mesmo sem token de país
    /// no título. Default: <c>true</c>.
    /// </summary>
    public const bool DefaultFeedCanonicalFallback = true;

    /// <summary>
    /// Limite superior (Max) da janela de pesquisa Telegram, em horas.
    /// Default: 24.
    /// </summary>
    public int HistoryHours { get; set; } = DefaultHistoryHours;

    /// <summary>
    /// Limite mínimo (em horas) da idade das mensagens a processar.
    /// Janela efectiva: <c>MinHistoryHours &lt;= idade &lt;= HistoryHours</c>.
    /// Default: 0 — comportamento legacy (apenas o limite superior).
    /// </summary>
    public int MinHistoryHours { get; set; } = DefaultMinHistoryHours;

    /// <summary>Limite de streams testadas por ciclo. Default: 500.</summary>
    public int MaxStreams { get; set; } = DefaultMaxStreams;

    /// <summary>Termo de pesquisa no Telegram. Default: <c>portugal</c>.</summary>
    public string Keyword { get; set; } = DefaultKeyword;

    /// <summary>
    /// Fallback canónico na aquisição (default <c>true</c>). Quando activo,
    /// dentro de uma playlist que passou o filtro de país, um stream que
    /// não apresenta token de país no título é ainda aceite se resolver
    /// para um canal canónico existente (<c>canonical_channels.Key</c> ou
    /// <c>channel_aliases.NormalizedAlias</c>). Nunca auto-cria canais e
    /// não sobrepõe a negative evidence de prefixo estrangeiro.
    /// </summary>
    public bool FeedCanonicalFallback { get; set; } = DefaultFeedCanonicalFallback;

    public DiscoverySettings Clone() => new()
    {
        HistoryHours = HistoryHours,
        MinHistoryHours = MinHistoryHours,
        MaxStreams = MaxStreams,
        Keyword = Keyword,
        FeedCanonicalFallback = FeedCanonicalFallback,
    };

    /// <summary>
    /// Normaliza valores inválidos para os defaults. Nunca lança; é
    /// aplicada em cada leitura do store (recuperação de ficheiro
    /// corrompido/editado à mão).
    /// </summary>
    public void Sanitize()
    {
        if (HistoryHours < MinValidHistoryHours || HistoryHours > MaxValidHistoryHours)
        {
            HistoryHours = DefaultHistoryHours;
        }

        if (MinHistoryHours < 0 || MinHistoryHours > HistoryHours)
        {
            MinHistoryHours = DefaultMinHistoryHours;
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
        if (HistoryHours < MinValidHistoryHours || HistoryHours > MaxValidHistoryHours)
        {
            error = $"historyHours deve estar entre {MinValidHistoryHours} e {MaxValidHistoryHours}.";
            return false;
        }

        if (MinHistoryHours < 0)
        {
            error = "minHistoryHours deve ser >= 0.";
            return false;
        }

        if (MinHistoryHours > HistoryHours)
        {
            error = $"minHistoryHours deve ser <= historyHours ({HistoryHours}).";
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
    /// (CLI/API/job) substituem o valor persistido quando presentes e
    /// válidos; caso contrário o valor persistido é mantido.
    ///
    /// <para>
    /// <paramref name="minHistoryHours"/> é opcional (default
    /// <c>null</c> = herda o persistido) e validado em
    /// <c>0..<see cref="MaxValidHistoryHours"/></c>. A normalização de
    /// janela invertida (<c>Min &gt; History</c> → <c>Min = 0</c>) mantém-se.
    /// </para>
    /// </summary>
    public DiscoverySettings WithOverrides(
        string? keyword,
        int? historyHours,
        int? maxStreams,
        int? minHistoryHours = null)
    {
        var effective = Clone();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            effective.Keyword = keyword.Trim();
        }

        if (historyHours is >= MinValidHistoryHours and <= MaxValidHistoryHours)
        {
            effective.HistoryHours = historyHours.Value;
        }

        if (maxStreams is >= MinMaxStreams)
        {
            effective.MaxStreams = maxStreams.Value;
        }

        if (minHistoryHours is >= 0 and <= MaxValidHistoryHours)
        {
            effective.MinHistoryHours = minHistoryHours.Value;
        }

        // Janela invertida após override do limite superior cai no
        // comportamento legacy (sem limite inferior) — nunca numa
        // janela vazia.
        if (effective.MinHistoryHours > effective.HistoryHours)
        {
            effective.MinHistoryHours = DefaultMinHistoryHours;
        }

        return effective;
    }
}

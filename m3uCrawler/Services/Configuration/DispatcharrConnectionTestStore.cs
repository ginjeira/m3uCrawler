using m3uCrawler.Services.Dispatcharr;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// W6c — Registo do último teste de ligação ao Dispatcharr. O estado é
/// persistido no <see cref="AppSettingsStore"/> existente
/// (<c>runtime-data/app_settings.json</c>), não num segundo ficheiro.
///
/// <para>
/// O registo não contém segredos: apenas o <see cref="DispatcharrConnectionStatus"/>,
/// a versão reportada (opcional) e o instante do teste (UTC). É usado pela
/// prontidão operacional para exigir um teste bem sucedido antes de considerar
/// a integração válida.
/// </para>
/// </summary>
public sealed record DispatcharrTestRecord(
    DispatcharrConnectionStatus Status,
    string? Version,
    DateTimeOffset TestedAtUtc)
{
    /// <summary>O último teste terminou com ligação estabelecida.</summary>
    public bool IsConnected => Status == DispatcharrConnectionStatus.Connected;
}

/// <summary>
/// Representação persistida (JSON) do último teste. Classe mutável para o
/// serializador do <see cref="AppSettingsStore"/>.
/// </summary>
public sealed class DispatcharrConnectionTestState
{
    public string? Status { get; set; }
    public string? Version { get; set; }
    public DateTimeOffset? TestedAtUtc { get; set; }
}

/// <summary>
/// Store de leitura/escrita do último teste de ligação, sobre o
/// <see cref="AppSettingsStore"/> partilhado. Nunca regista valores.
/// </summary>
public sealed class DispatcharrConnectionTestStore
{
    private readonly AppSettingsStore _store;

    public DispatcharrConnectionTestStore(AppSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Devolve o último teste persistido, ou <c>null</c> quando nunca foi
    /// testado (ou o registo é ilegível).
    /// </summary>
    public DispatcharrTestRecord? Load()
    {
        var state = _store.Load().DispatcharrTest;
        if (state is null
            || string.IsNullOrWhiteSpace(state.Status)
            || state.TestedAtUtc is null)
        {
            return null;
        }

        if (!Enum.TryParse<DispatcharrConnectionStatus>(
                state.Status, ignoreCase: true, out var status))
        {
            return null;
        }

        return new DispatcharrTestRecord(status, state.Version, state.TestedAtUtc.Value);
    }

    /// <summary>Persiste o resultado do teste mais recente.</summary>
    public void Save(DispatcharrTestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var settings = _store.Load();
        settings.DispatcharrTest = new DispatcharrConnectionTestState
        {
            Status = record.Status.ToString(),
            Version = record.Version,
            TestedAtUtc = record.TestedAtUtc,
        };
        _store.Save(settings);
    }
}

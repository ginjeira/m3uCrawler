namespace m3uCrawler.Services.Preview;

/// <summary>Resultado de um processo curto (ex.: ffprobe) executado até ao fim.</summary>
/// <param name="ExitCode">Código de saída. <c>-1</c> quando o processo foi morto por timeout.</param>
/// <param name="StdOut">Saída padrão capturada integralmente.</param>
/// <param name="StdErr">Saída de erro capturada (limitada).</param>
/// <param name="TimedOut"><c>true</c> se o processo excedeu o timeout e foi terminado.</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Handle de um processo longo (ex.: ffmpeg remux HLS). Implementações reais embrulham
/// <see cref="System.Diagnostics.Process"/>; em testes é usado um fake, sem ffmpeg.
/// </summary>
public interface IProcessHandle : IDisposable
{
    /// <summary><c>true</c> se o processo já terminou (por conta própria, kill ou timeout).</summary>
    bool HasExited { get; }

    /// <summary>Código de saída, ou <c>null</c> se o processo ainda corre.</summary>
    int? ExitCode { get; }

    /// <summary>Espera pela terminação (com timeout). Devolve <c>true</c> se terminou dentro do prazo.</summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout);

    /// <summary>Termina o processo (e a sua árvore). Nunca lança.</summary>
    void Kill();

    /// <summary>
    /// Cauda limitada do stderr, já sanitizada de credenciais via
    /// <c>CredentialSanitizer.SanitizeSensitiveText</c>. Seguro para diagnóstico/logs.
    /// </summary>
    string StderrTail { get; }
}

/// <summary>
/// Abstracção injectável de execução de processos. Existe para que o motor de preview
/// seja testável sem <c>ffmpeg</c>/<c>ffprobe</c> reais e sem rede.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Inicia um processo de longa duração (HLS). O URL vive apenas em <paramref name="args"/>;
    /// nunca é impresso nem registado por esta camada.
    /// </summary>
    IProcessHandle Start(string exe, IReadOnlyList<string> args, string workingDir);

    /// <summary>
    /// Executa um processo curto (ffprobe) até ao fim, capturando stdout. Devolve sempre um
    /// <see cref="ProcessResult"/>; em timeout mata o processo e marca <c>TimedOut</c>.
    /// </summary>
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

namespace m3uCrawler.Services.Preview;

/// <summary>
/// Referência resolvida de um stream a pré-visualizar. A resolução real (canal → URL Xtream
/// com credenciais) é delegada ao host via <c>Func&lt;string, CancellationToken, Task&lt;StreamReference?&gt;&gt;</c>
/// passada ao <see cref="PreviewSessionManager"/>. O motor nunca persiste nem regista <see cref="Url"/>.
/// </summary>
/// <param name="Kind">Tipo da entidade referenciada (ex.: <c>channel</c>, <c>stream</c>).</param>
/// <param name="Id">Identificador estável da entidade.</param>
/// <param name="Url">URL do stream (contém credenciais — nunca logar).</param>
/// <param name="DisplayName">Nome legível opcional para UI/logs sanitizados.</param>
public sealed record StreamReference(string Kind, string Id, string Url, string? DisplayName);

using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// As classes de teste que mutam o estado estático partilhado do
/// <c>WebDashboardService</c> (auth, lifecycle de configuração, host do
/// Live Run, flag <c>--web-allow-trigger</c>) partilham esta colecção.
/// O xUnit serializa as colecções, evitando que uma classe instale
/// estado que outra não espera (ex.: um host de Live Run activo ou um
/// lifecycle READY).
/// </summary>
[CollectionDefinition("DashboardStaticState", DisableParallelization = true)]
public sealed class DashboardStaticStateCollection
{
}

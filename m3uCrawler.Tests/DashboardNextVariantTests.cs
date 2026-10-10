using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Fase 0 da refactorização da IA do dashboard (estratégia A — builder
/// parametrizado por variante). Caracteriza a variante <c>Next</c>
/// (<c>GET /next</c>) e prova que a variante <c>Legacy</c> (<c>GET /</c>)
/// permanece inalterada.
/// </summary>
public class DashboardNextVariantTests
{
    private const string ValidPassword = "a-very-strong-password";

    private static readonly Type ServiceType = typeof(WebDashboardService);
    private static readonly Type VariantType =
        ServiceType.GetNestedType("DashboardVariant", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DashboardVariant não encontrado.");

    private static object Variant(string name) => Enum.Parse(VariantType, name);

    private static string BuildLegacyHtml()
    {
        var method = ServiceType.GetMethod(
            "BuildDashboardHtml", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }

    private static string BuildVariantHtml(string variantName)
    {
        var method = ServiceType.GetMethod(
            "BuildDashboardHtmlFor", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new[] { Variant(variantName) })!;
    }

    private static string BuildPageFor(string? csrfToken, string variantName)
    {
        var method = ServiceType.GetMethod(
            "BuildHtmlPageFor", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { csrfToken, Variant(variantName) })!;
    }

    private static string BuildLegacyPage(string? csrfToken)
    {
        var method = ServiceType.GetMethod(
            "BuildHtmlPage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { csrfToken })!;
    }

    /// <summary>
    /// Extrai o corpo de uma <c>&lt;section id='view-…'&gt;</c> até ao primeiro
    /// <c>&lt;/section&gt;</c>, para distinguir onde cada bloco vive.
    /// </summary>
    private static string Section(string html, string id)
    {
        var start = html.IndexOf($"<section id='{id}'", StringComparison.Ordinal);
        Assert.True(start >= 0, $"secção '{id}' não encontrada.");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"fecho da secção '{id}' não encontrado.");
        return html.Substring(start, end - start);
    }

    /// <summary>
    /// Extrai um sub-bloco do hub de Configuração a partir do seu <c>id</c>
    /// até ao marcador seguinte (o próximo <c>cfg-*</c> ou o fecho da secção).
    /// </summary>
    private static string CfgBlock(string html, string startMarker, string endMarker)
    {
        var start = html.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"marcador '{startMarker}' não encontrado.");
        var end = html.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"fim '{endMarker}' não encontrado.");
        return html.Substring(start, end - start);
    }

    // ===================== Variante Next =====================

    [Fact]
    public void Next_variant_groups_catalog_tabs_by_nature_and_renames_labels()
    {
        var html = BuildVariantHtml("Next");

        // Separadores de grupo.
        Assert.Contains("class='ctab-group'", html);
        Assert.Contains(">Dados</span>", html);
        Assert.Contains(">Configuração</span>", html);
        Assert.Contains(">Monitorização</span>", html);
        Assert.Contains(".ctab-group {", html);

        // Nomes desambiguados.
        Assert.Contains(">Origens</button>", html);
        Assert.Contains(">Ordenação</button>", html);
        Assert.Contains(">Prioridade de Fontes</button>", html);
        Assert.Contains(">Selecção de Fontes</button>", html);
        Assert.Contains(">Agendamento</button>", html);

        // Nav de topo renomeado.
        Assert.Contains("<button data-view='countries'>Países</button>", html);

        // Handlers/ids preservados (a ordem muda, os data-ctab não).
        Assert.Contains("data-ctab='pending'", html);
        Assert.Contains("id='pendingBadge'", html);
        Assert.Contains("data-ctab='policies' hidden", html);
        Assert.Contains("data-ctab='overview' class='active'", html);

        // Link para a UI clássica (só na Next).
        Assert.Contains("UI clássica", html);
    }

    [Fact]
    public void Next_variant_does_not_contain_old_button_labels()
    {
        var html = BuildVariantHtml("Next");

        Assert.DoesNotContain(
            "<button data-view='countries'>Canais / Países</button>", html);
        Assert.DoesNotContain(
            "<button data-ctab='priority' style='padding:8px 14px;'>Source Priority</button>", html);
        Assert.DoesNotContain(
            "<button data-ctab='sourceselection' style='padding:8px 14px;'>Source Selection</button>", html);
        Assert.DoesNotContain(
            "<button data-ctab='sources' style='padding:8px 14px;'>Sources</button>", html);
        Assert.DoesNotContain(
            "<button data-ctab='ordering' style='padding:8px 14px;'>Ordering</button>", html);
        Assert.DoesNotContain(
            "<button data-ctab='scheduled' style='padding:8px 14px;'>Scheduled Jobs</button>", html);
    }

    [Fact]
    public void Next_variant_moves_dispatcharr_config_into_dispatcharr_view()
    {
        var html = BuildVariantHtml("Next");
        var setup = Section(html, "view-setup");
        var dispatcharr = Section(html, "view-dispatcharr");

        // A config completa (ids/handlers intactos) vive agora na vista
        // Dispatcharr — junta-se às ações e ao estado.
        foreach (var id in new[]
        {
            "setupDispatcharrEnabled", "setupDispatcharrBaseUrl", "setupDispatcharrApiKey",
            "setupDispatcharrDryRun", "setupDispatcharrMatchThreshold",
            "setupDispatcharrAutoCreateGroups", "setupDispatcharrProviderPriority",
            "setupDispatcharrAliasFile", "setupDispatcharrUsername", "setupDispatcharrPassword",
            "setupDispatcharrTargetGroupName",
        })
        {
            Assert.Contains($"id='{id}'", dispatcharr);
            Assert.DoesNotContain($"id='{id}'", setup);
        }
        Assert.Contains("onclick='saveDispatcharrConfig()'", dispatcharr);
        Assert.Contains("onclick='testDispatcharrConnection()'", dispatcharr);
        Assert.DoesNotContain("setupDispatcharrEnabled", setup);

        // No Setup resta apenas uma referência curta com atalho.
        Assert.Contains("id='nextGotoDispatcharr'", setup);
        Assert.Contains("Abrir Dispatcharr", setup);

        // Card read-only dos factores que determinam o plano.
        Assert.Contains("Factores que determinam o plano", dispatcharr);
        Assert.Contains("id='nextGotoSourceSelection'", dispatcharr);
        Assert.Contains("id='nextGotoPriority'", dispatcharr);
        Assert.Contains("id='nextGotoOrdering'", dispatcharr);
    }

    [Fact]
    public void Next_variant_injects_wiring_script_only_in_next()
    {
        var html = BuildVariantHtml("Next");

        Assert.Contains("<script>(function(){", html);
        Assert.Contains("window.loadSetup", html);
        Assert.Contains("nextGotoSourceSelection','sourceselection'", html);
        Assert.Contains("nextGotoPriority','priority'", html);
        Assert.Contains("nextGotoOrdering','ordering'", html);

        // O script não existe na Legacy.
        Assert.DoesNotContain("<script>(function(){", BuildLegacyHtml());
    }

    // ===================== Fase 2 — hub Configuração (Next) =====================

    [Fact]
    public void Next_variant_adds_config_hub_nav_section_and_subtabs()
    {
        var html = BuildVariantHtml("Next");

        // Nav de topo: botão "Configuração" logo após "Setup" e antes de "Visão Geral".
        const string configButton = "<button data-view='config'>Configuração</button>";
        Assert.Contains(configButton, html);
        var setupIdx = html.IndexOf("id='navSetupButton'", StringComparison.Ordinal);
        var configIdx = html.IndexOf(configButton, StringComparison.Ordinal);
        var overviewIdx = html.IndexOf(
            "<button data-view='overview' class='active'>Visão Geral</button>", StringComparison.Ordinal);
        Assert.True(setupIdx >= 0 && configIdx > setupIdx && overviewIdx > configIdx);

        // Secção sibling e nav de sub-tabs (discovery activo).
        Assert.Contains("<section id='view-config' hidden>", html);
        Assert.Contains("id='configTabs'", html);
        Assert.Contains("<button data-cfgtab='discovery' class='active'", html);
        foreach (var tab in new[] { "validation", "publication", "scheduling", "security" })
        {
            Assert.Contains($"data-cfgtab='{tab}'", html);
        }

        // As 5 sub-secções.
        Assert.Contains("id='cfg-discovery'", html);
        foreach (var id in new[] { "cfg-validation", "cfg-publication", "cfg-scheduling", "cfg-security" })
        {
            Assert.Contains($"id='{id}' hidden", html);
        }
    }

    [Fact]
    public void Next_variant_moves_discovery_validation_and_password_cards_into_config_hub()
    {
        var html = BuildVariantHtml("Next");
        var cfgDiscovery = CfgBlock(html, "id='cfg-discovery'", "id='cfg-validation'");
        var cfgValidation = CfgBlock(html, "id='cfg-validation'", "id='cfg-publication'");
        var cfgSecurity = CfgBlock(html, "id='cfg-security'", "</section>");
        var viewDiscovery = Section(html, "view-discovery");
        var viewValidation = Section(html, "view-validation");
        var viewSetup = Section(html, "view-setup");

        // Descoberta global movida; no local original fica nota + tabela de candidatos.
        Assert.Contains("id='discoveryKeyword'", cfgDiscovery);
        Assert.Contains("id='discoverySettingsSaveBtn'", cfgDiscovery);
        Assert.DoesNotContain("id='discoveryKeyword'", viewDiscovery);
        Assert.Contains("id='nextGotoConfigDiscovery'", viewDiscovery);
        Assert.Contains("Configuração movida para", viewDiscovery);
        Assert.Contains("id='discState'", viewDiscovery);

        // Política de validação movida; dry-run permanece.
        Assert.Contains("id='validationPolicyForm'", cfgValidation);
        Assert.Contains("onclick='saveValidationPolicy()'", cfgValidation);
        Assert.DoesNotContain("id='validationPolicyForm'", viewValidation);
        // O card "Testar URLs" foi movido para a área Operações na Fase 3.
        Assert.DoesNotContain("id='validationTestUrls'", viewValidation);
        Assert.Contains("id='nextGotoConfigValidation'", viewValidation);
        Assert.Contains("id='nextGotoOperationsValidation'", viewValidation);

        // Card de password movido; no Setup fica nota + atalho.
        Assert.Contains("id='accountCurrentPassword'", cfgSecurity);
        Assert.Contains("onclick='changePassword()'", cfgSecurity);
        Assert.Contains("id='accountPasswordStatus'", cfgSecurity);
        Assert.DoesNotContain("id='accountCurrentPassword'", viewSetup);
        Assert.Contains("id='nextGotoConfigSecurity'", viewSetup);
    }

    [Fact]
    public void Next_variant_config_hub_has_index_links_and_precedence_block()
    {
        var html = BuildVariantHtml("Next");
        var cfgDiscovery = CfgBlock(html, "id='cfg-discovery'", "id='cfg-validation'");
        var cfgPublication = CfgBlock(html, "id='cfg-publication'", "id='cfg-scheduling'");
        var cfgScheduling = CfgBlock(html, "id='cfg-scheduling'", "id='cfg-security'");

        // Precedência (global → job → run); a validação fica no servidor.
        Assert.Contains("predefinição global → override por job → override por run", cfgDiscovery);
        Assert.Contains("id='cfgGotoScheduledOverrides'", cfgDiscovery);
        Assert.Contains("id='cfgGotoRunOverrides'", cfgDiscovery);

        // Publicação: página-índice com ligações aos editores actuais.
        Assert.Contains("Página-índice", cfgPublication);
        foreach (var id in new[]
        {
            "cfgGotoOrdering", "cfgGotoSourceSelection", "cfgGotoPriority", "cfgGotoGroups",
        })
        {
            Assert.Contains($"id='{id}'", cfgPublication);
        }

        // Agendamento: página-índice com ligação aos Scheduled Jobs.
        Assert.Contains("Página-índice", cfgScheduling);
        Assert.Contains("id='cfgGotoScheduled'", cfgScheduling);
    }

    [Fact]
    public void Next_variant_injects_config_hub_wiring_script()
    {
        var html = BuildVariantHtml("Next");

        Assert.Contains("document.querySelectorAll('#configTabs button')", html);
        Assert.Contains("function cfgTab(", html);
        Assert.Contains("loadDiscoveryLocal", html);
        Assert.Contains("/api/discovery/settings", html);
        Assert.Contains("window.loadValidationPolicy", html);
        Assert.Contains("cfgGotoRunOverrides", html);
        Assert.Contains("setCfgActive(currentCfg)", html);

        // Wiring do hub não existe na Legacy.
        Assert.DoesNotContain("#configTabs", BuildLegacyHtml());
    }

    // ===================== Fase 3 — Operações + Monitorização (Next) =====================

    [Fact]
    public void Next_variant_adds_operations_and_monitoring_nav_and_sections()
    {
        var html = BuildVariantHtml("Next");

        // Nav de topo: botões "Operações" e "Monitorização" após "Visão Geral".
        const string overviewButton = "<button data-view='overview' class='active'>Visão Geral</button>";
        const string operationsButton = "<button data-view='operations'>Operações</button>";
        const string monitoringButton = "<button data-view='monitoring'>Monitorização</button>";
        Assert.Contains(operationsButton, html);
        Assert.Contains(monitoringButton, html);
        var overviewIdx = html.IndexOf(overviewButton, StringComparison.Ordinal);
        var operationsIdx = html.IndexOf(operationsButton, StringComparison.Ordinal);
        var monitoringIdx = html.IndexOf(monitoringButton, StringComparison.Ordinal);
        Assert.True(overviewIdx >= 0 && operationsIdx > overviewIdx && monitoringIdx > operationsIdx);

        // Secções sibling (`main > section`), ocultas por omissão.
        Assert.Contains("<section id='view-operations' hidden>", html);
        Assert.Contains("<section id='view-monitoring' hidden>", html);
        Assert.DoesNotContain("id='view-operations'", BuildLegacyHtml());
        Assert.DoesNotContain("id='view-monitoring'", BuildLegacyHtml());
    }

    [Fact]
    public void Next_variant_moves_validation_test_card_into_operations()
    {
        var html = BuildVariantHtml("Next");
        var operations = Section(html, "view-operations");
        var validation = Section(html, "view-validation");

        // O card "Testar URLs" vive agora em Operações (ids/handlers intactos).
        Assert.Contains("id='validationTestUrls'", operations);
        Assert.Contains("id='validationTestResult'", operations);
        Assert.Contains("onclick='runValidationTest()'", operations);
        Assert.DoesNotContain("id='validationTestUrls'", validation);
        Assert.Contains("id='nextGotoOperationsValidation'", validation);

        // Restantes ações one-shot agrupadas (botões que navegam para o contexto).
        foreach (var id in new[]
        {
            "opRunNow", "opDispatcharrDryRun", "opDispatcharrSync", "opDispatcharrTest", "opValidateCountry",
        })
        {
            Assert.Contains($"id='{id}'", operations);
        }
    }

    [Fact]
    public void Next_variant_moves_monitoring_containers_out_of_catalog()
    {
        var html = BuildVariantHtml("Next");
        var monitoring = Section(html, "view-monitoring");
        var catalog = Section(html, "view-catalog");
        var catalogTabs = CfgBlock(html, "id='catalogTabs'", "</nav>");

        // Nova nav de sub-tabs, com Matching activo, e os 4 contentores movidos.
        Assert.Contains("id='monitoringTabs'", monitoring);
        Assert.Contains("data-monttab='matching' class='active'", monitoring);
        foreach (var tab in new[] { "degradation", "audit", "syncruns" })
        {
            Assert.Contains($"data-monttab='{tab}'", monitoring);
        }
        foreach (var id in new[] { "mont-matching", "mont-degradation", "mont-audit", "mont-syncruns" })
        {
            Assert.Contains($"id='{id}'", monitoring);
        }

        // Os ids internos das tabelas/loaders não mudam.
        foreach (var id in new[] { "matchingAuditsTable", "degradationTable", "auditTable", "catalogSyncRunsTable" })
        {
            Assert.Contains($"id='{id}'", monitoring);
        }

        // Os botões data-ctab dos 4 sub-tabs monitor saíram do #catalogTabs.
        foreach (var tab in new[] { "matching", "degradation", "audit", "syncruns" })
        {
            Assert.DoesNotContain($"data-ctab='{tab}'", catalogTabs);
        }

        // Os contentores ctab-* já não vivem no Catálogo; fica uma nota/atalho.
        foreach (var id in new[] { "ctab-matching", "ctab-degradation", "ctab-audit", "ctab-syncruns" })
        {
            Assert.DoesNotContain($"id='{id}'", catalog);
        }
        Assert.Contains("id='nextGotoMonitoring'", catalog);
        Assert.Contains("passaram para a área", catalog);
    }

    [Fact]
    public void Next_variant_injects_operations_and_monitoring_wiring_script()
    {
        var html = BuildVariantHtml("Next");

        Assert.Contains("function montTab(", html);
        Assert.Contains("function setMontActive(", html);
        Assert.Contains("'#monitoringTabs button'", html);
        Assert.Contains("montTab('matching')", html);
        Assert.Contains("window.loadMatchingAudits", html);
        Assert.Contains("window.loadDegradation", html);
        Assert.Contains("window.loadCatalogAudits", html);
        Assert.Contains("window.loadCatalogSyncRuns", html);
        Assert.Contains("!=='mont-'+t", html);
        Assert.Contains("'opRunNow'", html);
        Assert.Contains("'opDispatcharrDryRun'", html);
        Assert.Contains("'opValidateCountry'", html);

        // O wiring da Fase 3 não existe na Legacy.
        var legacy = BuildLegacyHtml();
        Assert.DoesNotContain("function montTab(", legacy);
        Assert.DoesNotContain("id='opRunNow'", legacy);
    }

    // ===================== Variante Legacy (inalterada) =====================

    [Fact]
    public void Legacy_variant_is_unchanged_and_equal_to_base_builder()
    {
        var viaNoArg = BuildLegacyHtml();
        var viaVariant = BuildVariantHtml("Legacy");

        // O método sem argumentos e a variante Legacy produzem a string actual,
        // byte a byte.
        Assert.Equal(viaNoArg, viaVariant);
    }

    [Fact]
    public void Legacy_variant_does_not_contain_next_markers()
    {
        var html = BuildLegacyHtml();

        Assert.DoesNotContain("class='ctab-group'", html);
        Assert.DoesNotContain(".ctab-group {", html);
        Assert.DoesNotContain("UI clássica", html);
        Assert.DoesNotContain(">Origens</button>", html);
        Assert.DoesNotContain(">Ordenação</button>", html);
        Assert.DoesNotContain(">Prioridade de Fontes</button>", html);
        Assert.DoesNotContain(">Selecção de Fontes</button>", html);
        Assert.DoesNotContain(">Agendamento</button>", html);

        // Os rótulos antigos continuam presentes.
        Assert.Contains("<button data-view='countries'>Canais / Países</button>", html);
        Assert.Contains(
            "<button data-ctab='priority' style='padding:8px 14px;'>Source Priority</button>", html);
        Assert.Contains(
            "<button data-ctab='sourceselection' style='padding:8px 14px;'>Source Selection</button>", html);
    }

    [Fact]
    public void Legacy_variant_keeps_dispatcharr_config_in_setup()
    {
        var html = BuildLegacyHtml();
        var setup = Section(html, "view-setup");
        var dispatcharr = Section(html, "view-dispatcharr");

        // A config continua no Setup e a vista Dispatcharr continua só com
        // ações/estado (nenhum marcador da Fase 1).
        Assert.Contains("id='setupDispatcharrEnabled'", setup);
        Assert.DoesNotContain("id='setupDispatcharrEnabled'", dispatcharr);
        Assert.DoesNotContain("id='nextGotoDispatcharr'", html);
        Assert.DoesNotContain("id='nextGotoSourceSelection'", html);
        Assert.DoesNotContain("Factores que determinam o plano", html);
    }

    [Fact]
    public void Legacy_variant_keeps_cards_in_original_places_without_config_hub()
    {
        var html = BuildLegacyHtml();

        // Nenhum marcador da Fase 2.
        Assert.DoesNotContain("id='view-config'", html);
        Assert.DoesNotContain("data-view='config'", html);
        Assert.DoesNotContain("id='configTabs'", html);
        Assert.DoesNotContain("id='cfg-discovery'", html);
        Assert.DoesNotContain("id='nextGotoConfigDiscovery'", html);

        // Os cards continuam nos locais originais.
        Assert.Contains("id='discoveryKeyword'", Section(html, "view-discovery"));
        Assert.Contains("id='validationPolicyForm'", Section(html, "view-validation"));
        Assert.Contains("id='accountCurrentPassword'", Section(html, "view-setup"));
    }

    [Fact]
    public void Legacy_variant_keeps_catalog_monitoring_tabs_and_validation_test_card()
    {
        var html = BuildLegacyHtml();
        var catalog = Section(html, "view-catalog");
        var validation = Section(html, "view-validation");

        // Nenhum marcador da Fase 3.
        Assert.DoesNotContain("id='view-operations'", html);
        Assert.DoesNotContain("id='view-monitoring'", html);
        Assert.DoesNotContain("data-view='operations'", html);
        Assert.DoesNotContain("data-view='monitoring'", html);
        Assert.DoesNotContain("id='monitoringTabs'", html);
        Assert.DoesNotContain("id='nextGotoMonitoring'", html);
        Assert.DoesNotContain("id='opRunNow'", html);

        // Os 4 sub-tabs INFO continuam no #catalogTabs e os contentores ctab-* no Catálogo.
        var catalogTabs = CfgBlock(html, "id='catalogTabs'", "</nav>");
        foreach (var tab in new[] { "matching", "degradation", "audit", "syncruns" })
        {
            Assert.Contains($"data-ctab='{tab}'", catalogTabs);
        }
        foreach (var id in new[] { "ctab-matching", "ctab-degradation", "ctab-audit", "ctab-syncruns" })
        {
            Assert.Contains($"id='{id}'", catalog);
        }

        // O card "Testar URLs" permanece em view-validation.
        Assert.Contains("id='validationTestUrls'", validation);
        Assert.DoesNotContain("id='nextGotoOperationsValidation'", html);
    }

    // ===================== Injecção de CSRF =====================

    [Fact]
    public void BuildHtmlPageFor_injects_csrf_like_legacy()
    {
        const string token = "csrf-token-value";

        var legacyWithToken = BuildLegacyPage(token);
        var nextWithToken = BuildPageFor(token, "Next");
        var nextWithoutToken = BuildPageFor(null, "Next");

        // A injecção de CSRF é a mesma que a Legacy.
        Assert.Contains(token, legacyWithToken);
        Assert.Contains(token, nextWithToken);
        // Assinatura única do script de injecção (o base apenas lê a variável).
        Assert.Contains("window.__m3uCrawlerCsrf=t;", nextWithToken);
        Assert.DoesNotContain("window.__m3uCrawlerCsrf=t;", nextWithoutToken);

        // Sem CSRF, a página Next é a própria string da variante Next.
        Assert.Equal(BuildVariantHtml("Next"), nextWithoutToken);

        // A página Legacy com token é exactamente o builder Legacy com a injecção.
        Assert.Equal(legacyWithToken, BuildLegacyPage(token));
    }

    // ===================== Safety net CSRF (só Next) =====================

    [Fact]
    public void Next_variant_injects_csrf_safety_net_only_in_next()
    {
        var next = BuildVariantHtml("Next");

        // A rede de segurança (reload único por sessão em `csrf-invalid`) vive
        // exclusivamente na variante Next.
        Assert.Contains("m3u_csrf_reload", next);
        Assert.Contains("csrf-invalid", next);
        Assert.Contains("window.fetch", next);

        // A Legacy permanece sem o marcador (identidade byte a byte preservada).
        Assert.DoesNotContain("m3u_csrf_reload", BuildLegacyHtml());
    }

    // ===================== Roteamento HTTP =====================

    [Collection("DashboardStaticState")]
    public sealed class Http : IAsyncLifetime
    {
        private readonly string _root;
        private readonly string _dbPath;
        private readonly string _outputDir;
        private readonly string _storePath;

        private TestDbContextFactory _factory = null!;
        private CatalogResolver _resolver = null!;
        private PlaylistComposerService _composer = null!;
        private ImportHistoryService _history = null!;
        private ConfigurationLifecycleService _lifecycle = null!;
        private AuthService _auth = null!;
        private BootstrapService _bootstrap = null!;
        private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

        public Http()
        {
            _root = Path.Combine(Path.GetTempPath(), $"dash-next-{Guid.NewGuid():N}");
            _dbPath = Path.Combine(_root, "channel-catalog.db");
            _outputDir = Path.Combine(_root, "output");
            _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
        }

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(_outputDir);
            _factory = new TestDbContextFactory(_dbPath);
            var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
            var ctx = await bootstrapper.InitializeAsync();
            await ctx.DisposeAsync();

            _resolver = new CatalogResolver(_factory, _dbPath);
            _composer = new PlaylistComposerService(_factory);
            _history = new ImportHistoryService(_outputDir);
            _lifecycle = new ConfigurationLifecycleService(
                new ConfigurationLifecycleStore(_storePath), _factory, _outputDir);

            var users = new AdminUserStore(_factory);
            _auth = new AuthService(users, new SessionStore(_factory));
            _bootstrap = new BootstrapService(
                _lifecycle, users,
                new BootstrapConfigurationValidator(_factory, _outputDir));

            _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
                _outputDir, _resolver, _composer, _history,
                _lifecycle, _auth, _bootstrap, webToken: null);
        }

        public async Task DisposeAsync()
        {
            if (_harness != null)
            {
                await _harness.DisposeAsync();
            }
            WebDashboardService.SetAuth(null, null);
            WebDashboardService.SetConfigurationLifecycle(null);
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task Next_in_bootstrap_redirects_to_wizard_like_root()
        {
            // Fresh install → AuthMode.Bootstrap.
            var next = await _harness!.Client.GetAsync("/next");
            Assert.Equal(HttpStatusCode.Found, next.StatusCode);
            Assert.Equal("/bootstrap", next.Headers.Location?.ToString());

            var root = await _harness.Client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Found, root.StatusCode);
            Assert.Equal("/bootstrap", root.Headers.Location?.ToString());
        }

        [Fact]
        public async Task Next_requires_session_and_serves_next_variant_on_session()
        {
            await ReachReadyWithAdminAsync();

            // Sem sessão → HTML de login (não 401 JSON), tal como a raiz.
            var anonymous = await _harness!.Client.GetAsync("/next");
            Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
            var anonHtml = await anonymous.Content.ReadAsStringAsync();
            Assert.Contains("Entrar", anonHtml);
            Assert.DoesNotContain("class='ctab-group'", anonHtml);
            Assert.DoesNotContain("not-found", anonHtml);

            // Login humano.
            var login = await _harness.Client.PostAsync(
                "/api/session",
                new StringContent(
                    JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                    Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            // Com sessão → variante Next.
            var next = await _harness.Client.GetAsync("/next");
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            var nextHtml = await next.Content.ReadAsStringAsync();
            Assert.Contains("class='ctab-group'", nextHtml);
            Assert.Contains(">Origens</button>", nextHtml);
            Assert.Contains(">Prioridade de Fontes</button>", nextHtml);
            Assert.Contains("UI clássica", nextHtml);

            // A raiz continua a servir a Legacy.
            var root = await _harness.Client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            var rootHtml = await root.Content.ReadAsStringAsync();
            Assert.Contains("<button data-view='countries'>Canais / Países</button>", rootHtml);
            Assert.Contains(
                "<button data-ctab='priority' style='padding:8px 14px;'>Source Priority</button>", rootHtml);
            Assert.DoesNotContain("class='ctab-group'", rootHtml);
            Assert.DoesNotContain("UI clássica", rootHtml);
        }

        [Fact]
        public async Task Next_serves_dispatcharr_workspace_and_root_stays_legacy()
        {
            await ReachReadyWithAdminAsync();

            var login = await _harness!.Client.PostAsync(
                "/api/session",
                new StringContent(
                    JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                    Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            // Com sessão: /next traz o workspace Dispatcharr (config movida +
            // factores + atalho).
            var next = await _harness.Client.GetAsync("/next");
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            var nextHtml = await next.Content.ReadAsStringAsync();
            Assert.Contains("id='nextGotoDispatcharr'", nextHtml);
            Assert.Contains("Factores que determinam o plano", nextHtml);

            // A raiz continua a Legacy, sem marcadores da Fase 1.
            var root = await _harness.Client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            var rootHtml = await root.Content.ReadAsStringAsync();
            Assert.DoesNotContain("id='nextGotoDispatcharr'", rootHtml);
            Assert.DoesNotContain("Factores que determinam o plano", rootHtml);
        }

        [Fact]
        public async Task Next_serves_config_hub_and_root_stays_legacy()
        {
            await ReachReadyWithAdminAsync();

            var login = await _harness!.Client.PostAsync(
                "/api/session",
                new StringContent(
                    JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                    Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            // Com sessão: /next traz o hub Configuração.
            var next = await _harness.Client.GetAsync("/next");
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            var nextHtml = await next.Content.ReadAsStringAsync();
            Assert.Contains("id='view-config'", nextHtml);
            Assert.Contains("id='configTabs'", nextHtml);
            Assert.Contains("<button data-view='config'>Configuração</button>", nextHtml);

            // A raiz continua Legacy, sem o hub.
            var root = await _harness.Client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            var rootHtml = await root.Content.ReadAsStringAsync();
            Assert.DoesNotContain("id='view-config'", rootHtml);
            Assert.DoesNotContain("id='configTabs'", rootHtml);
        }

        [Fact]
        public async Task Next_serves_operations_and_monitoring_and_root_stays_legacy()
        {
            await ReachReadyWithAdminAsync();

            var login = await _harness!.Client.PostAsync(
                "/api/session",
                new StringContent(
                    JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                    Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            // Com sessão: /next traz as áreas Operações e Monitorização.
            var next = await _harness.Client.GetAsync("/next");
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            var nextHtml = await next.Content.ReadAsStringAsync();
            Assert.Contains("id='view-operations'", nextHtml);
            Assert.Contains("id='view-monitoring'", nextHtml);
            Assert.Contains("<button data-view='operations'>Operações</button>", nextHtml);
            Assert.Contains("<button data-view='monitoring'>Monitorização</button>", nextHtml);
            Assert.Contains("id='monitoringTabs'", nextHtml);

            // A raiz continua Legacy, sem marcadores da Fase 3.
            var root = await _harness.Client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            var rootHtml = await root.Content.ReadAsStringAsync();
            Assert.DoesNotContain("id='view-operations'", rootHtml);
            Assert.DoesNotContain("id='view-monitoring'", rootHtml);
            Assert.Contains("id='validationTestUrls'", rootHtml);
        }

        [Fact]
        public async Task Html_responses_send_no_cache_headers()
        {
            await ReachReadyWithAdminAsync();

            // Sem sessão, `/` e `/next` servem o login: as páginas HTML nunca
            // devem ser reutilizadas de cache (evita um `/next` antigo com
            // token CSRF desactualizado).
            foreach (var path in new[] { "/", "/next" })
            {
                var response = await _harness!.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains(
                    "no-store",
                    string.Join(",", response.Headers.GetValues("Cache-Control")));
                Assert.Contains(
                    "no-cache",
                    string.Join(",", response.Headers.GetValues("Pragma")));
            }

            // Com sessão, a variante Next continua a enviar os mesmos cabeçalhos.
            var login = await _harness!.Client.PostAsync(
                "/api/session",
                new StringContent(
                    JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                    Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            var next = await _harness.Client.GetAsync("/next");
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.Contains(
                "no-store",
                string.Join(",", next.Headers.GetValues("Cache-Control")));
            Assert.Contains(
                "no-cache",
                string.Join(",", next.Headers.GetValues("Pragma")));
        }

        private async Task ReachReadyWithAdminAsync()
        {
            var users = new AdminUserStore(_factory);
            Assert.Equal(CreateAdminResult.Created, await users.CreateFirstAdminAsync("admin", ValidPassword));
            _lifecycle.SetState(ConfigurationLifecycleState.Ready, "test-ready-with-admin");
        }
    }
}

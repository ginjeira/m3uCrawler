using m3uCrawler.Build;
using m3uCrawler.Models;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Sync;
using m3uCrawler.Services.Validation;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace m3uCrawler.Services
{
    public static class WebDashboardService
    {
        private static CatalogResolver? _catalogResolver;
        private static IReadOnlyList<IScheduledAction>? _scheduledActions;
        private static ConfigurationLifecycleService? _configurationLifecycle;
        private static AuthService? _authService;
        private static BootstrapService? _bootstrapService;
        private static LiveRunHost? _liveRunHost;
        private static bool _webAllowTrigger;

        /// <summary>
        /// PHASE 9C.2 (S1-E) — Indica que o Dashboard corre num contexto
        /// explicitamente standalone/testes, onde a ausência simultânea de
        /// lifecycle e auth é legítima (comportamento legacy preservado).
        ///
        /// Em produção é sempre <c>false</c>: nesse caso, lifecycle e auth
        /// ambos ausentes (p.ex. falha na inicialização do catálogo) é
        /// fail-closed — nunca <c>Legacy</c> aberto.
        /// </summary>
        private static bool _standaloneAuthContext;

        /// <summary>Nome do cookie de sessão de administrador.</summary>
        public const string SessionCookieName = "m3u_session";
        private const string CsrfHeaderName = "X-CSRF-Token";

        public static void SetCatalogResolver(CatalogResolver resolver)
        {
            _catalogResolver = resolver;
        }

        /// <summary>
        /// PHASE 9C.2 — Regista os serviços de autenticação/bootstrap.
        /// Passar <c>null</c> repõe o comportamento "não ligado" (equivalente
        /// a legacy), usado em testes.
        /// </summary>
        public static void SetAuth(AuthService? authService, BootstrapService? bootstrapService)
        {
            _authService = authService;
            _bootstrapService = bootstrapService;
        }

        /// <summary>
        /// PHASE 9C.1 — Regista o serviço de lifecycle para que o dashboard
        /// possa reportar o estado de configuração. Passar <c>null</c> em
        /// testes repõe o comportamento "não ligado".
        /// </summary>
        public static void SetConfigurationLifecycle(ConfigurationLifecycleService? lifecycle)
        {
            _configurationLifecycle = lifecycle;
        }

        /// <summary>
        /// Regista a lista de <see cref="IScheduledAction"/> resolvidas
        /// pelo <c>ScheduledAutomationHost</c> para que o formulário
        /// de Scheduled Jobs apresente opções válidas. Mantém-se
        /// retro-compatibilidade: se não for chamado, o formulário
        /// aceita qualquer <c>actionName</c> livre.
        /// </summary>
        public static void SetScheduledActions(IEnumerable<IScheduledAction> actions)
        {
            _scheduledActions = actions.ToArray();
        }

        /// <summary>
        /// PHASE 9C.4 — Regista o <see cref="LiveRunHost"/> que faz a ponte
        /// entre o <see cref="RunCoordinator"/> único e os endpoints
        /// <c>GET /api/run/status</c> e <c>POST /api/run/start</c>. Sem
        /// host, os endpoints respondem <c>pipeline-not-configured</c>.
        /// </summary>
        public static void SetLiveRunHost(LiveRunHost? host)
        {
            _liveRunHost = host;
        }

        /// <summary>
        /// PHASE 9C.4 — Activa o trigger manual (botão "Run now") no
        /// dashboard. Default: <c>false</c>. Quando <c>false</c>,
        /// <c>POST /api/run/start</c> devolve 503
        /// <c>web-allow-trigger-disabled</c>; <c>GET /api/run/status</c>
        /// não é afectado.
        /// </summary>
        public static void SetWebAllowTrigger(bool allow)
        {
            _webAllowTrigger = allow;
        }

        public static async Task RunDashboardAsync(string outputDir, int port, ImportHistoryService historyService, string? webToken = null, CancellationToken cancellationToken = default)
        {
            var listener = new HttpListener();
            var prefix = $"http://+:{port}/";
            listener.Prefixes.Add(prefix);
            listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;

            try
            {
                listener.Start();
                Console.WriteLine($"🌐 Dashboard iniciado em {prefix}");
                if (!string.IsNullOrWhiteSpace(webToken))
                {
                    Console.WriteLine($"🔐 Dashboard protegido por token partilhado (Authorization: Bearer <token> ou ?token=).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Não foi possível iniciar o dashboard web: {ex.Message}");
                Console.WriteLine(ex);
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(async () => await HandleRequestAsync(context, outputDir, historyService, webToken));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Erro no dashboard web: {ex.Message}");
                }
            }

            listener.Stop();
        }

        /// <summary>
        /// Variante testável: executa o handler para um único
        /// <see cref="HttpListenerContext"/>, com <see cref="CatalogResolver"/>
        /// e <see cref="PlaylistComposerService"/> injetados. Permite
        /// isolar cada teste num DbContext próprio sem tocar no
        /// resolver estático global.
        /// </summary>
        public static async Task HandleRequestOnTestAsync(
            HttpListenerContext context,
            string outputDir,
            CatalogResolver resolver,
            PlaylistComposerService composer,
            ImportHistoryService historyService,
            string? webToken = null)
        {
            using var scope = new StaticResolverScope(resolver, standaloneAuthContext: true);
            await HandleRequestAsync(context, outputDir, historyService, webToken);
        }

        /// <summary>
        /// PHASE 9C.2 — Variante testável com lifecycle/auth/bootstrap
        /// isolados por chamada. Evita interferência entre testes que correm
        /// em paralelo através dos campos estáticos. Não marca contexto
        /// standalone: sem lifecycle/auth o resultado é fail-closed.
        /// </summary>
        public static async Task HandleRequestWithAuthOnTestAsync(
            HttpListenerContext context,
            string outputDir,
            CatalogResolver resolver,
            PlaylistComposerService composer,
            ImportHistoryService historyService,
            ConfigurationLifecycleService? lifecycle,
            AuthService? authService,
            BootstrapService? bootstrapService,
            string? webToken = null)
        {
            using var scope = new StaticResolverScope(resolver);
            using var authScope = new StaticAuthScope(lifecycle, authService, bootstrapService);
            await HandleRequestAsync(context, outputDir, historyService, webToken);
        }

        private sealed class StaticAuthScope : IDisposable
        {
            private readonly ConfigurationLifecycleService? _previousLifecycle;
            private readonly AuthService? _previousAuth;
            private readonly BootstrapService? _previousBootstrap;

            public StaticAuthScope(
                ConfigurationLifecycleService? lifecycle,
                AuthService? authService,
                BootstrapService? bootstrapService)
            {
                _previousLifecycle = _configurationLifecycle;
                _previousAuth = _authService;
                _previousBootstrap = _bootstrapService;
                _configurationLifecycle = lifecycle;
                _authService = authService;
                _bootstrapService = bootstrapService;
            }

            public void Dispose()
            {
                _configurationLifecycle = _previousLifecycle;
                _authService = _previousAuth;
                _bootstrapService = _previousBootstrap;
            }
        }

        /// <summary>
        /// PHASE 9C.4 — Scope testável para o <see cref="LiveRunHost"/> e
        /// o flag <c>--web-allow-trigger</c>. Restaura o estado anterior
        /// em <see cref="Dispose"/>, garantindo isolamento entre testes
        /// paralelos.
        /// </summary>
        public sealed class StaticLiveRunHostScope : IDisposable
        {
            private readonly LiveRunHost? _previousHost;
            private readonly bool _previousAllow;

            public StaticLiveRunHostScope(LiveRunHost? host, bool allowTrigger)
            {
                _previousHost = _liveRunHost;
                _previousAllow = _webAllowTrigger;
                _liveRunHost = host;
                _webAllowTrigger = allowTrigger;
            }

            public void Dispose()
            {
                _liveRunHost = _previousHost;
                _webAllowTrigger = _previousAllow;
            }
        }

        /// <summary>
        /// Variante testável do runner: arranca o <see cref="HttpListener"/>
        /// em loopback com factory e runtimeDir fornecidos.
        /// </summary>
        public static async Task RunDashboardForTestsAsync(
            string outputDir,
            int port,
            CatalogResolver resolver,
            PlaylistComposerService composer,
            ImportHistoryService historyService,
            string? webToken = null,
            CancellationToken cancellationToken = default)
        {
            using var scope = new StaticResolverScope(resolver, standaloneAuthContext: true);
            await RunDashboardAsync(outputDir, port, historyService, webToken, cancellationToken);
        }

        private sealed class StaticResolverScope : IDisposable
        {
            private readonly CatalogResolver? _previous;
            private readonly bool _previousStandaloneAuthContext;

            public StaticResolverScope(CatalogResolver resolver, bool standaloneAuthContext = false)
            {
                _previous = _catalogResolver;
                _previousStandaloneAuthContext = _standaloneAuthContext;
                _catalogResolver = resolver;
                _standaloneAuthContext = standaloneAuthContext;
            }

            public void Dispose()
            {
                _catalogResolver = _previous;
                _standaloneAuthContext = _previousStandaloneAuthContext;
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context, string outputDir, ImportHistoryService historyService, string? webToken = null)
        {
            var requestPath = context.Request.Url?.AbsolutePath ?? "/";
            var query = context.Request.Url?.Query ?? string.Empty;

            // Protecção opcional por token partilhado: se --web-token foi configurado,
            // todos os endpoints exigem o token via header Authorization: Bearer
            // ou query ?token=. Se não configurado, mantém-se o comportamento aberto
            // (compatibilidade com deployments locais).
            //
            // PHASE 9C.2 (B1) — O token é uma credencial de MÁQUINA. Quando válido,
            // autoriza o pedido sem exigir sessão humana, incluindo em READY + admin
            // (UserAuth). É distinto da autenticação humana e não cria utilizador
            // nem sessão.
            var tokenAuthorization = EvaluateTokenAuthorization(context.Request, webToken);
            if (tokenAuthorization == TokenAuthorization.Rejected)
            {
                await WriteUnauthorizedAsync(context.Response);
                return;
            }
            var machineAuthorized = tokenAuthorization == TokenAuthorization.Authorized;

            // === PHASE 9C.2 — Authentication / bootstrap ===
            // Gate único, avaliado antes de qualquer rota não pública.
            // PHASE 9C.2/9C.5 — modo e estado de lifecycle numa única decisão.
            var (authMode, lifecycleState) = await ResolveAuthDecisionAsync();
            var sessionId = GetCookieValue(context.Request, SessionCookieName);
            var isRootPath = requestPath.Length == 0 || requestPath == "/";

            // --- Bootstrap HTML + endpoints (só activos em bootstrap) ---
            if (requestPath.Equals("/bootstrap", StringComparison.OrdinalIgnoreCase))
            {
                if (authMode == AuthMode.Bootstrap)
                {
                    await WriteHtmlAsync(context.Response, BuildBootstrapHtml());
                }
                else
                {
                    RedirectTo(context.Response, "/");
                }
                return;
            }

            if (requestPath.StartsWith("/api/bootstrap/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleBootstrapEndpointAsync(context, requestPath, authMode);
                return;
            }

            // --- Sessão (login/logout/user actual) ---
            if (requestPath.Equals("/api/session", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSessionEndpointAsync(context);
                return;
            }

            // --- Enforcement para os restantes endpoints existentes ---
            // Não é um segundo pipeline: é um único gate que decide, por modo,
            // se o handler existente pode correr.
            if (!isRootPath && !IsAlwaysPublicPath(requestPath))
            {
                if (authMode == AuthMode.Bootstrap && !machineAuthorized)
                {
                    // PHASE 9C.5 (F6) — reportar o estado real (pode ser READY
                    // em BOOTSTRAP_REQUIRED), nunca um valor fixo.
                    await WriteJsonAsync(
                        context.Response,
                        new { error = "bootstrap-required", state = lifecycleState.ToWireName() },
                        HttpStatusCode.Forbidden);
                    return;
                }

                if (authMode == AuthMode.UserAuth && !machineAuthorized)
                {
                    // Sem credencial de máquina válida, exige sessão humana.
                    // Se o serviço de autenticação não estiver disponível, o
                    // resultado é 401 (fail-closed) — nunca autorização implícita.
                    var session = _authService != null
                        ? await _authService.ValidateSessionAsync(sessionId)
                        : null;
                    if (session == null)
                    {
                        await WriteJsonAsync(
                            context.Response,
                            new { error = "authentication-required" },
                            HttpStatusCode.Unauthorized);
                        return;
                    }

                    var method = context.Request.HttpMethod;
                    if (IsMutatingMethod(method))
                    {
                        var presented = context.Request.Headers[CsrfHeaderName];
                        if (string.IsNullOrEmpty(presented) || !FixedEquals(presented, session.CsrfToken))
                        {
                            await WriteJsonAsync(
                                context.Response,
                                new { error = "csrf-invalid" },
                                HttpStatusCode.Forbidden);
                            return;
                        }
                    }
                }
            }

            if (requestPath.Equals("/api/history", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, await historyService.GetRecentAsync(TimeSpan.FromHours(72)));
                return;
            }

            if (requestPath.Equals("/api/version", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, BuildVersionPayload());
                return;
            }

            // PHASE 9C.1 — Estado do ciclo de vida de configuração.
            // Endpoint de leitura apenas: em NOT_CONFIGURED o dashboard
            // continua acessível e mostra inequivocamente que a aplicação
            // ainda não está configurada. Não expõe operações destrutivas
            // nem contorna a autenticação (o gate de token é avaliado
            // antes, no topo do handler).
            if (requestPath.Equals("/api/configuration/lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, await BuildLifecyclePayloadAsync(_configurationLifecycle));
                return;
            }

            if (requestPath.Equals("/api/countries", StringComparison.OrdinalIgnoreCase))
            {
                var service = new CountryChannelListService(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                await WriteJsonAsync(context.Response, service.GetAllCountries());
                return;
            }

            if (requestPath.Equals("/api/country", StringComparison.OrdinalIgnoreCase))
            {
                var countryCode = context.Request.QueryString["country"];
                if (string.IsNullOrWhiteSpace(countryCode))
                {
                    await WriteJsonAsync(context.Response, new { error = "Parâmetro country é obrigatório." });
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                var service = new CountryChannelListService(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                await WriteJsonAsync(context.Response, service.GetCountry(countryCode));
                return;
            }

            if (requestPath.Equals("/api/country/validate", StringComparison.OrdinalIgnoreCase))
            {
                var countryCode = context.Request.QueryString["country"] ?? "pt";
                var countryList = new CountryChannelListService(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                var validator = new CountryChannelValidator(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                var playlistPath = Path.Combine(outputDir, "playlist.m3u");
                var playlistText = File.Exists(playlistPath) ? await File.ReadAllTextAsync(playlistPath, Encoding.UTF8) : string.Empty;

                var result = validator.AnalyzePlaylist(playlistText, countryCode, 3);
                var country = countryList.GetCountry(countryCode);

                await WriteJsonAsync(context.Response, new
                {
                    country = result.Country,
                    displayName = country.DisplayName,
                    isMatch = result.IsTargetCountry,
                    matchedAliases = result.MatchedAliases,
                    recognizedChannelCount = result.RecognizedChannelCount,
                    threshold = result.Threshold,
                    totalChannels = country.Channels.Count,
                    playlistLength = playlistText.Length,
                    sample = playlistText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(10)
                });
                return;
            }

            if (requestPath.Equals("/api/country/save", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var country = JsonSerializer.Deserialize<CountryChannelList>(body, JsonOptions);
                    if (country == null || string.IsNullOrWhiteSpace(country.Country))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteTextAsync(context.Response, "Payload inválido", HttpStatusCode.BadRequest);
                        return;
                    }

                    var service = new CountryChannelListService(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                    service.SaveCountry(country);
                    await WriteJsonAsync(context.Response, country);
                    return;
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await WriteTextAsync(context.Response, ex.Message, HttpStatusCode.InternalServerError);
                    return;
                }
            }

            if (requestPath.Equals("/api/playlist", StringComparison.OrdinalIgnoreCase))
            {
                var mainPath = Path.Combine(outputDir, "playlist.m3u");
                if (!File.Exists(mainPath))
                {
                    await WriteTextAsync(context.Response, "Playlist não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(mainPath, Encoding.UTF8);
                await WriteTextAsync(context.Response, content, HttpStatusCode.OK, "audio/x-mpegurl");
                return;
            }

            // Pré-visualização de diagnóstico: devolve a playlist com URLs sanitizadas para que
            // a interface web nunca exponha credenciais Xtream. A playlist funcional continua
            // disponível no endpoint /api/playlist.
            if (requestPath.Equals("/api/playlist/preview", StringComparison.OrdinalIgnoreCase))
            {
                var mainPath = Path.Combine(outputDir, "playlist.m3u");
                if (!File.Exists(mainPath))
                {
                    await WriteTextAsync(context.Response, "Playlist não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(mainPath, Encoding.UTF8);
                var sanitized = CredentialSanitizer.SanitizeM3uContent(content);
                await WriteTextAsync(context.Response, sanitized, HttpStatusCode.OK, "audio/x-mpegurl");
                return;
            }

            if (requestPath.Equals("/api/playlist_temp", StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = Path.Combine(outputDir, "playlist_temp.m3u");
                if (!File.Exists(tempPath))
                {
                    await WriteTextAsync(context.Response, "Playlist temporária não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(tempPath, Encoding.UTF8);
                await WriteTextAsync(context.Response, content, HttpStatusCode.OK, "audio/x-mpegurl");
                return;
            }

            if (requestPath.Equals("/api/playlist_temp/preview", StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = Path.Combine(outputDir, "playlist_temp.m3u");
                if (!File.Exists(tempPath))
                {
                    await WriteTextAsync(context.Response, "Playlist temporária não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(tempPath, Encoding.UTF8);
                var sanitized = CredentialSanitizer.SanitizeM3uContent(content);
                await WriteTextAsync(context.Response, sanitized, HttpStatusCode.OK, "audio/x-mpegurl");
                return;
            }

            if (requestPath.Equals("/api/run-report", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "telegram_run_report.json");
                if (!File.Exists(reportPath))
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem relatório de execução disponível." });
                    return;
                }

                var report = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(reportPath, Encoding.UTF8), JsonOptions);
                await WriteJsonAsync(context.Response, report ?? new RunReport());
                return;
            }

            if (requestPath.Equals("/api/discovered-playlists", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "telegram_run_report.json");
                if (!File.Exists(reportPath))
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem relatório de execução disponível." });
                    return;
                }

                var report = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(reportPath, Encoding.UTF8), JsonOptions);
                await WriteJsonAsync(context.Response, report?.DiscoveredPlaylists ?? new List<DiscoveredPlaylist>());
                return;
            }

            // Sumário de descoberta: dedup por (source, name) e ordenação determinística.
            if (requestPath.Equals("/api/discovery/summary", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "telegram_run_report.json");
                if (!File.Exists(reportPath))
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem relatório de execução disponível." });
                    return;
                }
                var report = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(reportPath, Encoding.UTF8), JsonOptions);
                var raw = report?.DiscoveredPlaylists ?? new List<DiscoveredPlaylist>();
                var dedup = DashboardMetrics.DeduplicateBySourceName(raw);
                await WriteJsonAsync(context.Response, new
                {
                    total = raw.Count,
                    distinct = dedup.Count,
                    duplicatesCollapsed = raw.Count - dedup.Count,
                    items = dedup,
                });
                return;
            }

            // Sumário de classificação do último MatchPlan publicado.
            // Lê o ficheiro dispatcharr_plan_<ts>.json mais recente e
            // devolve as contagens por ChannelKind + uma amostra das
            // entradas excluídas (sem URLs nem credenciais).
            if (requestPath.Equals("/api/classification-summary", StringComparison.OrdinalIgnoreCase))
            {
                var planPath = LatestDispatcharrPlanPath(outputDir);
                if (planPath == null)
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem plano de classificação disponível." });
                    return;
                }

                var plan = MatchPlanSerializer.Deserialize(await File.ReadAllTextAsync(planPath, Encoding.UTF8));
                if (plan == null)
                {
                    await WriteJsonAsync(context.Response, new { error = "Plano vazio." });
                    return;
                }

                var sample = plan.ClassifiedExclusions
                    .Take(50)
                    .Select(e => new
                    {
                        title = e.Title,
                        group = e.Group,
                        kind = e.Kind.ToString(),
                        reason = e.Reason,
                    })
                    .ToList();

                await WriteJsonAsync(context.Response, new
                {
                    classification = plan.Counts.Classification,
                    excludedCount = plan.ClassifiedExclusions.Count,
                    sample = sample,
                    planGeneratedAtUtc = plan.GeneratedAtUtc,
                    planSourcePlaylistPath = plan.SourcePlaylistPath,
                });
                return;
            }

            // Run report normalizado (métricas com semântica correcta).
            if (requestPath.Equals("/api/run-report/summary", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "telegram_run_report.json");
                if (!File.Exists(reportPath))
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem relatório de execução disponível." });
                    return;
                }
                var report = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(reportPath, Encoding.UTF8), JsonOptions);
                await WriteJsonAsync(context.Response, report == null
                    ? new { error = "Relatório vazio." }
                    : DashboardMetrics.SummarizeRun(report));
                return;
            }

            // Detalhe de uma execução do histórico (1-based).
            if (requestPath.StartsWith("/api/execution/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = requestPath.Substring("/api/execution/".Length);
                if (!int.TryParse(tail, out var idx) || idx < 1)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteTextAsync(context.Response, "Índice inválido", HttpStatusCode.BadRequest);
                    return;
                }
                var history = await historyService.GetRecentAsync(TimeSpan.FromDays(365));
                if (idx > history.Count)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteTextAsync(context.Response, "Execução inexistente", HttpStatusCode.NotFound);
                    return;
                }
                await WriteJsonAsync(context.Response, history[idx - 1]);
                return;
            }

            // Estado da última sincronização Dispatcharr (lê do filesystem, sem rede).
            if (requestPath.Equals("/api/dispatcharr/state", StringComparison.OrdinalIgnoreCase))
            {
                var state = DashboardMetrics.ReadLatestDispatcharrSync(outputDir);
                if (state == null)
                {
                    await WriteJsonAsync(context.Response, new
                    {
                        enabled = false,
                        reason = "Sem plan/report encontrado em output/. Sync opt-in ou nunca correu."
                    });
                    return;
                }
                await WriteJsonAsync(context.Response, state);
                return;
            }

            // Ficheiros disponíveis na pasta de output (preview/sanity).
            // Devolve um objecto por ficheiro com {present, size, exists}, para distinguir
            // "ficheiro ausente" de "ficheiro presente mas vazio".
            if (requestPath.Equals("/api/output/inventory", StringComparison.OrdinalIgnoreCase))
            {
                var inv = new Dictionary<string, object>();
                if (Directory.Exists(outputDir))
                {
                    foreach (var name in new[] { "playlist.m3u", "playlist_temp.m3u", "telegram_run_report.json", "telegram_maintain_report.json", "import_history.json" })
                    {
                        var p = Path.Combine(outputDir, name);
                        if (File.Exists(p))
                        {
                            var fi = new FileInfo(p);
                            inv[name] = new { present = true, size = fi.Length, lastWriteUtc = fi.LastWriteTimeUtc.ToString("o") };
                        }
                        else
                        {
                            inv[name] = new { present = false, size = 0, lastWriteUtc = (string?)null };
                        }
                    }
                }
                await WriteJsonAsync(context.Response, inv);
                return;
            }

            // === PHASE 9C.4 — Live Run endpoints ===
            // Gate único já avaliado acima (UserAuth + CSRF, Bootstrap,
            // machine token). O host decide se há pipeline configurada.
            if (requestPath.Equals("/api/run/status", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRunStatusEndpointAsync(context);
                return;
            }
            if (requestPath.Equals("/api/run/start", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRunStartEndpointAsync(context);
                return;
            }

            // === Catalog API endpoints ===
            if (_catalogResolver == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await WriteJsonAsync(context.Response, new { error = "Catálogo não inicializado." });
                return;
            }

            if (requestPath.Equals("/api/catalog/stats", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, await _catalogResolver.GetStatsAsync());
                return;
            }

            if (requestPath.Equals("/api/catalog/channels", StringComparison.OrdinalIgnoreCase))
            {
                var channels = await _catalogResolver.ListCanonicalChannelsAsync();
                await WriteJsonAsync(context.Response, channels.Select(ChannelToJson).ToList());
                return;
            }

            if (requestPath.Equals("/api/catalog/identity-rules", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    var rules = await _catalogResolver.ListIdentityRulesAsync();
                    await WriteJsonAsync(context.Response, rules.Select(r => new
                    {
                        id = r.Id,
                        normalizedIdentity = r.NormalizedIdentity,
                        disposition = r.Disposition.ToString(),
                        reason = r.Reason,
                        createdAtUtc = r.CreatedAtUtc.ToString("o"),
                        updatedAtUtc = r.UpdatedAtUtc.ToString("o"),
                    }).ToList());
                    return;
                }

                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<IdentityRulePayload>(body, JsonOptions);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.NormalizedIdentity))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido: NormalizedIdentity é obrigatório." });
                            return;
                        }

                        var disposition = Enum.TryParse<RuleDisposition>(payload.Disposition, true, out var d) ? d : RuleDisposition.ReviewOnly;
                        var rule = await _catalogResolver.CreateIdentityRuleAsync(payload.NormalizedIdentity, disposition, payload.Reason ?? string.Empty);
                        await WriteJsonAsync(context.Response, new
                        {
                            id = rule.Id,
                            normalizedIdentity = rule.NormalizedIdentity,
                            disposition = rule.Disposition.ToString(),
                            reason = rule.Reason,
                            createdAtUtc = rule.CreatedAtUtc.ToString("o"),
                        }, HttpStatusCode.Created);
                        return;
                    }
                    catch (InvalidOperationException ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }

                if (context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var identity = context.Request.QueryString["identity"];
                    if (string.IsNullOrWhiteSpace(identity))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = "Parâmetro 'identity' é obrigatório." });
                        return;
                    }

                    var deleted = await _catalogResolver.DeleteIdentityRuleAsync(identity);
                    if (!deleted)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = $"Regra não encontrada: {identity}" });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new { deleted = true, identity });
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.Equals("/api/catalog/affinity-groups", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    var groups = await _catalogResolver.ListAffinityGroupsAsync();
                    await WriteJsonAsync(context.Response, groups.Select(g => new
                    {
                        id = g.Id,
                        name = g.Name,
                        kind = g.Kind.ToString(),
                        canonicalChannelKey = g.CanonicalChannelKey,
                        countryCode = g.CountryCode,
                        canonicalChannelId = g.CanonicalChannelId,
                        canonicalChannelDisplayName = g.CanonicalChannel?.DisplayName,
                        canonicalChannelCountry = g.CanonicalChannel?.Country,
                        members = g.Members.Select(m => m.NormalizedMember).ToList(),
                        createdAtUtc = g.CreatedAtUtc.ToString("o"),
                        updatedAtUtc = g.UpdatedAtUtc.ToString("o"),
                    }).ToList());
                    return;
                }

                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<AffinityGroupPayload>(body, JsonOptions);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido: Name é obrigatório." });
                            return;
                        }
                        if (payload.Members == null || payload.Members.Count == 0)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Members é obrigatório e não pode estar vazio." });
                            return;
                        }

                        var resolved = await ResolveAffinityPayloadAsync(payload);
                        if (resolved.Error != null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = resolved.Error });
                            return;
                        }
                        var group = await _catalogResolver.CreateAffinityGroupAsync(
                            payload.Name, resolved.Kind, resolved.Key, resolved.Country, payload.Members);
                        await WriteJsonAsync(context.Response, new
                        {
                            id = group.Id,
                            name = group.Name,
                            kind = group.Kind.ToString(),
                            canonicalChannelKey = group.CanonicalChannelKey,
                            countryCode = group.CountryCode,
                            canonicalChannelId = group.CanonicalChannelId,
                            members = group.Members.Select(m => m.NormalizedMember).ToList(),
                            createdAtUtc = group.CreatedAtUtc.ToString("o"),
                        }, HttpStatusCode.Created);
                        return;
                    }
                    catch (InvalidOperationException ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }

                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.StartsWith("/api/catalog/affinity-groups/", StringComparison.OrdinalIgnoreCase)
                && requestPath.EndsWith("/delete", StringComparison.OrdinalIgnoreCase))
            {
                var idStr = requestPath.Substring("/api/catalog/affinity-groups/".Length);
                idStr = idStr[..^"/delete".Length];
                if (!long.TryParse(idStr, out var groupId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                    return;
                }

                var deleted = await _catalogResolver.DeleteAffinityGroupAsync(groupId);
                if (!deleted)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteJsonAsync(context.Response, new { error = "Grupo não encontrado." });
                    return;
                }
                await WriteJsonAsync(context.Response, new { deleted = true, id = groupId });
                return;
            }

            if (requestPath.StartsWith("/api/catalog/affinity-groups/", StringComparison.OrdinalIgnoreCase)
                && !requestPath.EndsWith("/delete", StringComparison.OrdinalIgnoreCase))
            {
                var idStr = requestPath.Substring("/api/catalog/affinity-groups/".Length);
                if (!long.TryParse(idStr, out var groupId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                    return;
                }

                if (context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                    || context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<AffinityGroupPayload>(body, JsonOptions);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido: Name é obrigatório." });
                            return;
                        }
                        if (payload.Members == null || payload.Members.Count == 0)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Members é obrigatório e não pode estar vazio." });
                            return;
                        }

                        var resolved = await ResolveAffinityPayloadAsync(payload);
                        if (resolved.Error != null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = resolved.Error });
                            return;
                        }
                        var group = await _catalogResolver.UpdateAffinityGroupAsync(
                            groupId, payload.Name, resolved.Kind, resolved.Key, resolved.Country, payload.Members);
                        if (group == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            await WriteJsonAsync(context.Response, new { error = "Grupo não encontrado." });
                            return;
                        }
                        await WriteJsonAsync(context.Response, new
                        {
                            id = group.Id,
                            name = group.Name,
                            kind = group.Kind.ToString(),
                            canonicalChannelKey = group.CanonicalChannelKey,
                            countryCode = group.CountryCode,
                            canonicalChannelId = group.CanonicalChannelId,
                            members = group.Members.Select(m => m.NormalizedMember).ToList(),
                            updatedAtUtc = group.UpdatedAtUtc.ToString("o"),
                        });
                        return;
                    }
                    catch (InvalidOperationException ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }

                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.Equals("/api/catalog/reviews", StringComparison.OrdinalIgnoreCase))
            {
                var reviews = await _catalogResolver.ListAllReviewItemsAsync();
                await WriteJsonAsync(context.Response, reviews.Select(r => new
                {
                    id = r.Id,
                    fingerprint = r.Fingerprint,
                    normalizedIdentity = r.NormalizedIdentity,
                    sourceGroup = r.SourceGroup,
                    reasonSignature = r.ReasonSignature,
                    state = r.State.ToString(),
                    note = r.Note,
                    approvedCanonicalChannelId = r.ApprovedCanonicalChannelId,
                    createdAtUtc = r.CreatedAtUtc.ToString("o"),
                    updatedAtUtc = r.UpdatedAtUtc.ToString("o"),
                    resolvedAtUtc = r.ResolvedAtUtc?.ToString("o"),
                }).ToList());
                return;
            }

            if (requestPath.StartsWith("/api/catalog/reviews/", StringComparison.OrdinalIgnoreCase))
            {
                var fingerprint = requestPath.Substring("/api/catalog/reviews/".Length);
                if (string.IsNullOrWhiteSpace(fingerprint))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "Fingerprint é obrigatório." });
                    return;
                }

                if (requestPath.EndsWith("/approve", StringComparison.OrdinalIgnoreCase))
                {
                    long? approvedId = null;
                    if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                            var body = await reader.ReadToEndAsync();
                            var payload = JsonSerializer.Deserialize<ApproveReviewPayload>(body, JsonOptions);
                            approvedId = payload?.ApprovedCanonicalChannelId;
                        }
                        catch { }
                    }

                    var approved = await _catalogResolver.ApproveReviewAsync(fingerprint, approvedId);
                    if (approved == null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = "Review item não encontrado." });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new
                    {
                        fingerprint = approved.Fingerprint,
                        state = approved.State.ToString(),
                        resolvedAtUtc = approved.ResolvedAtUtc?.ToString("o"),
                    });
                    return;
                }

                if (requestPath.EndsWith("/exclude", StringComparison.OrdinalIgnoreCase))
                {
                    var excluded = await _catalogResolver.ExcludeReviewAsync(fingerprint);
                    if (excluded == null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = "Review item não encontrado." });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new
                    {
                        fingerprint = excluded.Fingerprint,
                        state = excluded.State.ToString(),
                        resolvedAtUtc = excluded.ResolvedAtUtc?.ToString("o"),
                    });
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (requestPath.Equals("/api/catalog/sync-runs", StringComparison.OrdinalIgnoreCase))
            {
                var runs = await _catalogResolver.ListSyncRunsAsync();
                await WriteJsonAsync(context.Response, runs.Select(r => new
                {
                    id = r.Id,
                    startedAtUtc = r.StartedAtUtc.ToString("o"),
                    finishedAtUtc = r.FinishedAtUtc.ToString("o"),
                    appVersion = r.AppVersion,
                    countCreatedCrawlerManaged = r.CountCreatedCrawlerManaged,
                    countMergedIntoExternal = r.CountMergedIntoExternal,
                    countProtectedExternalStreams = r.CountProtectedExternalStreams,
                    countRemovedCrawlerManagedStreams = r.CountRemovedCrawlerManagedStreams,
                    countReviewRequired = r.CountReviewRequired,
                    countExcluded = r.CountExcluded,
                    result = r.Result,
                }).ToList());
                return;
            }

            // PHASE 11 — Per-step breakdown de uma SyncRun.
            // GET /api/catalog/sync-runs/{id}/steps
            // POST /api/catalog/sync-runs/{id}/steps
            if (requestPath.StartsWith("/api/catalog/sync-runs/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = requestPath.Substring("/api/catalog/sync-runs/".Length);
                var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 2 && segments[1].Equals("steps", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(segments[0], out var runId))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                        return;
                    }
                    if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        var steps = await _catalogResolver.GetSyncRunStepsAsync(runId);
                        await WriteJsonAsync(context.Response, steps.Select(SyncRunStepToJson));
                        return;
                    }
                    if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                            var body = await reader.ReadToEndAsync();
                            var payload = JsonSerializer.Deserialize<SyncRunStepPayload>(body, JsonOptions);
                            if (payload == null || string.IsNullOrWhiteSpace(payload.Step))
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                                return;
                            }
                            var started = payload.StartedAtUtc ?? DateTime.UtcNow;
                            var finished = payload.FinishedAtUtc ?? DateTime.UtcNow;
                            var saved = await _catalogResolver.RecordSyncRunStepAsync(
                                runId, payload.Step, started, finished,
                                payload.ItemsProcessed, payload.ItemsSucceeded, payload.ItemsFailed,
                                payload.Result ?? "ok");
                            await WriteJsonAsync(context.Response, SyncRunStepToJson(saved));
                            return;
                        }
                        catch (Exception ex)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = ex.Message });
                            return;
                        }
                    }
                }
            }

            // Pending country approvals
            if (requestPath.Equals("/api/catalog/pending-country-approvals", StringComparison.OrdinalIgnoreCase))
            {
                var pending = await _catalogResolver.ListPendingCountryApprovalsAsync();
                await WriteJsonAsync(context.Response, pending.Select(r => new
                {
                    id = r.Id,
                    normalizedIdentity = r.NormalizedIdentity,
                    originalTitle = r.OriginalTitle,
                    countryCode = r.CountryCode,
                    streamUrl = r.StreamUrl,
                    sourceGroup = r.SourceGroup,
                    reasonSignature = r.ReasonSignature,
                    state = r.State.ToString(),
                    createdAtUtc = r.CreatedAtUtc.ToString("o"),
                    updatedAtUtc = r.UpdatedAtUtc.ToString("o"),
                    resolvedAtUtc = r.ResolvedAtUtc?.ToString("o"),
                }).ToList());
                return;
            }

            if (requestPath.StartsWith("/api/catalog/pending-country-approvals/", StringComparison.OrdinalIgnoreCase))
            {
                var idStr = requestPath.Substring("/api/catalog/pending-country-approvals/".Length);
                if (!long.TryParse(idStr, out var pendingId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                    return;
                }

                if (requestPath.EndsWith("/approve", StringComparison.OrdinalIgnoreCase))
                {
                    var approved = await _catalogResolver.ApprovePendingCountryApprovalAsync(pendingId);
                    if (approved == null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = "Pending approval não encontrado." });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new
                    {
                        id = approved.Id,
                        state = approved.State.ToString(),
                        resolvedAtUtc = approved.ResolvedAtUtc?.ToString("o"),
                    });
                    return;
                }

                if (requestPath.EndsWith("/reject", StringComparison.OrdinalIgnoreCase))
                {
                    var rejected = await _catalogResolver.RejectPendingCountryApprovalAsync(pendingId);
                    if (rejected == null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = "Pending approval não encontrado." });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new
                    {
                        id = rejected.Id,
                        state = rejected.State.ToString(),
                        resolvedAtUtc = rejected.ResolvedAtUtc?.ToString("o"),
                    });
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // === PHASE 5 — Ordering Lists API ===
            // GET    /api/catalog/ordering-lists
            // POST   /api/catalog/ordering-lists
            // GET    /api/catalog/ordering-lists/{id}
            // POST   /api/catalog/ordering-lists/{id}/duplicate
            // DELETE /api/catalog/ordering-lists/{id}
            // POST   /api/catalog/ordering-lists/{id}/items
            // PUT    /api/catalog/ordering-items/{id}
            // DELETE /api/catalog/ordering-items/{id}
            // GET    /api/catalog/ordering-lists/{id}/preview
            if (requestPath.Equals("/api/catalog/ordering-lists", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context.Response, (await _catalogResolver.ListOrderingListsAsync()).Select(OrderingListSummaryToJson));
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<OrderingListPayload>(body, JsonOptions);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.Key) || string.IsNullOrWhiteSpace(payload.Name))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido: Key e Name são obrigatórios." });
                            return;
                        }
                        var list = await _catalogResolver.CreateOrderingListAsync(
                            payload.Key, payload.Name, payload.Country, payload.Description, payload.IsEnabled);
                        await WriteJsonAsync(context.Response, OrderingListSummaryToJson(list), HttpStatusCode.Created);
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.StartsWith("/api/catalog/ordering-lists/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = requestPath.Substring("/api/catalog/ordering-lists/".Length);
                var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }
                if (!long.TryParse(segments[0], out var listId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                    return;
                }

                if (segments.Length == 1)
                {
                    if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        var list = await _catalogResolver.GetOrderingListAsync(listId, includeItems: true);
                        if (list == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            await WriteJsonAsync(context.Response, new { error = $"OrderingList #{listId} não encontrada." });
                            return;
                        }
                        await WriteJsonAsync(context.Response, OrderingListDetailToJson(list));
                        return;
                    }
                    if (context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        var ok = await _catalogResolver.DeleteOrderingListAsync(listId);
                        if (!ok)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            await WriteJsonAsync(context.Response, new { error = $"OrderingList #{listId} não encontrada." });
                            return;
                        }
                        await WriteJsonAsync(context.Response, new { deleted = true, id = listId });
                        return;
                    }
                }

                if (segments.Length == 2 && segments[1].Equals("duplicate", StringComparison.OrdinalIgnoreCase)
                    && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<OrderingListDuplicatePayload>(body, JsonOptions);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.NewKey))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido: newKey obrigatório." });
                            return;
                        }
                        var clone = await _catalogResolver.DuplicateOrderingListAsync(listId, payload.NewKey, payload.NewName);
                        await WriteJsonAsync(context.Response, OrderingListSummaryToJson(clone), HttpStatusCode.Created);
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }

                if (segments.Length == 2 && segments[1].Equals("items", StringComparison.OrdinalIgnoreCase)
                    && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<OrderingItemAddPayload>(body, JsonOptions);
                        if (payload == null || payload.CanonicalChannelId <= 0)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido: canonicalChannelId obrigatório." });
                            return;
                        }
                        var item = await _catalogResolver.AddOrderingItemAsync(
                            listId, payload.CanonicalChannelId, payload.Position, payload.IsEnabled);
                        await WriteJsonAsync(context.Response, OrderingItemToJson(item), HttpStatusCode.Created);
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }

                if (segments.Length == 2 && segments[1].Equals("preview", StringComparison.OrdinalIgnoreCase)
                    && context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    var composer = new PlaylistComposerService(_catalogResolver.GetFactory());
                    var composition = await composer.ComposeAsync(listId);
                    await WriteJsonAsync(context.Response, PlaylistCompositionToJson(composition));
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (requestPath.StartsWith("/api/catalog/ordering-items/", StringComparison.OrdinalIgnoreCase))
            {
                var idStr = requestPath.Substring("/api/catalog/ordering-items/".Length);
                if (!long.TryParse(idStr, out var itemId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                    return;
                }

                if (context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await _catalogResolver.RemoveOrderingItemAsync(itemId);
                    if (!ok)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = $"OrderingItem #{itemId} não encontrado." });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new { deleted = true, id = itemId });
                    return;
                }
                if (context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<OrderingItemUpdatePayload>(body, JsonOptions);
                        if (payload == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                            return;
                        }
                        if (payload.Position.HasValue)
                        {
                            await _catalogResolver.MoveOrderingItemAsync(itemId, payload.Position.Value);
                        }
                        if (payload.IsEnabled.HasValue)
                        {
                            await _catalogResolver.SetOrderingItemEnabledAsync(itemId, payload.IsEnabled.Value);
                        }
                        await WriteJsonAsync(context.Response, new { updated = true, id = itemId });
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            // === PHASE 6 — Source Priority API ===
            // GET  /api/catalog/priority-policies?channelId={id}
            // POST /api/catalog/priority-policies
            if (requestPath.Equals("/api/catalog/priority-policies", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    var global = await _catalogResolver.GetOrCreateGlobalPriorityPolicyAsync();
                    object result = global;
                    var channelIdRaw = context.Request.QueryString["channelId"];
                    if (!string.IsNullOrWhiteSpace(channelIdRaw) && long.TryParse(channelIdRaw, out var chId))
                    {
                        var per = await _catalogResolver.GetChannelPriorityPolicyAsync(chId);
                        if (per != null) result = per;
                    }
                    await WriteJsonAsync(context.Response, result);
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<PriorityPolicyPayload>(body, JsonOptions);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.Scope))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                            return;
                        }
                        var saved = await _catalogResolver.UpsertPriorityPolicyAsync(
                            payload.Scope, payload.CanonicalChannelId,
                            payload.CriteriaJson ?? "[]", payload.PreferredQuality ?? "",
                            payload.AllowFallback);
                        await WriteJsonAsync(context.Response, PriorityPolicyToJson(saved));
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            // === PHASE 13 (Wave 13-4) — Global Source Selection Policy API ===
            // GET  /api/catalog/source-selection-policies
            // POST /api/catalog/source-selection-policies
            if (requestPath.Equals("/api/catalog/source-selection-policies", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    var global = await _catalogResolver.GetOrCreateGlobalSourceSelectionPolicyAsync();
                    await WriteJsonAsync(context.Response, SourceSelectionPolicyToJson(global));
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<SourceSelectionPolicyPayload>(body, JsonOptions);
                        if (payload == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.MaxSourcesPerChannel is null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "maxSourcesPerChannel é obrigatório." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.MaxSourcesPerChannel.Value < 0)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "maxSourcesPerChannel não pode ser negativo." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.PreferDistinctProviders is null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "preferDistinctProviders é obrigatório." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.AllowFallbackToSameProvider is null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "allowFallbackToSameProvider é obrigatório." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.MaxSourcesPerProvider is <= 0)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "maxSourcesPerProvider deve ser >= 1 ou ausente/null para sem limite." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        var saved = await _catalogResolver.UpsertGlobalSourceSelectionPolicyAsync(
                            payload.MaxSourcesPerChannel.Value,
                            payload.PreferDistinctProviders.Value,
                            payload.MaxSourcesPerProvider,
                            payload.AllowFallbackToSameProvider.Value);
                        await WriteJsonAsync(context.Response, SourceSelectionPolicyToJson(saved));
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message }, HttpStatusCode.BadRequest);
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            // === PHASE 13 (Wave 13-4b) — Per-channel Source Selection Policy API ===
            // GET    /api/catalog/source-selection-policies/channels
            // POST   /api/catalog/source-selection-policies/channels
            // GET    /api/catalog/source-selection-policies/channels/{key}
            // DELETE /api/catalog/source-selection-policies/channels/{key}
            //
            // A identidade é a chave canónica pública (CanonicalChannel.Key).
            // O CanonicalChannelId nunca é exposto nem aceite aqui.
            const string channelSelectionPoliciesPath = "/api/catalog/source-selection-policies/channels";
            if (requestPath.Equals(channelSelectionPoliciesPath, StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    var overrides = await _catalogResolver.ListChannelSourceSelectionPoliciesAsync();
                    await WriteJsonAsync(context.Response, new
                    {
                        overrides = overrides.Select(ChannelSourceSelectionPolicyToJson).ToList(),
                    });
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<ChannelSourceSelectionPolicyPayload>(body, JsonOptions);
                        if (payload == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        var canonicalChannelKey = payload.CanonicalChannelKey?.Trim();
                        if (string.IsNullOrEmpty(canonicalChannelKey))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "canonicalChannelKey é obrigatório." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.MaxSourcesPerChannel is null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "maxSourcesPerChannel é obrigatório." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.MaxSourcesPerChannel.Value < 0)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "maxSourcesPerChannel não pode ser negativo." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.PreferDistinctProviders is null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "preferDistinctProviders é obrigatório." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.AllowFallbackToSameProvider is null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "allowFallbackToSameProvider é obrigatório." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        if (payload.MaxSourcesPerProvider is <= 0)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "maxSourcesPerProvider deve ser >= 1 ou ausente/null para sem limite." }, HttpStatusCode.BadRequest);
                            return;
                        }
                        var saved = await _catalogResolver.UpsertChannelSourceSelectionPolicyAsync(
                            canonicalChannelKey,
                            payload.MaxSourcesPerChannel.Value,
                            payload.PreferDistinctProviders.Value,
                            payload.MaxSourcesPerProvider,
                            payload.AllowFallbackToSameProvider.Value);
                        await WriteJsonAsync(context.Response, ChannelSourceSelectionPolicyToJson(saved));
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message }, HttpStatusCode.BadRequest);
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.StartsWith(channelSelectionPoliciesPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                var canonicalChannelKey = Uri.UnescapeDataString(
                    requestPath.Substring((channelSelectionPoliciesPath + "/").Length));

                if (string.IsNullOrWhiteSpace(canonicalChannelKey))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteJsonAsync(context.Response, new { error = "Override não encontrado." }, HttpStatusCode.NotFound);
                    return;
                }

                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    var existing = await _catalogResolver.GetChannelSourceSelectionPolicyAsync(canonicalChannelKey);
                    if (existing == null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = "Override não encontrado." }, HttpStatusCode.NotFound);
                        return;
                    }
                    await WriteJsonAsync(context.Response, ChannelSourceSelectionPolicyToJson(existing));
                    return;
                }
                if (context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var deleted = await _catalogResolver.DeleteChannelSourceSelectionPolicyAsync(canonicalChannelKey);
                    if (!deleted)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = "Override não encontrado." }, HttpStatusCode.NotFound);
                        return;
                    }
                    await WriteJsonAsync(context.Response, new { deleted = true });
                    return;
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            // === PHASE 9 — Stream degradation dashboard ===
            // GET /api/catalog/degradation/recent?lookbackMinutes={n}&limit={n}
            // GET /api/catalog/degradation/stats?lookbackMinutes={n}
            if (requestPath.Equals("/api/catalog/degradation/recent", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                int lookbackMinutes = 60 * 24 * 7;
                int limit = 200;
                var rawLookback = context.Request.QueryString["lookbackMinutes"];
                if (!string.IsNullOrWhiteSpace(rawLookback) && int.TryParse(rawLookback, out var pl))
                {
                    lookbackMinutes = pl;
                }
                var rawLimit = context.Request.QueryString["limit"];
                if (!string.IsNullOrWhiteSpace(rawLimit) && int.TryParse(rawLimit, out var pl2))
                {
                    limit = pl2;
                }
                var list = await _catalogResolver.GetDegradedStreamsAsync(lookbackMinutes, limit);
                await WriteJsonAsync(context.Response, list.Select(DegradedStreamToJson));
                return;
            }
            if (requestPath.Equals("/api/catalog/degradation/stats", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                int lookbackMinutes = 60 * 24;
                var rawLookback = context.Request.QueryString["lookbackMinutes"];
                if (!string.IsNullOrWhiteSpace(rawLookback) && int.TryParse(rawLookback, out var pl))
                {
                    lookbackMinutes = pl;
                }
                var stats = await _catalogResolver.GetDegradationStatsAsync(lookbackMinutes);
                await WriteJsonAsync(context.Response, stats);
                return;
            }

            // === PHASE 12 — Scheduled Jobs API ===
            // GET    /api/scheduled-actions       — lista as IScheduledAction disponíveis
            // GET    /api/catalog/scheduled-jobs
            // POST   /api/catalog/scheduled-jobs
            // PUT    /api/catalog/scheduled-jobs/{id}/enabled
            // DELETE /api/catalog/scheduled-jobs/{id}
            if (requestPath.Equals("/api/scheduled-actions", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var names = _scheduledActions is null
                    ? Array.Empty<string>()
                    : _scheduledActions.Select(a => a.Name).ToArray();
                await WriteJsonAsync(context.Response, names);
                return;
            }
            if (requestPath.Equals("/api/catalog/scheduled-jobs", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context.Response, (await _catalogResolver.ListScheduledJobsAsync()).Select(ScheduledJobToJson));
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<ScheduledJobPayload>(body, JsonOptions);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.Name) || string.IsNullOrWhiteSpace(payload.CronExpression) || string.IsNullOrWhiteSpace(payload.ActionName))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido: Name, CronExpression e ActionName obrigatórios." });
                            return;
                        }
                        var saved = await _catalogResolver.UpsertScheduledJobAsync(
                            payload.Name, payload.CronExpression, payload.ActionName, payload.IsEnabled);
                        await WriteJsonAsync(context.Response, ScheduledJobToJson(saved));
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }
            if (requestPath.StartsWith("/api/catalog/scheduled-jobs/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = requestPath.Substring("/api/catalog/scheduled-jobs/".Length);
                var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length != 2 || segments[1] != "enabled")
                {
                    // DELETE
                    if (segments.Length == 1 && context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!long.TryParse(segments[0], out var jid))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                            return;
                        }
                        var ok = await _catalogResolver.DeleteScheduledJobAsync(jid);
                        if (!ok)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            return;
                        }
                        await WriteJsonAsync(context.Response, new { deleted = true, id = jid });
                        return;
                    }
                }
                else if (segments.Length == 2 && context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(segments[0], out var jid))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return;
                    }
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<ScheduledJobEnablePayload>(body, JsonOptions);
                        if (payload == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            return;
                        }
                        var ok = await _catalogResolver.SetScheduledJobEnabledAsync(jid, payload.IsEnabled);
                        if (!ok)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            return;
                        }
                        await WriteJsonAsync(context.Response, new { updated = true, id = jid, isEnabled = payload.IsEnabled });
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
            }

            // === PHASE 3 — Matching observability ===
            // GET /api/catalog/matching/recent?channelId={id}&limit={n}
            // GET /api/catalog/matching/stats
            if (requestPath.Equals("/api/catalog/matching/recent", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                long? channelId = null;
                int limit = 100;
                var rawChannel = context.Request.QueryString["channelId"];
                if (!string.IsNullOrWhiteSpace(rawChannel) && long.TryParse(rawChannel, out var parsed))
                {
                    channelId = parsed;
                }
                var rawLimit = context.Request.QueryString["limit"];
                if (!string.IsNullOrWhiteSpace(rawLimit) && int.TryParse(rawLimit, out var parsedLimit))
                {
                    limit = parsedLimit;
                }
                var list = await _catalogResolver.GetRecentMatchingAuditsAsync(limit, channelId);
                await WriteJsonAsync(context.Response, list.Select(MatchingAuditToJson));
                return;
            }

            if (requestPath.Equals("/api/catalog/matching/stats", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var stats = await _catalogResolver.GetMatchingAuditStatsAsync();
                await WriteJsonAsync(context.Response, stats);
                return;
            }

            // === PHASE 8 — Import Policies + Canonical Groups + Group Mappings ===
            // GET    /api/catalog/import-policies
            // POST   /api/catalog/import-policies
            // GET    /api/catalog/canonical-groups
            // POST   /api/catalog/canonical-groups
            // DELETE /api/catalog/canonical-groups/{id}
            // GET    /api/catalog/group-mappings
            // POST   /api/catalog/group-mappings
            // DELETE /api/catalog/group-mappings/{id}
            if (requestPath.Equals("/api/catalog/import-policies", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context.Response, (await _catalogResolver.ListImportPoliciesAsync()).Select(ImportPolicyToJson));
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<ImportPolicyPayload>(body, JsonOptions);
                        if (payload == null || !Enum.TryParse<MediaKind>(payload.MediaKind, true, out var kind))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                            return;
                        }
                        if (!Enum.TryParse<VodPolicy>(payload.VodPolicy ?? "ExcludeVod", true, out var vod))
                        {
                            vod = VodPolicy.ExcludeVod;
                        }
                        var saved = await _catalogResolver.UpsertImportPolicyAsync(
                            kind, vod, payload.TargetGroupsCsv ?? string.Empty,
                            payload.ExcludedGroupsCsv ?? string.Empty, payload.IsEnabled);
                        await WriteJsonAsync(context.Response, ImportPolicyToJson(saved));
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.Equals("/api/catalog/canonical-groups", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context.Response, (await _catalogResolver.ListCanonicalGroupsAsync()).Select(CanonicalGroupToJson));
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<CanonicalGroupPayload>(body, JsonOptions);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.Key) || string.IsNullOrWhiteSpace(payload.DisplayName))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido: Key e DisplayName obrigatórios." });
                            return;
                        }
                        var saved = await _catalogResolver.UpsertCanonicalGroupAsync(
                            payload.Key, payload.DisplayName, payload.Country,
                            payload.Order, payload.IsEnabled, payload.IsDefault);
                        await WriteJsonAsync(context.Response, CanonicalGroupToJson(saved));
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.StartsWith("/api/catalog/canonical-groups/", StringComparison.OrdinalIgnoreCase))
            {
                var idStr = requestPath.Substring("/api/catalog/canonical-groups/".Length);
                if (!long.TryParse(idStr, out var gid))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                    return;
                }
                if (context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await _catalogResolver.DeleteCanonicalGroupAsync(gid);
                    if (!ok)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = $"CanonicalGroup #{gid} não encontrada." });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new { deleted = true, id = gid });
                    return;
                }
            }

            if (requestPath.Equals("/api/catalog/group-mappings", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context.Response, (await _catalogResolver.ListGroupMappingsAsync()).Select(GroupMappingToJson));
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<GroupMappingPayload>(body, JsonOptions);
                        if (payload == null
                            || !Enum.TryParse<SourceKind>(payload.SourceKind, true, out var sk)
                            || string.IsNullOrWhiteSpace(payload.SourceGroupTitle)
                            || payload.CanonicalGroupId <= 0)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                            return;
                        }
                        var saved = await _catalogResolver.UpsertGroupMappingAsync(
                            sk, payload.SourceGroupTitle, payload.CanonicalGroupId, payload.IsEnabled);
                        await WriteJsonAsync(context.Response, GroupMappingToJson(saved));
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.StartsWith("/api/catalog/group-mappings/", StringComparison.OrdinalIgnoreCase))
            {
                var idStr = requestPath.Substring("/api/catalog/group-mappings/".Length);
                if (!long.TryParse(idStr, out var mid))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                    return;
                }
                if (context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await _catalogResolver.DeleteGroupMappingAsync(mid);
                    if (!ok)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = $"GroupMapping #{mid} não encontrado." });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new { deleted = true, id = mid });
                    return;
                }
            }

            // === PHASE 9 b — ChannelSource observation history ===
            // GET /api/catalog/channel-sources/{id}/observations?limit={n}
            // POST /api/catalog/channel-sources/{id}/observations
            if (requestPath.StartsWith("/api/catalog/channel-sources/", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var tail = requestPath.Substring("/api/catalog/channel-sources/".Length);
                var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 2 && segments[1].Equals("observations", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(segments[0], out var csId))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                        return;
                    }
                    int limit = 200;
                    var rawLimit = context.Request.QueryString["limit"];
                    if (!string.IsNullOrWhiteSpace(rawLimit) && int.TryParse(rawLimit, out var parsed))
                    {
                        limit = parsed;
                    }
                    var list = await _catalogResolver.GetChannelSourceObservationsAsync(csId, limit);
                    await WriteJsonAsync(context.Response, list.Select(ChannelSourceObservationToJson));
                    return;
                }
            }
            if (requestPath.StartsWith("/api/catalog/channel-sources/", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var tail = requestPath.Substring("/api/catalog/channel-sources/".Length);
                var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 2 && segments[1].Equals("observations", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(segments[0], out var csId))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                        return;
                    }
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<ChannelSourceObservationPayload>(body, JsonOptions);
                        if (payload == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                            return;
                        }
                        if (!Enum.TryParse<StreamQuality>(payload.Quality, true, out var quality)) quality = StreamQuality.Unknown;
                        if (!Enum.TryParse<EpgState>(payload.Epg, true, out var epg)) epg = EpgState.Unknown;
                        if (!Enum.TryParse<AvailabilityState>(payload.Availability, true, out var availability)) availability = AvailabilityState.Discovered;
                        await _catalogResolver.RecordChannelSourceObservationAsync(csId, quality, epg, availability, payload.ResponseTimeMs);
                        await WriteJsonAsync(context.Response, new { recorded = true });
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
            }

            // === PHASE 4 — Sources & ChannelSources API ===
            // GET    /api/catalog/sources
            // POST   /api/catalog/sources
            // DELETE /api/catalog/sources/{id}
            // GET    /api/catalog/sources/{id}/streams?channelId={id}
            // POST   /api/catalog/sources/{id}/streams
            // DELETE /api/catalog/channel-sources/{id}
            // PUT    /api/catalog/channel-sources/{id}
            if (requestPath.Equals("/api/catalog/sources", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context.Response, (await _catalogResolver.ListSourcesAsync()).Select(SourceToJson));
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<SourcePayload>(body, JsonOptions);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.Key) || string.IsNullOrWhiteSpace(payload.Name))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido: Key e Name são obrigatórios." });
                            return;
                        }
                        if (!Enum.TryParse<SourceKind>(payload.Kind, true, out var kind))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = $"Kind inválido: '{payload.Kind}'." });
                            return;
                        }
                        var source = await _catalogResolver.EnsureSourceAsync(
                            payload.Key, payload.Name, kind, payload.Origin ?? string.Empty,
                            payload.Priority, payload.IsEnabled);
                        await WriteJsonAsync(context.Response, SourceToJson(source), HttpStatusCode.Created);
                        return;
                    }
                    catch (ArgumentException ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.StartsWith("/api/catalog/sources/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = requestPath.Substring("/api/catalog/sources/".Length);
                var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }
                if (!long.TryParse(segments[0], out var sourceId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID de source inválido." });
                    return;
                }

                if (segments.Length == 1 && context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await _catalogResolver.DeleteSourceAsync(sourceId);
                    if (!ok)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = $"Source #{sourceId} não encontrada." });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new { deleted = true, id = sourceId });
                    return;
                }

                // /api/catalog/sources/{id}/streams
                if (segments.Length >= 2 && segments[1].Equals("streams", StringComparison.OrdinalIgnoreCase))
                {
                    if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        long? channelId = null;
                        var rawChannel = context.Request.QueryString["channelId"];
                        if (!string.IsNullOrWhiteSpace(rawChannel) && long.TryParse(rawChannel, out var parsed))
                        {
                            channelId = parsed;
                        }
                        var streams = await _catalogResolver.ListChannelSourcesAsync(canonicalChannelId: channelId, sourceId: sourceId);
                        await WriteJsonAsync(context.Response, streams.Select(ChannelSourceToJson));
                        return;
                    }
                    if (segments.Length == 2 && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                            var body = await reader.ReadToEndAsync();
                            var payload = JsonSerializer.Deserialize<ChannelSourcePayload>(body, JsonOptions);
                            if (payload == null || payload.CanonicalChannelId <= 0 || string.IsNullOrWhiteSpace(payload.StreamUrl))
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                                return;
                            }
                            if (!Enum.TryParse<StreamQuality>(payload.Quality, true, out var quality)) quality = StreamQuality.Unknown;
                            if (!Enum.TryParse<EpgState>(payload.Epg, true, out var epg)) epg = EpgState.Unknown;
                            if (!Enum.TryParse<AvailabilityState>(payload.Availability, true, out var availability)) availability = AvailabilityState.Discovered;
                            var cs = await _catalogResolver.RecordChannelSourceAsync(
                                payload.CanonicalChannelId, sourceId, payload.StreamUrl,
                                quality, epg, availability, payload.MatchConfidence,
                                payload.MatchMethod ?? "unknown",
                                payload.ExternalStreamId,
                                payload.IsEnabled);
                            await WriteJsonAsync(context.Response, ChannelSourceToJson(cs), HttpStatusCode.Created);
                            return;
                        }
                        catch (ArgumentException ex)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = ex.Message });
                            return;
                        }
                    }
                }

                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (requestPath.StartsWith("/api/catalog/channel-sources/", StringComparison.OrdinalIgnoreCase))
            {
                var idStr = requestPath.Substring("/api/catalog/channel-sources/".Length);
                if (!long.TryParse(idStr, out var csId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID inválido." });
                    return;
                }
                if (context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await _catalogResolver.DeleteChannelSourceAsync(csId);
                    if (!ok)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = $"ChannelSource #{csId} não encontrada." });
                        return;
                    }
                    await WriteJsonAsync(context.Response, new { deleted = true, id = csId });
                    return;
                }
                if (context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<ChannelSourceUpdatePayload>(body, JsonOptions);
                        if (payload == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                            return;
                        }
                        var ok = await _catalogResolver.SetChannelSourceEnabledAsync(csId, payload.IsEnabled);
                        if (!ok)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            await WriteJsonAsync(context.Response, new { error = $"ChannelSource #{csId} não encontrada." });
                            return;
                        }
                        await WriteJsonAsync(context.Response, new { updated = true, id = csId, isEnabled = payload.IsEnabled });
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            // === Channels admin API (PHASE 2 — Dashboard Control Plane) ===
            // GET    /api/catalog/channels/{id}
            // POST   /api/catalog/channels
            // PUT    /api/catalog/channels/{id}
            // DELETE /api/catalog/channels/{id}
            // POST   /api/catalog/channels/{id}/aliases
            // DELETE /api/catalog/channels/{id}/aliases/{alias}
            if (requestPath.StartsWith("/api/catalog/channels/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = requestPath.Substring("/api/catalog/channels/".Length);
                var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteJsonAsync(context.Response, new { error = "Path incompleto." });
                    return;
                }
                if (!long.TryParse(segments[0], out var channelId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = "ID de canal inválido." });
                    return;
                }

                // /api/catalog/channels/{id} (singular)
                if (segments.Length == 1)
                {
                    if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        var ch = await _catalogResolver.GetCanonicalChannelAsync(channelId);
                        if (ch == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            await WriteJsonAsync(context.Response, new { error = $"Canal #{channelId} não encontrado." });
                            return;
                        }
                        await WriteJsonAsync(context.Response, ChannelToJson(ch));
                        return;
                    }

                    if (context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                            var body = await reader.ReadToEndAsync();
                            var payload = JsonSerializer.Deserialize<UpdateChannelPayload>(body, JsonOptions);
                            if (payload == null)
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                                return;
                            }
                            if (string.IsNullOrWhiteSpace(payload.DisplayName))
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteJsonAsync(context.Response, new { error = "DisplayName é obrigatório." });
                                return;
                            }
                            if (!Enum.TryParse<EditorialCategory>(payload.EditorialCategory, true, out var editorialCategory))
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteJsonAsync(context.Response, new { error = $"EditorialCategory inválido: '{payload.EditorialCategory}'." });
                                return;
                            }
                            if (!Enum.TryParse<CanonicalEditorialGroup>(payload.EditorialGroup, true, out var editorialGroup))
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteJsonAsync(context.Response, new { error = $"EditorialGroup inválido: '{payload.EditorialGroup}'." });
                                return;
                            }
                            if (!Enum.TryParse<PublicationPolicy>(payload.PublicationPolicy, true, out var publicationPolicy))
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteJsonAsync(context.Response, new { error = $"PublicationPolicy inválido: '{payload.PublicationPolicy}'." });
                                return;
                            }
                            var isEnabled = payload.IsEnabled ?? true;
                            var updated = await _catalogResolver.UpdateCanonicalChannelAsync(
                                channelId, payload.DisplayName, editorialCategory,
                                editorialGroup, publicationPolicy, isEnabled, payload.Country);
                            if (updated == null)
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                await WriteJsonAsync(context.Response, new { error = $"Canal #{channelId} não encontrado." });
                                return;
                            }
                            var reloaded = await _catalogResolver.GetCanonicalChannelAsync(channelId);
                            await WriteJsonAsync(context.Response, ChannelToJson(reloaded!));
                            return;
                        }
                        catch (ChannelAdministrationException ex)
                        {
                            await WriteChannelAdminError(context.Response, ex);
                            return;
                        }
                    }

                    if (context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var deleted = await _catalogResolver.DeleteCanonicalChannelAsync(channelId);
                            if (!deleted)
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                await WriteJsonAsync(context.Response, new { error = $"Canal #{channelId} não encontrado." });
                                return;
                            }
                            await WriteJsonAsync(context.Response, new { deleted = true, id = channelId });
                            return;
                        }
                        catch (ChannelAdministrationException ex)
                        {
                            await WriteChannelAdminError(context.Response, ex);
                            return;
                        }
                    }

                    context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    await WriteJsonAsync(context.Response, new { error = "Método não permitido." });
                    return;
                }

                // /api/catalog/channels/{id}/aliases  or  .../aliases/{alias}
                if (segments.Length >= 2 && segments[1].Equals("aliases", StringComparison.OrdinalIgnoreCase))
                {
                    // POST .../aliases
                    if (segments.Length == 2 && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                            var body = await reader.ReadToEndAsync();
                            var payload = JsonSerializer.Deserialize<AddAliasPayload>(body, JsonOptions);
                            if (payload == null || string.IsNullOrWhiteSpace(payload.NormalizedAlias))
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteJsonAsync(context.Response, new { error = "Payload inválido: NormalizedAlias é obrigatório." });
                                return;
                            }
                            var alias = await _catalogResolver.AddAliasAsync(channelId, payload.NormalizedAlias);
                            await WriteJsonAsync(context.Response, new
                            {
                                id = alias.Id,
                                normalizedAlias = alias.NormalizedAlias,
                                canonicalChannelId = alias.CanonicalChannelId,
                                createdAtUtc = alias.CreatedAtUtc.ToString("o"),
                            }, HttpStatusCode.Created);
                            return;
                        }
                        catch (ChannelAdministrationException ex)
                        {
                            await WriteChannelAdminError(context.Response, ex);
                            return;
                        }
                    }

                    // DELETE .../aliases/{alias}
                    if (segments.Length == 3 && context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        var alias = Uri.UnescapeDataString(segments[2]);
                        var removed = await _catalogResolver.RemoveAliasAsync(channelId, alias);
                        if (!removed)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            await WriteJsonAsync(context.Response, new { error = $"Alias '{alias}' não encontrado no canal #{channelId}." });
                            return;
                        }
                        await WriteJsonAsync(context.Response, new { deleted = true, channelId, alias });
                        return;
                    }

                    context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    await WriteJsonAsync(context.Response, new { error = "Método não permitido para /aliases." });
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteJsonAsync(context.Response, new { error = "Sub-path desconhecido." });
                return;
            }

            // POST /api/catalog/channels
            if (requestPath.Equals("/api/catalog/channels", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var payload = JsonSerializer.Deserialize<CreateChannelPayload>(body, JsonOptions);
                    if (payload == null || string.IsNullOrWhiteSpace(payload.Key) || string.IsNullOrWhiteSpace(payload.DisplayName))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = "Payload inválido: Key e DisplayName são obrigatórios." });
                        return;
                    }
                    if (!Enum.TryParse<EditorialCategory>(payload.EditorialCategory, true, out var editorialCategory))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = $"EditorialCategory inválido: '{payload.EditorialCategory}'." });
                        return;
                    }
                    if (!Enum.TryParse<CanonicalEditorialGroup>(payload.EditorialGroup, true, out var editorialGroup))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = $"EditorialGroup inválido: '{payload.EditorialGroup}'." });
                        return;
                    }
                    if (!Enum.TryParse<PublicationPolicy>(payload.PublicationPolicy, true, out var publicationPolicy))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = $"PublicationPolicy inválido: '{payload.PublicationPolicy}'." });
                        return;
                    }
                    var aliases = (payload.Aliases ?? new List<string>())
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a.Trim())
                        .ToList();
                    var created = await _catalogResolver.CreateCanonicalChannelAsync(
                        payload.Key.Trim(), payload.DisplayName,
                        editorialCategory, editorialGroup, publicationPolicy,
                        payload.IsEnabled, aliases, payload.Country);
                    var reloaded = await _catalogResolver.GetCanonicalChannelAsync(created.Id);
                    await WriteJsonAsync(context.Response, ChannelToJson(reloaded!), HttpStatusCode.Created);
                    return;
                }
                catch (ChannelAdministrationException ex)
                {
                    await WriteChannelAdminError(context.Response, ex);
                    return;
                }
            }

            // === Stream Validation Policy (PHASE 9A) ===
            // GET  /api/validation/policy
            // POST /api/validation/policy
            // POST /api/validation/test (dry-run contra URLs de exemplo; útil para tunar)
            if (requestPath.Equals("/api/validation/policy", StringComparison.OrdinalIgnoreCase))
            {
                var runtimeDir = Path.Combine(Directory.GetCurrentDirectory(), "runtime-data");
                var store = new StreamValidationPolicyStore(runtimeDir);
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    var current = store.Load();
                    await WriteJsonAsync(context.Response, current);
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var incoming = JsonSerializer.Deserialize<StreamValidationOptions>(body, JsonOptions);
                        if (incoming == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                            return;
                        }
                        var saved = store.Save(incoming);
                        await WriteJsonAsync(context.Response, saved);
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            // === App settings (PHASE 9C.3) ===
            // GET  /api/settings
            // POST /api/settings
            if (requestPath.Equals("/api/settings", StringComparison.OrdinalIgnoreCase))
            {
                var runtimeDir = Path.Combine(Directory.GetCurrentDirectory(), "runtime-data");
                var settingsStore = new AppSettingsStore(runtimeDir);
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context.Response, settingsStore.Load());
                    return;
                }
                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var payload = JsonSerializer.Deserialize<AppSettingsPayload>(body, JsonOptions);
                        if (payload == null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new { error = "Payload inválido." });
                            return;
                        }
                        var current = settingsStore.Load();
                        if (payload.AffinityVariantDelimiter != null)
                        {
                            current.AffinityVariantDelimiter = payload.AffinityVariantDelimiter;
                        }
                        var saved = settingsStore.Save(current);
                        await WriteJsonAsync(context.Response, saved);
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = ex.Message });
                        return;
                    }
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (requestPath.Equals("/api/validation/test", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var payload = JsonSerializer.Deserialize<ValidationTestPayload>(body, JsonOptions);
                    if (payload == null || payload.Urls == null || payload.Urls.Count == 0)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteJsonAsync(context.Response, new { error = "Payload inválido: campo 'urls' é obrigatório." });
                        return;
                    }
                    var runtimeDir = Path.Combine(Directory.GetCurrentDirectory(), "runtime-data");
                    var store = new StreamValidationPolicyStore(runtimeDir);
                    var options = payload.Options ?? store.Load();
                    options.Sanitize();

                    using var tester = new M3uTesterService(options);
                    var outcomes = await tester.RunAsync(payload.Urls, options);
                    var metrics = tester.LastMetrics;
                    await WriteJsonAsync(context.Response, new
                    {
                        options = options,
                        outcomes = outcomes,
                        metrics = metrics?.ToAnonymousObject(),
                    });
                    return;
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteJsonAsync(context.Response, new { error = ex.Message });
                    return;
                }
            }

            if (isRootPath && authMode == AuthMode.Bootstrap)
            {
                RedirectTo(context.Response, "/bootstrap");
                return;
            }

            if (isRootPath && authMode == AuthMode.UserAuth)
            {
                var rootSession = _authService != null
                    ? await _authService.ValidateSessionAsync(sessionId)
                    : null;
                if (rootSession == null)
                {
                    await WriteHtmlAsync(context.Response, BuildLoginHtml());
                    return;
                }

                // PHASE 9C.2 (B2) — Entrega o token CSRF à página autenticada,
                // apenas em memória JavaScript da página (nunca em URL, query,
                // localStorage ou logs), para que o helper de fetch o envie
                // automaticamente em métodos mutantes.
                await WriteHtmlAsync(context.Response, BuildHtmlPage(rootSession.CsrfToken));
                return;
            }

            await WriteHtmlAsync(context.Response, BuildHtmlPage());
        }

        private sealed class ValidationTestPayload
        {
            [JsonPropertyName("urls")]
            public List<string>? Urls { get; set; }
            [JsonPropertyName("options")]
            public StreamValidationOptions? Options { get; set; }
        }

        private static async Task WriteChannelAdminError(HttpListenerResponse response, ChannelAdministrationException ex)
        {
            var status = ex.Error switch
            {
                ChannelAdministrationError.DuplicateKey => HttpStatusCode.Conflict,
                ChannelAdministrationError.AliasConflict => HttpStatusCode.Conflict,
                ChannelAdministrationError.AlreadyExists => HttpStatusCode.Conflict,
                ChannelAdministrationError.ChannelNotFound => HttpStatusCode.NotFound,
                ChannelAdministrationError.HasOwnership => HttpStatusCode.Conflict,
                _ => HttpStatusCode.BadRequest,
            };
            response.StatusCode = (int)status;
            await WriteJsonAsync(response, new { error = ex.Message, code = ex.Error.ToString() });
        }

        // Opções JSON partilhadas por todos os endpoints do dashboard: serializam
        // com camelCase (alinhado com o que o JavaScript inlined lê) e permitem
        // deserializar JSON camelCase (como o telegram_run_report.json escrito
        // por Program.cs com a mesma política). Sem isto o frontend recebe
        // PascalCase e renderiza "undefined" / "Invalid Date".
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Constrói o payload JSON do endpoint <c>/api/version</c>. Mantém-se
        /// como função interna pura para ser testada directamente, sem
        /// precisar do <c>HttpListener</c>.
        /// </summary>
        internal static object BuildVersionPayload()
        {
            var info = BuildInfo.Current;
            return new
            {
                application = BuildInfo.Application,
                version = info.Version,
                commit = info.Commit,
                build = info.BuildNumber,
                buildDate = info.BuildDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        /// <summary>
        /// PHASE 9C.1 — Constrói o payload do endpoint
        /// <c>GET /api/configuration/lifecycle</c>. Público para ser
        /// testável sem levantar um HttpListener. Quando o lifecycle não
        /// está ligado, reporta <c>available=false</c> e um estado
        /// não-pronto (fail-safe), nunca uma falsa prontidão.
        /// </summary>
        public static async Task<object> BuildLifecyclePayloadAsync(ConfigurationLifecycleService? lifecycle)
        {
            if (lifecycle == null)
            {
                return new
                {
                    state = ConfigurationLifecycleState.NotConfigured.ToWireName(),
                    available = false,
                    isReady = false,
                    adoptedFromLegacy = false,
                    adoptedAtUtc = (string?)null,
                    updatedAtUtc = (string?)null,
                    reason = "lifecycle-not-wired",
                    advisory = Array.Empty<object>(),
                };
            }

            var snapshot = await lifecycle.GetStateAsync();
            var advisory = await lifecycle.EvaluateAdvisoryAsync();

            return new
            {
                state = snapshot.State.ToWireName(),
                available = true,
                isReady = snapshot.IsReady,
                adoptedFromLegacy = snapshot.AdoptedFromLegacy,
                adoptedAtUtc = snapshot.AdoptedAtUtc?.ToString("o"),
                updatedAtUtc = snapshot.UpdatedAtUtc.ToString("o"),
                reason = snapshot.LastReason,
                advisory = advisory.Select(a => new
                {
                    key = a.Key,
                    satisfied = a.Satisfied,
                    detail = a.Detail,
                }),
            };
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, object data, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            response.StatusCode = (int)statusCode;
            var json = JsonSerializer.Serialize(data, JsonOptions);
            response.ContentType = "application/json; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        /// <summary>
        /// Returns the most recent <c>dispatcharr_plan_*.json</c>
        /// file under <paramref name="outputDir"/>, or <c>null</c> when
        /// no plan exists yet. Filename ordering is the same as the
        /// writer: <c>dispatcharr_plan_{yyyyMMdd_HHmmss}.json</c>;
        /// we use <see cref="File.GetLastWriteTimeUtc"/> to break
        /// filename-collision ties deterministically.
        /// </summary>
        private static string? LatestDispatcharrPlanPath(string outputDir)
        {
            if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            {
                return null;
            }
            try
            {
                var files = Directory
                    .EnumerateFiles(outputDir, "dispatcharr_plan_*.json", SearchOption.TopDirectoryOnly)
                    .Select(p => new { Path = p, Ticks = File.GetLastWriteTimeUtc(p).Ticks })
                    .OrderByDescending(x => x.Ticks)
                    .ThenByDescending(x => x.Path, StringComparer.Ordinal)
                    .Select(x => x.Path)
                    .ToList();
                return files.Count == 0 ? null : files[0];
            }
            catch
            {
                return null;
            }
        }

        private static async Task WriteTextAsync(HttpListenerResponse response, string text, HttpStatusCode statusCode = HttpStatusCode.OK, string contentType = "text/plain; charset=utf-8")
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = contentType;
            var buffer = Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        private static async Task WriteHtmlAsync(HttpListenerResponse response, string html)
        {
            response.ContentType = "text/html; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

    /// <summary>
    /// PHASE 9C.2 (B2) — Página do Dashboard. Quando <paramref name="csrfToken"/>
    /// é fornecido (sessão humana autenticada), injecta um helper que adiciona o
    /// header <c>X-CSRF-Token</c> a todos os <c>fetch</c> de mesma origem. O token
    /// vive apenas em memória JavaScript da página — nunca em URL, query,
    /// localStorage ou logs. Sem token (legacy/bootstrap) o helper não é injectado.
    /// </summary>
    private static string BuildHtmlPage(string? csrfToken = null)
    {
        var html = BuildDashboardHtml();
        if (string.IsNullOrEmpty(csrfToken))
        {
            return html;
        }

        var tokenLiteral = JsonSerializer.Serialize(csrfToken);
        var script =
            "<script>(function(){var t=" + tokenLiteral + ";" +
            "if(!t||typeof window.fetch!=='function'){return;}" +
            "var f=window.fetch.bind(window);" +
            "window.fetch=function(input,init){init=init||{};" +
            "var h=new Headers(init.headers||{});" +
            "if(!h.has('X-CSRF-Token')){h.set('X-CSRF-Token',t);}" +
            "init.headers=h;return f(input,init);};})();</script>";

        var bodyIndex = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyIndex < 0)
        {
            return script + html;
        }

        var bodyClose = html.IndexOf('>', bodyIndex);
        return bodyClose < 0 ? script + html : html.Insert(bodyClose + 1, script);
    }

    private sealed class IdentityRulePayload
    {
        [JsonPropertyName("normalizedIdentity")]
        public string? NormalizedIdentity { get; set; }
        [JsonPropertyName("disposition")]
        public string Disposition { get; set; } = "ReviewOnly";
        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }

    private sealed class AffinityGroupPayload
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
        [JsonPropertyName("canonicalChannelKey")]
        public string? CanonicalChannelKey { get; set; }
        [JsonPropertyName("canonicalChannelId")]
        public long? CanonicalChannelId { get; set; }
        [JsonPropertyName("countryCode")]
        public string? CountryCode { get; set; }
        [JsonPropertyName("members")]
        public List<string> Members { get; set; } = new();
    }

    private sealed class AppSettingsPayload
    {
        [JsonPropertyName("affinityVariantDelimiter")]
        public string? AffinityVariantDelimiter { get; set; }
    }

    /// <summary>
    /// Resolve o kind/país/canal de um payload de afinidade. O kind
    /// explícito tem prioridade; caso ausente, é inferido
    /// (canal → Channel, país → Country) para compatibilidade com
    /// clientes antigos.
    /// </summary>
    private static async Task<(AffinityKind Kind, string? Key, string? Country, string? Error)>
        ResolveAffinityPayloadAsync(AffinityGroupPayload payload)
    {
        var kindText = (payload.Kind ?? string.Empty).Trim().ToLowerInvariant();
        AffinityKind? kind = kindText switch
        {
            "channel" => AffinityKind.Channel,
            "country" => AffinityKind.Country,
            "" => (AffinityKind?)null,
            _ => (AffinityKind?)null,
        };
        if (kindText.Length > 0 && kind == null)
        {
            return (default, null, null, $"Kind inválido: '{payload.Kind}'.");
        }

        var key = (payload.CanonicalChannelKey ?? string.Empty).Trim();
        if (key.Length == 0 && payload.CanonicalChannelId.HasValue && _catalogResolver != null)
        {
            var channel = await _catalogResolver.GetCanonicalChannelAsync(payload.CanonicalChannelId.Value);
            key = channel?.Key ?? string.Empty;
        }

        if (kind == null)
        {
            if (key.Length > 0) kind = AffinityKind.Channel;
            else if (!string.IsNullOrWhiteSpace(payload.CountryCode)) kind = AffinityKind.Country;
        }
        if (kind == null)
        {
            return (default, null, null, "Kind é obrigatório (Channel ou Country).");
        }

        return (kind.Value, key.Length > 0 ? key : null, payload.CountryCode, null);
    }

    private sealed class ApproveReviewPayload
    {
        [JsonPropertyName("approvedCanonicalChannelId")]
        public long? ApprovedCanonicalChannelId { get; set; }
    }

    private sealed class CreateChannelPayload
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }
        [JsonPropertyName("country")]
        public string? Country { get; set; }
        [JsonPropertyName("editorialCategory")]
        public string? EditorialCategory { get; set; }
        [JsonPropertyName("editorialGroup")]
        public string? EditorialGroup { get; set; }
        [JsonPropertyName("publicationPolicy")]
        public string? PublicationPolicy { get; set; }
        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; } = true;
        [JsonPropertyName("aliases")]
        public List<string>? Aliases { get; set; }
    }

    private sealed class UpdateChannelPayload
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }
        [JsonPropertyName("country")]
        public string? Country { get; set; }
        [JsonPropertyName("editorialCategory")]
        public string? EditorialCategory { get; set; }
        [JsonPropertyName("editorialGroup")]
        public string? EditorialGroup { get; set; }
        [JsonPropertyName("publicationPolicy")]
        public string? PublicationPolicy { get; set; }
        [JsonPropertyName("isEnabled")]
        public bool? IsEnabled { get; set; }
    }

    private sealed class AddAliasPayload
    {
        [JsonPropertyName("normalizedAlias")]
        public string? NormalizedAlias { get; set; }
    }

    private static object ChannelToJson(CanonicalChannelEntity c)
    {
        return new
        {
            id = c.Id,
            key = c.Key,
            displayName = c.DisplayName,
            country = c.Country,
            editorialCategory = c.EditorialCategory.ToString(),
            editorialGroup = c.EditorialGroup.ToString(),
            publicationPolicy = c.PublicationPolicy.ToString(),
            isEnabled = c.IsEnabled,
            aliases = c.Aliases.OrderBy(a => a.NormalizedAlias, StringComparer.Ordinal)
                .Select(a => a.NormalizedAlias).ToList(),
            createdAtUtc = c.CreatedAtUtc.ToString("o"),
            updatedAtUtc = c.UpdatedAtUtc.ToString("o"),
        };
    }

    // === PHASE 4 — Sources & ChannelSources payloads ===
    private sealed class SourcePayload
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("origin")] public string? Origin { get; set; }
        [JsonPropertyName("priority")] public int Priority { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    }

    private sealed class ChannelSourcePayload
    {
        [JsonPropertyName("canonicalChannelId")] public long CanonicalChannelId { get; set; }
        [JsonPropertyName("streamUrl")] public string? StreamUrl { get; set; }
        [JsonPropertyName("externalStreamId")] public string? ExternalStreamId { get; set; }
        [JsonPropertyName("quality")] public string? Quality { get; set; }
        [JsonPropertyName("epg")] public string? Epg { get; set; }
        [JsonPropertyName("availability")] public string? Availability { get; set; }
        [JsonPropertyName("matchConfidence")] public double MatchConfidence { get; set; }
        [JsonPropertyName("matchMethod")] public string? MatchMethod { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    }

    private sealed class ChannelSourceUpdatePayload
    {
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    }

    // === PHASE 9 b — ChannelSource observation payloads ===
    private sealed class ChannelSourceObservationPayload
    {
        [JsonPropertyName("quality")] public string? Quality { get; set; }
        [JsonPropertyName("epg")] public string? Epg { get; set; }
        [JsonPropertyName("availability")] public string? Availability { get; set; }
        [JsonPropertyName("responseTimeMs")] public long ResponseTimeMs { get; set; }
    }

    // === PHASE 11 — SyncRunStep payloads ===
    private sealed class SyncRunStepPayload
    {
        [JsonPropertyName("step")] public string? Step { get; set; }
        [JsonPropertyName("startedAtUtc")] public DateTime? StartedAtUtc { get; set; }
        [JsonPropertyName("finishedAtUtc")] public DateTime? FinishedAtUtc { get; set; }
        [JsonPropertyName("itemsProcessed")] public int ItemsProcessed { get; set; }
        [JsonPropertyName("itemsSucceeded")] public int ItemsSucceeded { get; set; }
        [JsonPropertyName("itemsFailed")] public int ItemsFailed { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
    }

    // === PHASE 12 — Scheduled Job payloads ===
    private sealed class ScheduledJobPayload
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("cronExpression")] public string? CronExpression { get; set; }
        [JsonPropertyName("actionName")] public string? ActionName { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    }

    private sealed class ScheduledJobEnablePayload
    {
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    }

    private static object SourceToJson(SourceEntity s)
    {
        return new
        {
            id = s.Id,
            key = s.Key,
            name = s.Name,
            kind = s.Kind.ToString(),
            origin = s.Origin,
            priority = s.Priority,
            isEnabled = s.IsEnabled,
            lastDiscoveryAtUtc = s.LastDiscoveryAtUtc?.ToString("o"),
            lastValidationAtUtc = s.LastValidationAtUtc?.ToString("o"),
            createdAtUtc = s.CreatedAtUtc.ToString("o"),
            updatedAtUtc = s.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object ChannelSourceToJson(ChannelSourceEntity cs)
    {
        return new
        {
            id = cs.Id,
            canonicalChannelId = cs.CanonicalChannelId,
            sourceId = cs.SourceId,
            streamUrl = cs.StreamUrl,
            externalStreamId = cs.ExternalStreamId,
            quality = cs.Quality.ToString(),
            epg = cs.Epg.ToString(),
            availability = cs.Availability.ToString(),
            matchConfidence = cs.MatchConfidence,
            matchMethod = cs.MatchMethod,
            isEnabled = cs.IsEnabled,
            firstSeenAtUtc = cs.FirstSeenAtUtc.ToString("o"),
            lastSeenAtUtc = cs.LastSeenAtUtc.ToString("o"),
            lastTestedAtUtc = cs.LastTestedAtUtc.ToString("o"),
            lastResponseTimeMs = cs.LastResponseTimeMs,
            createdAtUtc = cs.CreatedAtUtc.ToString("o"),
            updatedAtUtc = cs.UpdatedAtUtc.ToString("o"),
        };
    }

    // === PHASE 5 — Ordering payloads ===
    private sealed class OrderingListPayload
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    }

    private sealed class OrderingListDuplicatePayload
    {
        [JsonPropertyName("newKey")] public string? NewKey { get; set; }
        [JsonPropertyName("newName")] public string? NewName { get; set; }
    }

    private sealed class OrderingItemAddPayload
    {
        [JsonPropertyName("canonicalChannelId")] public long CanonicalChannelId { get; set; }
        [JsonPropertyName("position")] public int? Position { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    }

    private sealed class OrderingItemUpdatePayload
    {
        [JsonPropertyName("position")] public int? Position { get; set; }
        [JsonPropertyName("isEnabled")] public bool? IsEnabled { get; set; }
    }

    // === PHASE 6 — Priority Policy payloads ===
    private sealed class PriorityPolicyPayload
    {
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("canonicalChannelId")] public long? CanonicalChannelId { get; set; }
        [JsonPropertyName("criteriaJson")] public string? CriteriaJson { get; set; }
        [JsonPropertyName("preferredQuality")] public string? PreferredQuality { get; set; }
        [JsonPropertyName("allowFallback")] public bool AllowFallback { get; set; } = true;
    }

    // === PHASE 13 (Wave 13-4) — Source Selection Policy payload ===
    private sealed class SourceSelectionPolicyPayload
    {
        [JsonPropertyName("maxSourcesPerChannel")] public int? MaxSourcesPerChannel { get; set; }
        [JsonPropertyName("preferDistinctProviders")] public bool? PreferDistinctProviders { get; set; }
        [JsonPropertyName("maxSourcesPerProvider")] public int? MaxSourcesPerProvider { get; set; }
        [JsonPropertyName("allowFallbackToSameProvider")] public bool? AllowFallbackToSameProvider { get; set; }
    }

    // === PHASE 13 (Wave 13-4b) — Per-channel Source Selection Policy payload ===
    private sealed class ChannelSourceSelectionPolicyPayload
    {
        [JsonPropertyName("canonicalChannelKey")] public string? CanonicalChannelKey { get; set; }
        [JsonPropertyName("maxSourcesPerChannel")] public int? MaxSourcesPerChannel { get; set; }
        [JsonPropertyName("preferDistinctProviders")] public bool? PreferDistinctProviders { get; set; }
        [JsonPropertyName("maxSourcesPerProvider")] public int? MaxSourcesPerProvider { get; set; }
        [JsonPropertyName("allowFallbackToSameProvider")] public bool? AllowFallbackToSameProvider { get; set; }
    }

    // === PHASE 8 — payloads ===
    private sealed class ImportPolicyPayload
    {
        [JsonPropertyName("mediaKind")] public string? MediaKind { get; set; }
        [JsonPropertyName("vodPolicy")] public string? VodPolicy { get; set; }
        [JsonPropertyName("targetGroupsCsv")] public string? TargetGroupsCsv { get; set; }
        [JsonPropertyName("excludedGroupsCsv")] public string? ExcludedGroupsCsv { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    }

    private sealed class CanonicalGroupPayload
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("order")] public int Order { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
        [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }
    }

    private sealed class GroupMappingPayload
    {
        [JsonPropertyName("sourceKind")] public string? SourceKind { get; set; }
        [JsonPropertyName("sourceGroupTitle")] public string? SourceGroupTitle { get; set; }
        [JsonPropertyName("canonicalGroupId")] public long CanonicalGroupId { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    }

    private static object ImportPolicyToJson(ImportPolicyEntity p)
    {
        return new
        {
            id = p.Id,
            mediaKind = p.MediaKind.ToString(),
            vodPolicy = p.VodPolicy.ToString(),
            targetGroupsCsv = p.TargetGroupsCsv,
            excludedGroupsCsv = p.ExcludedGroupsCsv,
            isEnabled = p.IsEnabled,
            createdAtUtc = p.CreatedAtUtc.ToString("o"),
            updatedAtUtc = p.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object CanonicalGroupToJson(CanonicalGroupEntity g)
    {
        return new
        {
            id = g.Id,
            key = g.Key,
            displayName = g.DisplayName,
            country = g.Country,
            order = g.Order,
            isEnabled = g.IsEnabled,
            isDefault = g.IsDefault,
            createdAtUtc = g.CreatedAtUtc.ToString("o"),
            updatedAtUtc = g.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object GroupMappingToJson(GroupMappingEntity m)
    {
        return new
        {
            id = m.Id,
            sourceKind = m.SourceKind.ToString(),
            sourceGroupTitle = m.SourceGroupTitle,
            canonicalGroupId = m.CanonicalGroupId,
            canonicalGroupKey = m.CanonicalGroup?.Key,
            canonicalGroupDisplayName = m.CanonicalGroup?.DisplayName,
            isEnabled = m.IsEnabled,
            createdAtUtc = m.CreatedAtUtc.ToString("o"),
            updatedAtUtc = m.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object MatchingAuditToJson(MatchingAuditEntity a)
    {
        return new
        {
            id = a.Id,
            normalizedIdentity = a.NormalizedIdentity,
            originalTitle = a.OriginalTitle,
            sourceGroup = a.SourceGroup,
            resolutionKind = a.ResolutionKind,
            canonicalChannelId = a.CanonicalChannelId,
            confidence = a.Confidence,
            reasonSignature = a.ReasonSignature,
            atUtc = a.AtUtc.ToString("o"),
        };
    }

    private static object ChannelSourceObservationToJson(ChannelSourceObservationEntity o)
    {
        return new
        {
            id = o.Id,
            channelSourceId = o.ChannelSourceId,
            quality = o.Quality.ToString(),
            epg = o.Epg.ToString(),
            availability = o.Availability.ToString(),
            responseTimeMs = o.ResponseTimeMs,
            observedAtUtc = o.ObservedAtUtc.ToString("o"),
        };
    }

    private static object SyncRunStepToJson(SyncRunStepEntity s)
    {
        return new
        {
            id = s.Id,
            syncRunId = s.SyncRunId,
            step = s.Step,
            startedAtUtc = s.StartedAtUtc.ToString("o"),
            finishedAtUtc = s.FinishedAtUtc.ToString("o"),
            durationMs = s.DurationMs,
            itemsProcessed = s.ItemsProcessed,
            itemsSucceeded = s.ItemsSucceeded,
            itemsFailed = s.ItemsFailed,
            result = s.Result,
        };
    }

    private static object ScheduledJobToJson(ScheduledJobEntity j)
    {
        return new
        {
            id = j.Id,
            name = j.Name,
            cronExpression = j.CronExpression,
            actionName = j.ActionName,
            isEnabled = j.IsEnabled,
            lastRunAtUtc = j.LastRunAtUtc?.ToString("o"),
            nextRunAtUtc = j.NextRunAtUtc?.ToString("o"),
            lastResult = j.LastResult,
            createdAtUtc = j.CreatedAtUtc.ToString("o"),
            updatedAtUtc = j.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object DegradedStreamToJson(DegradedStreamSummary d)
    {
        return new
        {
            channelSourceId = d.ChannelSourceId,
            canonicalChannelId = d.CanonicalChannelId,
            sourceId = d.SourceId,
            sourceName = d.SourceName,
            latestAvailability = d.LatestAvailability.ToString(),
            latestObservedAtUtc = d.LatestObservedAtUtc.ToString("o"),
            latestResponseMs = d.LatestResponseMs,
            lastHealthyAtUtc = d.LastHealthyAtUtc?.ToString("o"),
            totalSamples = d.TotalSamples,
            failedSamples = d.FailedSamples,
            failureRate = d.FailureRate,
            quality = d.Quality.ToString(),
            epg = d.Epg.ToString(),
        };
    }

    private static object OrderingListSummaryToJson(OrderingListEntity l)
    {
        return new
        {
            id = l.Id,
            key = l.Key,
            name = l.Name,
            country = l.Country,
            description = l.Description,
            isEnabled = l.IsEnabled,
            itemCount = l.Items?.Count ?? 0,
            createdAtUtc = l.CreatedAtUtc.ToString("o"),
            updatedAtUtc = l.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object OrderingListDetailToJson(OrderingListEntity l)
    {
        return new
        {
            id = l.Id,
            key = l.Key,
            name = l.Name,
            country = l.Country,
            description = l.Description,
            isEnabled = l.IsEnabled,
            items = (l.Items ?? new List<OrderingItemEntity>())
                .OrderBy(i => i.Position)
                .Select(OrderingItemToJson),
            createdAtUtc = l.CreatedAtUtc.ToString("o"),
            updatedAtUtc = l.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object OrderingItemToJson(OrderingItemEntity i)
    {
        return new
        {
            id = i.Id,
            orderingListId = i.OrderingListId,
            canonicalChannelId = i.CanonicalChannelId,
            canonicalChannelKey = i.CanonicalChannel?.Key,
            canonicalChannelDisplayName = i.CanonicalChannel?.DisplayName,
            position = i.Position,
            isEnabled = i.IsEnabled,
            createdAtUtc = i.CreatedAtUtc.ToString("o"),
            updatedAtUtc = i.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object PriorityPolicyToJson(SourcePriorityPolicyEntity p)
    {
        return new
        {
            id = p.Id,
            scope = p.Scope,
            canonicalChannelId = p.CanonicalChannelId,
            criteriaJson = p.CriteriaJson,
            preferredQuality = p.PreferredQuality,
            allowFallback = p.AllowFallback,
            createdAtUtc = p.CreatedAtUtc.ToString("o"),
            updatedAtUtc = p.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object SourceSelectionPolicyToJson(SourceSelectionPolicyEntity p)
    {
        return new
        {
            id = p.Id,
            scopeKey = p.ScopeKey,
            canonicalChannelKey = p.CanonicalChannelKey,
            maxSourcesPerChannel = p.MaxSourcesPerChannel,
            preferDistinctProviders = p.PreferDistinctProviders,
            maxSourcesPerProvider = p.MaxSourcesPerProvider,
            allowFallbackToSameProvider = p.AllowFallbackToSameProvider,
            createdAtUtc = p.CreatedAtUtc.ToString("o"),
            updatedAtUtc = p.UpdatedAtUtc.ToString("o"),
        };
    }

    private static object ChannelSourceSelectionPolicyToJson(SourceSelectionPolicyEntity p)
    {
        return new
        {
            scopeKey = p.ScopeKey,
            canonicalChannelKey = p.CanonicalChannelKey,
            maxSourcesPerChannel = p.MaxSourcesPerChannel,
            preferDistinctProviders = p.PreferDistinctProviders,
            maxSourcesPerProvider = p.MaxSourcesPerProvider,
            allowFallbackToSameProvider = p.AllowFallbackToSameProvider,
            createdAtUtc = p.CreatedAtUtc.ToString("o"),
            updatedAtUtc = p.UpdatedAtUtc.ToString("o"),
        };
    }

    // === PHASE 7 — Playlist Composer JSON ===
    private static object PlaylistCompositionToJson(PlaylistComposition c)
    {
        return new
        {
            orderingListId = c.OrderingListId,
            orderingListName = c.OrderingListName,
            totalEntries = c.TotalEntries,
            entries = c.Entries.Select(e => new
            {
                canonicalChannelId = e.CanonicalChannelId,
                canonicalKey = e.CanonicalKey,
                displayName = e.DisplayName,
                group = e.Group,
                streamUrl = e.StreamUrl,
                chosenChannelSourceId = e.ChosenChannelSourceId,
                sourceId = e.SourceId,
                sourceName = e.SourceName,
                quality = e.Quality,
            }),
            missingChannels = c.MissingChannels.Select(m => new
            {
                canonicalChannelId = m.CanonicalChannelId,
                reason = m.Reason,
            }),
        };
    }

    private static string BuildDashboardHtml()
        {
            string s = """
<!doctype html>
<html lang='pt'>
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width,initial-scale=1'>
  <title>m3uCrawler Dashboard</title>
  <style>
    :root {
      --bg: #0d1117;
      --panel: #161b22;
      --panel-2: #1c232c;
      --border: #30363d;
      --text: #e6edf3;
      --muted: #8b949e;
      --accent: #2f81f7;
      --accent-2: #218bff;
      --ok: #3fb950;
      --warn: #d29922;
      --err: #f85149;
      --info: #79c0ff;
    }
    * { box-sizing: border-box; }
    html, body { margin: 0; padding: 0; background: var(--bg); color: var(--text); font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif; font-size: 14px; line-height: 1.5; }
    a { color: var(--accent-2); text-decoration: none; }
    a:hover { text-decoration: underline; }
    header { background: var(--panel); border-bottom: 1px solid var(--border); padding: 12px 20px; display: flex; align-items: center; gap: 16px; }
    header h1 { font-size: 16px; margin: 0; font-weight: 600; }
    header .meta { color: var(--muted); font-size: 12px; }
    nav { background: var(--panel); border-bottom: 1px solid var(--border); padding: 0 12px; display: flex; gap: 4px; flex-wrap: wrap; }
    nav button { background: transparent; color: var(--muted); border: none; padding: 12px 16px; cursor: pointer; font: inherit; border-bottom: 2px solid transparent; }
    nav button:hover { color: var(--text); }
    nav button.active { color: var(--text); border-bottom-color: var(--accent); }
    main { padding: 20px; max-width: 1400px; margin: 0 auto; }
    section[hidden] { display: none; }
    .grid { display: grid; gap: 16px; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); margin-bottom: 20px; }
    .card { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; padding: 16px; }
    .card h3 { margin: 0 0 8px 0; font-size: 13px; color: var(--muted); font-weight: 500; text-transform: uppercase; letter-spacing: 0.05em; }
    .card .value { font-size: 28px; font-weight: 600; }
    .card .sub { color: var(--muted); font-size: 12px; margin-top: 6px; }
    .card .help { color: var(--muted); font-size: 11px; margin-top: 8px; border-top: 1px solid var(--border); padding-top: 8px; }
    table { border-collapse: collapse; width: 100%; background: var(--panel); border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border); }
    th { background: var(--panel-2); font-weight: 500; font-size: 12px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.04em; }
    tr:last-child td { border-bottom: none; }
    tr:hover td { background: rgba(56, 139, 253, 0.06); }
    .badge { display: inline-block; padding: 2px 8px; border-radius: 999px; font-size: 11px; }
    .badge.ok { background: rgba(63, 185, 80, 0.18); color: var(--ok); }
    .badge.warn { background: rgba(210, 153, 34, 0.18); color: var(--warn); }
    .badge.err { background: rgba(248, 81, 73, 0.18); color: var(--err); }
    .badge.info { background: rgba(121, 192, 255, 0.18); color: var(--info); }
    .badge.muted { background: rgba(139, 148, 158, 0.18); color: var(--muted); }
    .toolbar { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 12px; }
    .toolbar select, .toolbar input { background: var(--panel); color: var(--text); border: 1px solid var(--border); padding: 6px 10px; border-radius: 6px; font: inherit; }
    .toolbar button { background: var(--accent); color: white; border: none; padding: 6px 12px; border-radius: 6px; cursor: pointer; font: inherit; }
    .toolbar button.secondary { background: var(--panel-2); color: var(--text); border: 1px solid var(--border); }
    textarea { width: 100%; background: var(--panel); color: var(--text); border: 1px solid var(--border); border-radius: 6px; padding: 10px; font: inherit; }
    .muted { color: var(--muted); }
    .bar { background: var(--panel-2); border-radius: 4px; overflow: hidden; height: 8px; display: flex; border: 1px solid var(--border); }
    .bar > div { height: 100%; }
    .bar .ok { background: var(--ok); }
    .bar .err { background: var(--err); }
    pre { background: var(--panel-2); padding: 14px; overflow: auto; border-radius: 6px; border: 1px solid var(--border); font-size: 12px; }
    .row-counts { color: var(--muted); font-size: 12px; margin-top: 8px; }
    details { background: var(--panel); border: 1px solid var(--border); border-radius: 6px; padding: 8px 12px; margin-top: 8px; }
    summary { cursor: pointer; font-weight: 500; }
  </style>
</head>
<body>
  <header>
    <h1>m3uCrawler Dashboard</h1>
    <span class='meta' id='metaLine'>a carregar…</span>
  </header>

    <nav id='nav'>
    <button data-view='overview' class='active'>Overview</button>
    <button data-view='executions'>Execuções</button>
    <button data-view='discovery'>Descoberta</button>
    <button data-view='countries'>Canais / Países</button>
    <button data-view='playlist'>Playlist</button>
    <button data-view='dispatcharr'>Dispatcharr</button>
    <button data-view='catalog'>Catálogo</button>
    <button data-view='validation'>Stream Validation</button>
    <button data-view='liverun'>Live Run</button>
    <button data-view='diagnostics'>Diagnóstico</button>
  </nav>

  <main>
    <!-- OVERVIEW -->
    <section id='view-overview'>
      <h2 style='font-size:18px;margin-top:0;'>Resumo do sistema</h2>
      <div class='grid' id='overviewCards'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Relações da última execução</h3>
      <div class='card'>
        <div id='runMath'></div>
      </div>
    </section>

    <!-- EXECUÇÕES -->
    <section id='view-executions' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Histórico de execuções (últimas 72h)</h2>
      <div class='toolbar'>
        <span class='muted' id='historyCount'></span>
        <button class='secondary' onclick='loadHistory()'>Recarregar</button>
      </div>
      <div id='historyTable'></div>
      <div id='historyDetail'></div>
    </section>

    <!-- DESCOBERTA -->
    <section id='view-discovery' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Descoberta</h2>
      <div class='toolbar'>
        <label class='muted'>Filtro estado:</label>
        <select id='discState'>
          <option value=''>todas</option>
          <option value='accepted'>aceites</option>
          <option value='rejected'>rejeitadas</option>
        </select>
        <label class='muted'>Origem:</label>
        <select id='discSource'><option value=''>todas</option></select>
        <label class='muted'>País:</label>
        <select id='discCountry'><option value=''>todos</option></select>
        <span class='muted' id='discMeta'></span>
      </div>
      <div id='discoveryTable'></div>
      <details>
        <summary>Ver entradas brutas (sem agregação)</summary>
        <div id='discoveryRaw' style='margin-top:8px;'></div>
      </details>
    </section>

    <!-- CANAIS / PAÍSES -->
    <section id='view-countries' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Canais / Países</h2>
      <h3 style='font-size:14px;'>Validação da playlist atual</h3>
      <div class='toolbar'>
        <label class='muted'>País:</label>
        <select id='countrySelect'></select>
        <button class='secondary' onclick='loadCountryValidation()'>Re-validar</button>
      </div>
      <div id='countryValidationResult'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Listas de canais por país (editáveis)</h3>
      <div id='countrySection'></div>
    </section>

    <!-- PLAYLIST -->
    <section id='view-playlist' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Playlist atual</h2>
      <div class='toolbar'>
        <a href='/api/playlist' target='_blank'>playlist.m3u (funcional)</a>
        <a href='/api/playlist_temp' target='_blank'>playlist_temp.m3u</a>
        <span class='muted' id='playlistInventory'></span>
      </div>
      <div class='card' style='margin-bottom:12px;'>
        <div class='muted' id='playlistMath'></div>
      </div>
      <h3 style='font-size:14px;'>Pré-visualização (URLs sanitizadas — sem credenciais Xtream)</h3>
      <pre id='playlistPreview'>a carregar…</pre>
    </section>

    <!-- DISPATCHARR -->
    <section id='view-dispatcharr' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Dispatcharr</h2>
      <div id='dispatcharrOverview'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Detalhes da última sincronização</h3>
      <div id='dispatcharrDetail'></div>
    </section>

    <!-- CATÁLOGO -->
    <section id='view-catalog' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Catálogo de Canais</h2>
      <div class='grid' id='catalogStats'></div>

      <h3 style='font-size:14px;margin-top:24px;'>Sub-separadores</h3>
      <nav id='catalogTabs' style='background:transparent;border-bottom:1px solid var(--border);padding:0;gap:4px;'>
        <button data-ctab='overview' class='active' style='padding:8px 14px;'>Visão Geral</button>
        <button data-ctab='channels' style='padding:8px 14px;'>Canais</button>
        <button data-ctab='rules' style='padding:8px 14px;'>Regras</button>
        <button data-ctab='affinity' style='padding:8px 14px;'>Afinidades</button>
        <button data-ctab='sources' style='padding:8px 14px;'>Sources</button>
        <button data-ctab='ordering' style='padding:8px 14px;'>Ordering</button>
        <button data-ctab='priority' style='padding:8px 14px;'>Source Priority</button>
        <button data-ctab='sourceselection' style='padding:8px 14px;'>Source Selection</button>
        <button data-ctab='matching' style='padding:8px 14px;'>Matching</button>
        <button data-ctab='degradation' style='padding:8px 14px;'>Degradação</button>
        <button data-ctab='scheduled' style='padding:8px 14px;'>Scheduled Jobs</button>
        <button data-ctab='policies' style='padding:8px 14px;'>Import Policies</button>
        <button data-ctab='groups' style='padding:8px 14px;'>Grupos</button>
        <button data-ctab='reviews' style='padding:8px 14px;'>Reviews</button>
        <button data-ctab='syncruns' style='padding:8px 14px;'>Sync Runs</button>
        <button data-ctab='pending' style='padding:8px 14px;'>Pending <span id='pendingBadge' class='badge warn' style='margin-left:4px;padding:1px 6px;border-radius:999px;font-size:10px;display:none;'>0</span></button>
      </nav>

      <!-- TAB: Visão Geral -->
      <div id='ctab-overview'>
        <div class='card' style='margin-top:16px;'>
          <h3>Estatísticas do Catálogo</h3>
          <div id='catalogStatsDetail'></div>
        </div>
      </div>

      <!-- TAB: Canais -->
      <div id='ctab-channels' hidden>
        <div style='margin-top:16px;'>
          <div class='toolbar'>
            <span class='muted' id='channelsCount'></span>
            <input id='channelsSearch' type='text' placeholder='Pesquisar por display name, key ou alias…' style='flex:1;min-width:200px;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
            <label class='muted'>Categoria:</label>
            <select id='channelsCategoryFilter'>
              <option value=''>todas</option>
              <option value='Live'>Live</option>
              <option value='Entretenimento'>Entretenimento</option>
              <option value='Desporto'>Desporto</option>
              <option value='Infantil'>Infantil</option>
              <option value='Documentarios'>Documentarios</option>
            </select>
            <label class='muted'>Estado:</label>
            <select id='channelsStatusFilter'>
              <option value=''>todos</option>
              <option value='enabled'>activos</option>
              <option value='disabled'>inactivos</option>
            </select>
            <label class='muted'>Política:</label>
            <select id='channelsPolicyFilter'>
              <option value=''>todas</option>
              <option value='CreateEligible'>CreateEligible</option>
              <option value='MergeOnly'>MergeOnly</option>
              <option value='ReviewOnly'>ReviewOnly</option>
              <option value='Excluded'>Excluded</option>
            </select>
            <button onclick='showCreateChannelForm()'>+ Novo Canal</button>
          </div>
          <div id='createChannelForm' hidden style='margin-bottom:16px;'>
            <div class='card'>
              <h3>Novo Canal Canónico</h3>
              <div style='display:grid;gap:10px;grid-template-columns:1fr 1fr;'>
                <div>
                  <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Key (slug único, imutável)</label>
                  <input id='newChannelKey' type='text' placeholder='ex: rtp-memoria' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                </div>
                <div>
                  <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Display Name</label>
                  <input id='newChannelDisplayName' type='text' placeholder='ex: RTP Memória' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                </div>
                <div>
                  <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>País (opcional, ex: pt)</label>
                  <input id='newChannelCountry' type='text' maxlength='10' placeholder='pt, es…' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                </div>
                <div>
                  <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Categoria</label>
                  <select id='newChannelCategory' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                    <option value='Live'>Live</option>
                    <option value='Entretenimento'>Entretenimento</option>
                    <option value='Desporto'>Desporto</option>
                    <option value='Infantil'>Infantil</option>
                    <option value='Documentarios'>Documentarios</option>
                  </select>
                </div>
                <div>
                  <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Grupo Editorial</label>
                  <select id='newChannelGroup' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                    <option value='PortugalLive'>PortugalLive</option>
                    <option value='PortugalFilmes24_7'>PortugalFilmes24_7</option>
                    <option value='PortugalEntretenimento'>PortugalEntretenimento</option>
                    <option value='PortugalDesporto'>PortugalDesporto</option>
                    <option value='PortugalInfantil'>PortugalInfantil</option>
                    <option value='PortugalDocumentarios'>PortugalDocumentarios</option>
                    <option value='PortugalPPV'>PortugalPPV</option>
                    <option value='Foreign'>Foreign</option>
                    <option value='Other'>Other</option>
                  </select>
                </div>
                <div>
                  <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Política de Publicação</label>
                  <select id='newChannelPolicy' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                    <option value='CreateEligible'>CreateEligible</option>
                    <option value='MergeOnly'>MergeOnly</option>
                    <option value='ReviewOnly'>ReviewOnly</option>
                    <option value='Excluded'>Excluded</option>
                  </select>
                </div>
                <div>
                  <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Activo</label>
                  <select id='newChannelEnabled' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                    <option value='true'>sim</option>
                    <option value='false'>não</option>
                  </select>
                </div>
                <div style='grid-column:1/-1;'>
                  <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Aliases normalizados (um por linha)</label>
                  <textarea id='newChannelAliases' rows='4' placeholder='rtp memoria' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;resize:vertical;'></textarea>
                </div>
              </div>
              <div style='margin-top:10px;display:flex;gap:8px;'>
                <button onclick='submitCreateChannel()'>Guardar</button>
                <button class='secondary' onclick='hideCreateChannelForm()'>Cancelar</button>
              </div>
            </div>
          </div>
          <div id='catalogChannelsTable'></div>
          <div id='channelDetailPanel' style='margin-top:24px;'></div>
        </div>
      </div>

      <!-- TAB: Regras -->
      <div id='ctab-rules' hidden>
        <div class='toolbar' style='margin-top:16px;'>
          <span class='muted' id='rulesCount'></span>
          <button onclick='showAddRuleForm()'>+ Nova Regra</button>
        </div>
        <div id='addRuleForm' hidden style='margin-bottom:16px;'>
          <div class='card'>
            <h3>Nova Regra de Identidade</h3>
            <div style='display:grid;gap:10px;grid-template-columns:1fr 1fr;'>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Identidade normalizada</label>
                <input id='ruleIdentity' type='text' placeholder='ex: sport tv nba' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Disposição</label>
                <select id='ruleDisposition' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                  <option value='ReviewOnly'>ReviewOnly</option>
                  <option value='Excluded'>Excluded</option>
                </select>
              </div>
              <div style='grid-column:1/-1;'>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Razão</label>
                <input id='ruleReason' type='text' placeholder='Razão textual (sem URLs)' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
            </div>
            <div style='margin-top:10px;display:flex;gap:8px;'>
              <button onclick='submitAddRule()'>Guardar</button>
              <button class='secondary' onclick='hideAddRuleForm()'>Cancelar</button>
            </div>
          </div>
        </div>
        <div id='catalogRulesTable'></div>
      </div>

      <!-- TAB: Afinidades -->
      <div id='ctab-affinity' hidden>
        <div class='toolbar' style='margin-top:16px;'>
          <span class='muted' id='affinityCount'></span>
          <button onclick='showAddAffinityForm()'>+ Novo Grupo</button>
        </div>
        <div class='card' style='margin-top:12px;'>
          <h4 style='margin:0 0 8px 0;'>Separador de variantes (global)</h4>
          <p class='muted' style='margin:0 0 8px 0;'>Separador usado no campo único de variantes. É apenas uma convenção de edição; as variantes são guardadas individualmente.</p>
          <div style='display:flex;gap:8px;align-items:center;'>
            <input id='affinityDelimiter' type='text' maxlength='3' value=',' style='width:80px;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
            <button class='secondary' onclick='saveAffinityDelimiter()'>Guardar separador</button>
          </div>
        </div>
        <div id='addAffinityForm' hidden style='margin-bottom:16px;'>
          <div class='card'>
            <h3 id='affinityFormTitle'>Nova Afinidade</h3>
            <div style='display:grid;gap:10px;grid-template-columns:1fr 1fr 1fr;'>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Tipo</label>
                <select id='affinityKind' onchange='onAffinityKindChange()' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                  <option value='channel'>Canal (variantes → canal canónico)</option>
                  <option value='country'>País (indicadores country-level)</option>
                </select>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Nome do grupo</label>
                <input id='affinityName' type='text' placeholder='ex: TVI' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div id='affinityCountryField' hidden>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Código do país</label>
                <input id='affinityCountryCode' type='text' maxlength='10' placeholder='pt, es, br...' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div id='affinityChannelField' style='grid-column:1/-1;'>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Canal canónico (catálogo por país)</label>
                <select id='affinityChannelKey' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'></select>
              </div>
              <div style='grid-column:1/-1;'>
                <label id='affinityVariantsLabel' style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Variantes do canal (separadas por ",")</label>
                <textarea id='affinityMembers' rows='5' placeholder='tvi24, tvi 24, tvi noticias' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;resize:vertical;'></textarea>
              </div>
            </div>
            <div style='margin-top:10px;display:flex;gap:8px;'>
              <button id='affinitySubmitBtn' onclick='submitAddAffinityGroup()'>Guardar</button>
              <button class='secondary' onclick='hideAddAffinityForm()'>Cancelar</button>
              <button class='secondary' id='affinityEditCancelBtn' hidden onclick='cancelAffinityEdit()'>Cancelar Edição</button>
            </div>
          </div>
        </div>
        <div id='catalogAffinityTable'></div>
      </div>

      <!-- TAB: Reviews -->
      <div id='ctab-reviews' hidden>
        <div class='toolbar' style='margin-top:16px;'>
          <span class='muted' id='reviewsCount'></span>
          <button class='secondary' onclick='loadCatalogReviews()'>Recarregar</button>
        </div>
        <div id='catalogReviewsTable'></div>
      </div>

      <!-- TAB: Sync Runs -->
      <div id='ctab-syncruns' hidden>
        <div class='toolbar' style='margin-top:16px;'>
          <span class='muted' id='syncRunsCount'></span>
          <button class='secondary' onclick='loadCatalogSyncRuns()'>Recarregar</button>
        </div>
        <div id='catalogSyncRunsTable'></div>
      </div>

      <!-- TAB: Sources (PHASE 4) -->
      <div id='ctab-sources' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Sources externas</h3>
          <p class='muted'>Cada <i>source</i> é uma origem (Telegram, M3U, Xtream, HTTP, ficheiro, manual) que contribui streams para o catálogo canónico. As credenciais são sanitizadas antes de persistir.</p>
          <div id='sourcesTable'></div>
          <div style='margin-top:16px;'>
            <h4 style='margin:0 0 8px 0;'>Registar source</h4>
            <div style='display:grid;gap:8px;grid-template-columns:1fr 1fr 1fr;'>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Key (slug único)</label>
                <input id='sourceKey' placeholder='ex: telegram-m3u8-pt' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Nome</label>
                <input id='sourceName' placeholder='ex: Telegram canal m3u8-pt' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Tipo</label>
                <select id='sourceKind' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                  <option value='M3U'>M3U</option>
                  <option value='Xtream'>Xtream</option>
                  <option value='Telegram'>Telegram</option>
                  <option value='Http'>Http</option>
                  <option value='File'>File</option>
                  <option value='Manual'>Manual</option>
                </select>
              </div>
              <div style='grid-column:1/-1;'>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Origem (URL/path/chat id)</label>
                <input id='sourceOrigin' placeholder='ex: https://example.com/playlist.m3u8' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Priority</label>
                <input id='sourcePriority' type='number' value='100' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Activo</label>
                <select id='sourceEnabled' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                  <option value='true'>sim</option>
                  <option value='false'>não</option>
                </select>
              </div>
            </div>
            <div style='margin-top:10px;display:flex;gap:8px;'>
              <button onclick='submitCreateSource()'>Guardar</button>
            </div>
          </div>
        </div>
        <div class='card' style='margin-top:16px;'>
          <h3>Streams associadas (Channel Sources)</h3>
          <p class='muted'>Cada <i>channel source</i> representa uma stream concreta, proveniente de uma source, associada a um canal canónico.</p>
          <div class='toolbar'>
            <label class='muted'>Source:</label>
            <select id='channelSourceSourceFilter'>
              <option value=''>todas</option>
            </select>
            <button class='secondary' onclick='loadChannelSources()'>Recarregar</button>
          </div>
          <div id='channelSourcesTable'></div>
        </div>
      </div>

      <!-- TAB: Ordering Lists (PHASE 5) -->
      <div id='ctab-ordering' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Ordering Lists</h3>
          <p class='muted'>Listas que determinam a ordem dos canais na playlist gerada. Cada lista pode ser duplicada, editada e usada como entrada para a composição.</p>
          <div id='orderingListsTable'></div>
          <div style='margin-top:16px;'>
            <h4 style='margin:0 0 8px 0;'>Nova lista</h4>
            <div style='display:grid;gap:8px;grid-template-columns:1fr 1fr 1fr;'>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Key (slug único)</label>
                <input data-ordering-create='key' placeholder='ex: pt-principal' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Nome</label>
                <input data-ordering-create='name' placeholder='ex: PT Principal' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>País (ISO)</label>
                <input data-ordering-create='country' placeholder='pt' maxlength='10' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
            </div>
            <div style='margin-top:10px;display:flex;gap:8px;'>
              <button onclick='submitCreateOrderingList()'>Guardar</button>
            </div>
          </div>
        </div>
        <div class='card' style='margin-top:16px;'>
          <h3>Detalhe da lista</h3>
          <div id='orderingDetail'></div>
        </div>
        <div class='card' style='margin-top:16px;'>
          <h3>Preview da playlist</h3>
          <p class='muted'>Aplica a política de Source Priority corrente sobre o conteúdo desta lista.</p>
          <div id='orderingPreview'></div>
        </div>
      </div>

      <!-- TAB: Source Priority (PHASE 6) -->
      <div id='ctab-priority' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Política global</h3>
          <p class='muted'>Aplica-se por defeito a todos os canais quando não há override por canal. Critérios são aplicados por ordem; o primeiro critério com resultados diferentes decide.</p>
          <div id='globalPriorityForm' style='display:grid;gap:8px;grid-template-columns:1fr 1fr;'></div>
          <div style='margin-top:10px;display:flex;gap:8px;'>
            <button onclick='saveGlobalPriority()'>Guardar política global</button>
          </div>
        </div>
        <div class='card' style='margin-top:16px;'>
          <h3>Override por canal</h3>
          <p class='muted'>Quando definido, este override substitui a política global para o canal seleccionado.</p>
          <div style='display:flex;gap:8px;align-items:center;margin-bottom:8px;'>
            <label class='muted'>Canal ID:</label>
            <input id='priorityChannelId' type='number' placeholder='ex: 1' style='background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
            <button class='secondary' onclick='loadChannelPriority()'>Carregar</button>
          </div>
          <div id='channelPriorityForm' style='display:grid;gap:8px;grid-template-columns:1fr 1fr;'></div>
          <div style='margin-top:10px;display:flex;gap:8px;'>
            <button onclick='saveChannelPriority()'>Guardar override</button>
          </div>
        </div>
      </div>

      <!-- TAB: Source Selection (PHASE 13, Wave 13-4) -->
      <div id='ctab-sourceselection' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Política global de selecção de fontes</h3>
          <p class='muted'>Aplica-se por defeito a todos os canais quando não há override por canal. <strong>MaxSourcesPerChannel = 0</strong> é uma definição deliberada e válida: nenhuma fonte seleccionada é publicada para os canais. Não são aceites valores negativos. MaxSourcesPerProvider em branco significa sem limite.</p>
          <div id='sourceSelectionPolicyForm' style='display:grid;gap:8px;grid-template-columns:1fr 1fr;'></div>
          <div style='margin-top:10px;display:flex;gap:8px;'>
            <button onclick='saveSourceSelectionPolicy()'>Guardar política</button>
            <button class='secondary' onclick='loadSourceSelectionPolicy()'>Recarregar</button>
          </div>
          <div id='sourceSelectionPolicyStatus' class='muted' style='margin-top:8px;'></div>
        </div>

        <div class='card' style='margin-top:16px;'>
          <h3>Override por canal</h3>
          <p class='muted'>Um override por canal <strong>substitui por completo a política global</strong> para esse canal. <strong>MaxSourcesPerChannel = 0</strong> é válido e significa que nenhuma fonte seleccionada é publicada para esse canal. Valores negativos são rejeitados. MaxSourcesPerProvider em branco significa sem limite. A identidade usada é a chave canónica do canal.</p>
          <div style='display:grid;gap:8px;grid-template-columns:1fr 1fr;'>
            <div>
              <label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Chave canónica do canal</label>
              <input id='cssp_channelKey' list='cssp_channelKeyList' placeholder='ex: sic-pt' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              <datalist id='cssp_channelKeyList'></datalist>
            </div>
            <div>
              <label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Max sources per channel (0 = não publicar nenhuma)</label>
              <input id='cssp_maxSourcesPerChannel' type='number' min='0' value='3' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
            </div>
            <div>
              <label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Max sources per provider (vazio = sem limite)</label>
              <input id='cssp_maxSourcesPerProvider' type='number' min='1' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
            </div>
            <div>
              <label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Prefer distinct providers</label>
              <select id='cssp_preferDistinctProviders'><option value='true'>sim</option><option value='false'>não</option></select>
            </div>
            <div>
              <label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Allow fallback to same provider</label>
              <select id='cssp_allowFallbackToSameProvider'><option value='true'>sim</option><option value='false'>não</option></select>
            </div>
          </div>
          <div style='margin-top:10px;display:flex;gap:8px;'>
            <button onclick='saveChannelSourceSelectionPolicy()'>Guardar override</button>
            <button class='secondary' onclick='loadChannelSourceSelectionPolicies()'>Recarregar</button>
          </div>
          <div id='channelSourceSelectionPolicyStatus' class='muted' style='margin-top:8px;'></div>
          <div id='channelSourceSelectionPoliciesTable' style='margin-top:12px;'></div>
        </div>
      </div>

      <!-- TAB: Matching (PHASE 3) -->
      <div id='ctab-matching' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Observabilidade de Matching</h3>
          <p class='muted'>Cada resolução de identidade por <code>CatalogResolver.ResolveAsync</code> é auditada. Esta página mostra as últimas decisões e a sua distribuição por caminho de decisão (Canonical / Alias / Rule / Unknown).</p>
          <div class='toolbar'>
            <label class='muted'>Canal ID:</label>
            <input id='matchingChannelId' type='number' placeholder='opcional' style='background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
            <label class='muted'>Limite:</label>
            <input id='matchingLimit' type='number' value='200' style='background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
            <button class='secondary' onclick='loadMatchingAudits()'>Aplicar</button>
            <button class='secondary' onclick='loadMatchingAudits()'>Recarregar</button>
          </div>
          <div id='matchingStats' class='muted' style='margin-top:10px;'></div>
        </div>
        <div class='card' style='margin-top:16px;'>
          <h3>Últimas decisões</h3>
          <div id='matchingAuditsTable'></div>
        </div>
      </div>

      <!-- TAB: Stream Degradation (PHASE 9 visão agregada) -->
      <div id='ctab-degradation' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Streams degradados</h3>
          <p class='muted'>Observações de <code>ChannelSource</code> que transitaram de saudável (<code>Validated/Reachable</code>) para terminal (<code>Dead/Unreachable/Timeout</code>) no lookback seleccionado. Ordenado por observação mais recente.</p>
          <div class='toolbar'>
            <label class='muted'>Lookback (min):</label>
            <input id='degLookback' type='number' value='10080' min='1' style='background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
            <label class='muted'>Limite:</label>
            <input id='degLimit' type='number' value='200' min='1' max='2000' style='background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
            <button class='secondary' onclick='loadDegradation()'>Aplicar</button>
            <button class='secondary' onclick='loadDegradation()'>Recarregar</button>
          </div>
          <div id='degradationStats' class='muted' style='margin-top:10px;'></div>
        </div>
        <div class='card' style='margin-top:16px;'>
          <h3>Streams com degradação</h3>
          <div id='degradationTable'></div>
        </div>
      </div>

      <!-- TAB: Scheduled Jobs (PHASE 12) -->
      <div id='ctab-scheduled' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Scheduled Jobs</h3>
          <p class='muted'>Jobs persistidos em SQLite. O scheduler calcula o próximo tick a partir da expressão cron (5 campos) e persiste <code>lastRunAtUtc</code> + <code>nextRunAtUtc</code>.</p>
          <div id='scheduledJobsTable'></div>
          <div style='margin-top:16px;'>
            <h4 style='margin:0 0 8px 0;'>Novo / actualizar job</h4>
            <div style='display:grid;gap:8px;grid-template-columns:1fr 1fr 1fr 1fr;'>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Name (único)</label>
                <input data-sched-create='name' placeholder='ex: hourly-telegram' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Cron (5 campos)</label>
                <input data-sched-create='cron' placeholder='ex: 0 * * * *' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Action Name</label>
                <input data-sched-create='action' placeholder='ex: discoverTelegram' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                <select data-sched-create='action-select' style='display:none;width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'></select>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Action Name</label>
                <input data-sched-create='action' placeholder='ex: discoverTelegram' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                <select data-sched-create='action-select' style='display:none;width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'></select>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Activo</label>
                <select data-sched-create='enabled' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
                  <option value='true'>sim</option>
                  <option value='false'>não</option>
                </select>
              </div>
            </div>
            <div style='margin-top:10px;display:flex;gap:8px;'>
              <button onclick='submitCreateScheduledJob()'>Guardar</button>
            </div>
          </div>
        </div>
      </div>

      <!-- TAB: Import Policies (PHASE 8) -->
      <div id='ctab-policies' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Import Policies (Live / Radio / VOD)</h3>
          <p class='muted'>Define, por tipo de media, quais os grupos alvo e quais os excluídos. VOD tem ainda a política <i>Import / Keep / Exclude</i> separada de Linear TV.</p>
          <div id='importPoliciesTable'></div>
        </div>
      </div>

      <!-- TAB: Groups (PHASE 8) -->
      <div id='ctab-groups' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Grupos canónicos</h3>
          <p class='muted'>Os grupos canónicos deixam de ser apenas enums rígidos. São entidades persistentes, configuráveis e associáveis a group-titles de cada source via mapping explícito.</p>
          <div id='canonicalGroupsTable'></div>
          <div style='margin-top:16px;'>
            <h4 style='margin:0 0 8px 0;'>Novo grupo</h4>
            <div style='display:grid;gap:8px;grid-template-columns:1fr 1fr 1fr 1fr;'>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Key</label>
                <input data-group-create='key' placeholder='ex: portugal-live' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Display Name</label>
                <input data-group-create='name' placeholder='ex: Portugal Live' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>País (ISO)</label>
                <input data-group-create='country' placeholder='pt' maxlength='10' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Ordem</label>
                <input data-group-create='order' type='number' value='100' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
            </div>
            <div style='margin-top:10px;display:flex;gap:8px;'>
              <button onclick='submitCreateGroup()'>Guardar</button>
            </div>
          </div>
        </div>
        <div class='card' style='margin-top:16px;'>
          <h3>Group Mappings</h3>
          <p class='muted'>Associa <i>group-titles</i> de uma source a um grupo canónico. Nunca é automático — exige mapping explícito.</p>
          <div id='groupMappingsTable'></div>
          <div style='margin-top:16px;'>
            <h4 style='margin:0 0 8px 0;'>Novo mapping</h4>
            <div style='display:grid;gap:8px;grid-template-columns:1fr 1fr 1fr;'>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Source Kind</label>
                <select data-mapping-create='kind'>
                  <option value='M3U'>M3U</option>
                  <option value='Xtream'>Xtream</option>
                  <option value='Telegram'>Telegram</option>
                  <option value='Http'>Http</option>
                  <option value='File'>File</option>
                  <option value='Manual'>Manual</option>
                </select>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Source Group Title</label>
                <input data-mapping-create='title' placeholder='ex: PORTUGAL SPORTS' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
              <div>
                <label style='display:block;color:var(--muted);font-size:12px;margin-bottom:4px;'>Canonical Group ID</label>
                <input data-mapping-create='groupId' type='number' placeholder='id' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
              </div>
            </div>
            <div style='margin-top:10px;display:flex;gap:8px;'>
              <button onclick='submitCreateGroupMapping()'>Guardar</button>
            </div>
          </div>
        </div>
      </div>

      <!-- TAB: Pending Country Approvals -->
      <div id='ctab-pending' hidden>
        <div class='card' style='margin-top:16px;'>
          <h3>Canais para Aprovação Manual</h3>
          <p class='muted' style='margin:8px 0 16px;'>Canais que geraram dúvida no country-level targeting (e.g. contêm "PT" mas não correspondem a um canal canónico conhecido). Se um canal for aceite, é adicionada uma IdentityRule com ReviewOnly que permite fuzzy matching. Se for reprovado, é criada uma IdentityRule com Excluded.</p>
        </div>
        <div class='toolbar' style='margin-top:16px;'>
          <span class='muted' id='pendingCount'></span>
          <button class='secondary' onclick='loadPendingCountryApprovals()'>Recarregar</button>
        </div>
        <div id='pendingCountryApprovalsTable'></div>
      </div>
    </section>

    <!-- STREAM VALIDATION (PHASE 9A) -->
    <section id='view-validation' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Stream Validation</h2>
      <p class='muted'>Política operacional do teste de streams. Os valores são persistidos em <code>runtime-data/stream_validation_policy.json</code>.</p>
      <div class='card'>
        <h3>Política de validação</h3>
        <div id='validationPolicyForm' style='display:grid;gap:12px;grid-template-columns:1fr 1fr;'></div>
        <div style='margin-top:12px;display:flex;gap:8px;'>
          <button onclick='saveValidationPolicy()'>Guardar política</button>
          <button class='secondary' onclick='loadValidationPolicy()'>Recarregar</button>
        </div>
        <div id='validationPolicyStatus' class='muted' style='margin-top:8px;'></div>
      </div>
      <div class='card' style='margin-top:16px;'>
        <h3>Dry-run</h3>
        <p class='muted'>Testa uma lista de URLs com a política corrente. Útil para afinar concorrência/TTL/early-exit.</p>
        <textarea id='validationTestUrls' rows='4' placeholder='https://example.com/stream.ts (uma por linha)' style='width:100%;'></textarea>
        <div style='margin-top:8px;display:flex;gap:8px;'>
          <button onclick='runValidationTest()'>Testar URLs</button>
        </div>
        <div id='validationTestResult' class='muted' style='margin-top:8px;'></div>
      </div>
    </section>

    <!-- DIAGNÓSTICO -->
    <section id='view-diagnostics' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Diagnóstico</h2>
      <div id='diagLastRun'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Inventário de output/</h3>
      <div id='diagInventory'></div>
      <h3 style='font-size:14px;margin-top:24px;'>End-to-end (raw RunReport)</h3>
      <details><summary>Ver RunReport completo</summary><pre id='diagRawRunReport'>a carregar…</pre></details>
      <h3 style='font-size:14px;margin-top:24px;'>Glossário de métricas</h3>
      <div id='diagGlossary'></div>
    </section>

    <!-- LIVE RUN (PHASE 9C.4) -->
    <section id='view-liverun' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Live Run</h2>
      <div class='toolbar'>
        <span class='muted' id='liveRunPollState'>a actualizar automaticamente…</span>
        <button id='liveRunStartBtn' onclick='startLiveRun()' disabled>Run now</button>
        <button class='secondary' onclick='loadLiveRun()'>Recarregar</button>
      </div>
      <div class='card' style='margin-bottom:12px;'>
        <div id='liveRunTriggerState' class='muted'></div>
      </div>
      <div id='liveRunStatus'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Contadores</h3>
      <div class='grid' id='liveRunCounts'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Últimas actividades</h3>
      <div id='liveRunActivities' class='muted'>—</div>
      <h3 style='font-size:14px;margin-top:24px;'>Últimas execuções (24h)</h3>
      <div id='liveRunRecent'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Execução agendada (Telegram)</h3>
      <div class='muted' style='margin-bottom:8px;'>
        O agendamento usa as expressões cron existentes em
        <b>Scheduled Jobs</b> com as acções <code>telegramRun</code> e
        <code>telegramMaintainRun</code>. Não há um segundo scheduler nem
        <code>StartAtUtc</code>: a UI calcula a expressão cron.
      </div>
      <div id='liveRunScheduled'></div>
    </section>
  </main>

  <script>
  (function(){
    const fmt = (v) => (v === undefined || v === null) ? '—' : v;
    const nfmt = (n) => new Intl.NumberFormat('pt-PT').format(n);
    const pct = (n) => new Intl.NumberFormat('pt-PT', { maximumFractionDigits: 1, minimumFractionDigits: 1 }).format(n) + '%';
    const tsLocal = (s) => s ? new Date(s).toLocaleString() : '—';

    function metricCard(title, value, sub, help) {
      return `<div class='card'><h3>${title}</h3><div class='value'>${value}</div>${sub ? `<div class='sub'>${sub}</div>` : ''}${help ? `<div class='help'>${help}</div>` : ''}</div>`;
    }

    function helpFor(key) {
      const map = {
        candidates: 'URLs/attachments identificados como potenciais M3U antes do download.',
        playlists: 'Playlists distintas descarregadas com sucesso (passaram o gate #EXTM3U).',
        streams: 'Entradas EXTINF extraídas, antes do filtro por país.',
        streamsAfter: 'Streams que sobraram depois do filtro por país (candidatos a teste).',
        tested: 'Streams aos quais foi feito um teste HTTP.',
        working: 'Streams que responderam OK.',
        failed: 'Streams cujo teste falhou.',
        durationMs: 'Duração total da execução em ms.'
      };
      return map[key] || '';
    }

    async function safeFetchJson(url, fallback) {
      try { const r = await fetch(url); if (!r.ok) return fallback || { error: `HTTP ${r.status}` }; return await r.json(); }
      catch (e) { return fallback || { error: e.message }; }
    }

    async function loadOverview() {
      const [run, hist, dispatcharr, inv] = await Promise.all([
        safeFetchJson('/api/run-report/summary', null),
        safeFetchJson('/api/history', []),
        safeFetchJson('/api/dispatcharr/state', null),
        safeFetchJson('/api/output/inventory', {})
      ]);

      // Cabeçalho
      const lastRunTs = (run && run.startedAtUtc) ? tsLocal(run.startedAtUtc) : '—';
      document.getElementById('metaLine').textContent = `Última execução: ${lastRunTs}`;

      const last = (hist && hist.length) ? hist[0] : null;
      const finalCount = last ? last.finalPlaylistCount : '—';
      const histoHours = last ? (last.historyHours + 'h') : '—';

      const cards = [];
      cards.push(metricCard('Última execução', lastRunTs, run ? ('estado: ' + (run.status || '—')) : 'sem run report', ''));
      cards.push(metricCard('Duração', run ? (run.durationMs != null ? (nfmt(run.durationMs) + ' ms') : '—') : '—', run && run.durationMs ? ((run.durationMs/1000).toFixed(1) + ' s') : '', helpFor('durationMs')));
      cards.push(metricCard('playlist.m3u', finalCount + ' streams', last ? ('janela ' + histoHours) : '', 'Streams actualmente publicados.'));
      cards.push(metricCard('playlist.m3u (bytes)', inv['playlist.m3u'] ? nfmt(inv['playlist.m3u']) + ' B' : '—', '', 'Tamanho do ficheiro actual.'));
      if (run) {
        const w = run.streamsWorking, f = run.streamsFailed, t = run.streamsTested;
        const rate = (w + f) > 0 ? (100 * w / (w + f)) : null;
        const sub = rate != null ? (pct(rate) + ' de sucesso · ' + nfmt(w) + ' OK / ' + nfmt(f) + ' KO') : '—';
        cards.push(metricCard('Última: testados / funcionais', nfmt(t) + ' · ' + nfmt(w), sub, helpFor('working')));
        cards.push(metricCard('Última: candidatos', nfmt(run.candidates || 0), 'playlists: ' + nfmt(run.playlistsDownloaded || 0), helpFor('candidates')));
      } else {
        cards.push(metricCard('Última execução (resumo)', '—', 'sem run report disponível', ''));
      }
      cards.push(metricCard('Última sync Dispatcharr', dispatcharr && dispatcharr.startedAtUtc ? tsLocal(dispatcharr.startedAtUtc) : '—',
        dispatcharr && dispatcharr.dispatchedDetailDisabled ? '' :
          (dispatcharr && dispatcharr.dispatcharrVersion ? ('versão ' + dispatcharr.dispatcharrVersion) : (dispatcharr ? (dispatcharr.reason || '—') : '')),
        'Sincronização opt-in (dispatcharr_enabled=true em wtelegram.config).'));
      document.getElementById('overviewCards').innerHTML = cards.join('');

      // Relações matemáticas
      let math = '<p class="muted">Sem dados do último run.</p>';
      if (run) {
        const w = run.streamsWorking || 0, f = run.streamsFailed || 0, t = run.streamsTested || 0;
        const tested = w + f;
        const balanced = tested === t;
        const rate = tested > 0 ? (100 * w / tested) : null;
        const failRate = tested > 0 ? (100 * f / tested) : null;
        const candidates = run.candidates || 0;
        const playlistsDl = run.playlistsDownloaded || 0;
        const playlistsRej = run.playlistsRejected || 0;
        const streams = run.streamsExtracted || 0;
        const streamsAfter = run.streamsAfterCountryFilter || 0;
        const rejectedByCountry = run.streamsRejectedByCountry || 0;
        const messages = run.messages || 0;

        // NOTA: a seta "→" representa uma CONTAGEM ao longo do pipeline, não igualdade.
        //   Messages -> Candidates           (sub-conjunto,scan)
        //   Candidates = PlaylistsDownloaded + PlaylistsInvalid
        //                                       (PlaylistsInvalid = #EXTM3U gate falhado)
        //   PlaylistsDownloaded = CountryMatches + PlaylistsRejected
        //                                       (PlaylistsRejected mistura
        //                                        fast-reject E 0-streams-após-filtro)
        //   StreamsExtracted conta APENAS streams de playlists COM país-alvo aceite.
        //   Por isso StreamsExtracted << PlaylistsDownloaded quando há playlists rejeitadas.
        math = `
          <p><strong>Cadeia (cada → não é igualdade):</strong></p>
          <p>${nfmt(messages)} mensagens Telegram → ${nfmt(candidates)} candidatos → ${nfmt(playlistsDl)} playlists OK (+ ${nfmt(playlistsRej)} rejeitadas) → ${nfmt(streams)} streams (em playlists OK) → ${nfmt(streamsAfter)} após filtro por país → ${nfmt(t)} testados</p>
          <h4 style='margin:12px 0 4px;'>Testes</h4>
          ${t > 0 ? `
            <p>${nfmt(t)} streams testados = ${nfmt(w)} funcionais + ${nfmt(f)} falhados
              ${balanced ? '' : `<span class='badge warn' title='Funcionais+Falhados ≠ Testados (streams pulados)'>⚠ ${nfmt(t - tested)} não testados / pulados</span>`}
            </p>
            <div class='bar' title='${pct(rate ?? 0)} de sucesso'>
              <div class='ok' style='width:${rate}%;'></div>
              <div class='err' style='width:${failRate}%;'></div>
            </div>
            <p class='row-counts'>taxa de sucesso: ${rate != null ? pct(rate) : '—'} · taxa de falha: ${failRate != null ? pct(failRate) : '—'}</p>
          ` : '<p class="muted">Sem streams testados neste run.</p>'}
          <h4 style='margin:16px 0 4px;'>Filtro por país (per-stream)</h4>
          <p>${nfmt(streamsAfter)} streams seleccionados · ${nfmt(rejectedByCountry)} rejeitados por país</p>
          <p class='row-counts'>Sumido entre passos: ${nfmt(playlistsRej)} playlists não passaram o gate (incl. fast-reject) não contribuem para ${nfmt(streams)}.</p>
        `;
      }
      document.getElementById('runMath').innerHTML = math;
    }

    let historyCache = [];
    async function loadHistory() {
      const items = await safeFetchJson('/api/history', []);
      historyCache = Array.isArray(items) ? items : [];
      document.getElementById('historyCount').textContent = historyCache.length + ' execuções listadas.';
      if (!historyCache.length) { document.getElementById('historyTable').innerHTML = '<p class="muted">Nenhum histórico encontrado.</p>'; return; }
      const rows = historyCache.map((e, idx) => {
        const ts = new Date(e.timestamp).toLocaleString();
        const modeBadge = `<span class='badge ${e.mode === 'TelegramMaintenance' ? 'info' : 'muted'}'>${e.mode || ''}</span>`;
        const ratio = e.existingRetestedCount > 0 ? (e.existingStillWorkingCount + '/' + e.existingRetestedCount) : '—';
        return `<tr data-idx='${idx}' style='cursor:pointer'>
          <td>${ts}</td>
          <td>${modeBadge}</td>
          <td>${e.searchTerm || '—'}</td>
          <td>${e.historyHours}h</td>
          <td>${e.maxStreams || '—'}</td>
          <td>${e.newFunctionalCount}</td>
          <td>${ratio}</td>
          <td>${e.finalPlaylistCount}</td>
        </tr>`;
      }).join('');
      document.getElementById('historyTable').innerHTML =
        '<table><thead><tr><th>Quando</th><th>Modo</th><th>Pesquisa</th><th>História</th><th>Máx</th><th>Novos</th><th>Retestados</th><th>Total final</th></tr></thead><tbody>' + rows + '</tbody></table>';
      document.querySelectorAll('#historyTable tr[data-idx]').forEach(tr => tr.addEventListener('click', () => showHistoryDetail(parseInt(tr.getAttribute('data-idx'), 10))));
    }

    async function showHistoryDetail(idx) {
      if (idx < 0 || idx >= historyCache.length) return;
      const entry = historyCache[idx];
      const detail = await safeFetchJson(`/api/execution/${idx + 1}`, null);
      const e = detail || entry;
      const html = `
        <div class='card' style='margin-top:12px;'>
          <h3>Execução #${idx + 1} — ${new Date(e.timestamp).toLocaleString()}</h3>
          <table>
            <tr><th>Modo</th><td>${fmt(e.mode)}</td><th>Pesquisa</th><td>${fmt(e.searchTerm)}</td></tr>
            <tr><th>Janela</th><td>${fmt(e.historyHours)} h</td><th>Máx streams</th><td>${fmt(e.maxStreams)}</td></tr>
            <tr><th>Mensagens analisadas</th><td>${nfmt(e.messagesAnalyzed || 0)}</td><th>Candidatos</th><td>${nfmt(e.candidatesFound || 0)}</td></tr>
            <tr><th>Playlists</th><td>${nfmt(e.playlistsDownloaded || 0)}</td><th>Rejeitadas</th><td>${nfmt(e.playlistsRejected || 0)}</td></tr>
            <tr><th>Country matches</th><td>${nfmt(e.countryMatches || 0)}</td><th>Streams extraídos</th><td>${nfmt(e.streamsExtracted || 0)}</td></tr>
            <tr><th>Após filtro país</th><td>${nfmt(e.streamsAfterCountryFilter || 0)}</td><th>Rejeitados país</th><td>${nfmt(e.streamsRejectedByCountry || 0)}</td></tr>
            <tr><th>Testados</th><td>${nfmt(e.streamsTested || 0)}</td><th>Funcionais</th><td>${nfmt(e.streamsWorking || 0)}</td></tr>
            <tr><th>Falhados</th><td>${nfmt(e.streamsFailed || 0)}</td><th>Total final</th><td>${nfmt(e.finalPlaylistCount || 0)}</td></tr>
          </table>
          <p class='row-counts'>Detalhes crus (apenas leitura) preservados para auditoria.</p>
        </div>`;
      document.getElementById('historyDetail').innerHTML = html;
    }

    let discoveryCache = { total: 0, distinct: 0, items: [] };
    async function loadDiscovery() {
      const sum = await safeFetchJson('/api/discovery/summary', { items: [] });
      discoveryCache = sum || { items: [] };
      const items = discoveryCache.items || [];
      document.getElementById('discMeta').textContent =
        `${items.length} entradas distintas (de ${discoveryCache.total || items.length} brutas, ${discoveryCache.duplicatesCollapsed || 0} duplicações agregadas)`;

      const sources = [...new Set(items.map(i => i.source).filter(Boolean))].sort();
      const countries = [...new Set(items.map(i => i.countryDetected).filter(Boolean))].sort();
      const srcSel = document.getElementById('discSource');
      const coSel = document.getElementById('discCountry');
      srcSel.innerHTML = '<option value="">todas</option>' + sources.map(s => `<option value="${s.replace(/"/g,'&quot;')}">${s}</option>`).join('');
      coSel.innerHTML = '<option value="">todos</option>' + countries.map(c => `<option value="${c.replace(/"/g,'&quot;')}">${c}</option>`).join('');

      renderDiscovery();
      const raw = await safeFetchJson('/api/discovered-playlists', []);
      if (Array.isArray(raw)) {
        document.getElementById('discoveryRaw').innerHTML = '<pre>' + JSON.stringify(raw, null, 2).slice(0, 20000) + '</pre>';
      }
    }

    function renderDiscovery() {
      const state = document.getElementById('discState').value;
      const source = document.getElementById('discSource').value;
      const country = document.getElementById('discCountry').value;
      const items = (discoveryCache.items || []).filter(i =>
        (!state || i.state === state) &&
        (!source || i.source === source) &&
        (!country || i.countryDetected === country)
      );
      const rows = items.map((p, idx) => {
        const stateBadge = p.state === 'accepted'
          ? '<span class="badge ok">aceite</span>'
          : '<span class="badge err">rejeitada</span>';
        return `<tr>
          <td>${p.source || '—'}</td>
          <td>${p.name || '—'}</td>
          <td>${p.countryDetected || '—'}</td>
          <td>${nfmt(p.channelsRecognized || 0)}</td>
          <td>${nfmt(p.streamCount || 0)}</td>
          <td>${nfmt(p.streamsAfterCountryFilter || 0)}</td>
          <td>${nfmt(p.workingStreams || 0)}</td>
          <td>${stateBadge}</td>
          <td>${p.occurrences > 1 ? '<span class="badge warn" title="Mesma (origem, nome) em múltiplas linhas do RunReport">×' + p.occurrences + '</span>' : '—'}</td>
        </tr>`;
      }).join('');
      document.getElementById('discoveryTable').innerHTML = items.length
        ? `<table><thead><tr><th>Origem</th><th>Nome</th><th>País</th><th>Canais</th><th>Streams</th><th>Após país</th><th>Funcionais</th><th>Estado</th><th>Notas</th></tr></thead><tbody>${rows}</tbody></table>`
        : '<p class="muted">Nenhuma playlist encontrada para os filtros escolhidos.</p>';
    }

    let countryOptions = [];
    async function loadCountries() {
      const res = await safeFetchJson('/api/countries', []);
      countryOptions = res || [];
      const countrySelect = document.getElementById('countrySelect');
      countrySelect.innerHTML = countryOptions.map(c => `<option value='${c.country}'>${c.displayName || c.country}</option>`).join('');
      countrySelect.value = countryOptions[0]?.country || 'pt';
      if (!countryOptions.length) {
        document.getElementById('countrySection').innerHTML = '<p class="muted">Nenhuma lista de canais encontrada.</p>';
        document.getElementById('countryValidationResult').innerHTML = '<p class="muted">Sem país disponível para validação.</p>';
        return;
      }
      const countryCards = countryOptions.map(c => `
        <div class='card' style='margin-bottom:12px;'>
          <h3>${c.displayName || c.country}</h3>
          <textarea id='country-${c.country}' rows='6'>${(c.channels || []).join('\n')}</textarea>
          <div style='margin-top:8px;'><button class='secondary' data-country='${c.country}'>Guardar</button></div>
        </div>`).join('');
      document.getElementById('countrySection').innerHTML = countryCards;
      document.querySelectorAll('button[data-country]').forEach(btn => {
        btn.addEventListener('click', async () => {
          const code = btn.getAttribute('data-country');
          const t = document.getElementById(`country-${code}`);
          const channels = t.value.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
          const r = await fetch('/api/country/save', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ country: code, displayName: code.toUpperCase(), channels }) });
          if (r.ok) { alert('Lista guardada.'); await loadCountries(); } else { alert('Erro: ' + (await r.text())); }
        });
      });
      await loadCountryValidation();
    }

    async function loadCountryValidation() {
      const code = document.getElementById('countrySelect').value;
      const res = await safeFetchJson(`/api/country/validate?country=${encodeURIComponent(code)}`, null);
      const target = document.getElementById('countryValidationResult');
      if (!res || res.error) { target.innerHTML = '<p class="muted">Sem dados de validação.</p>'; return; }
      const detail = res.isMatch
        ? `<p><span class='badge ok'>correspondência</span> em ${res.displayName || res.country}.</p><p>Aliases detectados: ${(res.matchedAliases || []).join(', ') || '—'}</p>`
        : `<p><span class='badge warn'>sem correspondência</span> em ${res.displayName || res.country}.</p><p>Aliases esperados: ${(res.matchedAliases || []).join(', ') || '—'}</p>`;
      // As três grandezas NÃO representam a mesma colecção de coisas:
      //   recognized  = nº de canais canónicos distintos reconhecidos NA PLAYLIST actual
      //   threshold   = mínimo para o país ser considerado alvo (gate do AnalyzePlaylist)
      //   totalChannels = nº de aliases configurados em runtime-data/countries/<code>.json
      // Por isso não calculamos "cobertura" como recognized/total — esse rácio mistura
      // colecções diferentes. Cada número é apresentado em separado.
      target.innerHTML = `
        <div class='card'>
          <h3>Resultado da validação — ${res.displayName || res.country}</h3>
          ${detail}
          <p><strong>Reconhecidos na playlist:</strong> ${nfmt(res.recognizedChannelCount || 0)} canais canónicos distintos</p>
          <p><strong>Threshold (gate do país-alvo):</strong> ${res.threshold || 3}</p>
          <p><strong>Aliases configurados:</strong> ${nfmt(res.totalChannels || 0)} (em <code>runtime-data/countries/${code}.json</code>)</p>
          <p class='row-counts'>tamanho playlist: ${nfmt(res.playlistLength || 0)} bytes</p>
        </div>`;
    }

    async function loadPlaylist() {
      try {
        const inv = await safeFetchJson('/api/output/inventory', {});
const rows = Object.entries(inv).map(([k, v]) => {
          if (!v || !v.present) return `<tr><td>${k}</td><td><span class="badge muted">ausente</span></td></tr>`;
          return `<tr><td>${k}</td><td>${nfmt(v.size)} B <span class="row-counts">(${new Date(v.lastWriteUtc).toLocaleString()})</span></td></tr>`;
        }).join('');
        document.getElementById('playlistInventory').innerHTML = '· ' + Object.entries(inv).map(([k, v]) => v && v.present ? `${k}=${nfmt(v.size)}B` : `${k}=ausente`).join(' · ');
        document.getElementById('diagInventory').innerHTML = `<table><thead><tr><th>Ficheiro</th><th>Tamanho</th></tr></thead><tbody>${rows}</tbody></table>`;
      } catch (e) {}
      try {
        const r = await fetch('/api/playlist/preview');
        const txt = r.ok ? (await r.text()) : 'Playlist não disponível.';
        const lines = txt.split('\n').filter(Boolean).slice(0, 80);
        const streamCount = (txt.match(/^#EXTINF/gm) || []).length;
        document.getElementById('playlistMath').innerHTML = `
          <p><strong>${nfmt(streamCount)}</strong> entradas #EXTINF na pré-visualização.
          URLs foram sanitizadas neste endpoint — credenciais Xtream nunca aparecem.</p>`;
        document.getElementById('playlistPreview').textContent = lines.join('\n');
      } catch (e) {
        document.getElementById('playlistPreview').textContent = 'Erro ao carregar playlist.';
      }
    }

    async function loadDispatcharr() {
      const s = await safeFetchJson('/api/dispatcharr/state', null);
      const target = document.getElementById('dispatcharrOverview');
      const detail = document.getElementById('dispatcharrDetail');
      if (!s || s.reason) {
        target.innerHTML = `
          <div class='card'>
            <h3>Sincronização Dispatcharr</h3>
            <p><span class='badge warn'>não activa</span></p>
            <p>${s && s.reason ? s.reason : 'Sem dados.'}</p>
            <p class='row-counts'>A integração é opt-in (wtelegram.config: <code>dispatcharr_enabled=true</code>).
            Nenhuma chave ainda foi escrita, ou o sync opt-in nunca correu.</p>
          </div>`;
        detail.innerHTML = '';
        return;
      }
      const cards = [];
      cards.push(metricCard('Última execução', tsLocal(s.startedAtUtc), s.dryRun ? '<span class="badge info">dry-run</span>' : '<span class="badge warn">apply</span>', ''));
      cards.push(metricCard('Versão Dispatcharr', s.dispatcharrVersion || '—', '', 'Obtida de GET /api/core/version/ no início da run (best-effort).'));
      cards.push(metricCard('Total de canais no plano', nfmt(s.totalChannels || 0), 'matched + new + unchanged + ...', ''));
      cards.push(metricCard('Matched (canais)', nfmt(s.matched || 0), 'canal existente reutilizado', ''));
      cards.push(metricCard('New channels', nfmt(s.newChannels || 0), 'canais novos a criar', ''));
      cards.push(metricCard('New streams', nfmt(s.newStreams || 0), 'streams novos a anexar', ''));
      cards.push(metricCard('Removed streams', nfmt(s.removedStreams || 0), 'a desassociar e apagar', ''));
      cards.push(metricCard('Ambiguous', nfmt(s.ambiguous || 0), 'nunca aplicados automaticamente', ''));
      cards.push(metricCard('Skipped', nfmt(s.skipped || 0), 'streams não-workings na playlist', ''));
      cards.push(metricCard('Failed', nfmt(s.failed || 0), '', ''));
      target.innerHTML = `<div class='grid'>${cards.join('')}</div>`;

      // Sinaliza se plan e report pertencem à mesma execução (por timestamp).
      const pairingNote = s.planReportPaired
        ? 'plan + report emparelhados (mesma execução)'
        : '<span class="badge warn">plan e report NÃO foram emparelhados pelo timestamp</span>';

      detail.innerHTML = `
        <div class='card'>
          <h3>Ficheiros produzidos</h3>
          ${s.latestPlanPath ? `<p><strong>Plano:</strong> <code>${s.latestPlanPath}</code></p>` : '<p><em>Sem plano (sync opt-in nunca correu ou foi interrompido).</em></p>'}
          ${s.latestReportPath ? `<p><strong>Relatório:</strong> <code>${s.latestReportPath}</code></p>` : '<p><em>Sem relatório.</em></p>'}
          <p>${pairingNote}</p>
          ${(s.planValid === false || s.reportValid === false) ? `<p class='row-counts'><span class='badge err'>ficheiro(s) com JSON inválido</span>: ${s.error || ''}</p>` : ''}
          <p class='row-counts'>Os ficheiros ficam em <code>output/</code> (bind mount <code>/opt/playlists</code>).
          Ver <code>dispatcharr_plan_*.json</code> e <code>dispatcharr_report_*.json</code> mais recentes.
          Estes ficheiros nunca contêm credenciais em claro (sanitização automática).</p>
        </div>`;
    }

    async function loadDiagnostics() {
      const r = await safeFetchJson('/api/run-report/summary', null);
      const target = document.getElementById('diagLastRun');
      if (!r || r.error) { target.innerHTML = '<p class="muted">Sem run report.</p>'; }
      else {
        target.innerHTML = `
          <div class='card'>
            <h3>Última execução (resumo)</h3>
            <table>
              <tr><th>Início</th><td>${tsLocal(r.startedAtUtc)}</td><th>Fim</th><td>${tsLocal(r.finishedAtUtc)}</td></tr>
              <tr><th>Estado</th><td><span class='badge ${r.status === "ok" ? "ok" : (r.status === "sem-streams" ? "warn" : "err")}'>${r.status}</span></td><th>Duração</th><td>${nfmt(r.durationMs || 0)} ms</td></tr>
              <tr><th>Mensagens</th><td>${nfmt(r.messages || 0)}</td><th>Candidatos</th><td>${nfmt(r.candidates || 0)}</td></tr>
              <tr><th>Playlists OK</th><td>${nfmt(r.playlistsDownloaded || 0)}</td><th>Playlists inválidas</th><td>${nfmt(r.playlistsInvalid || 0)}</td></tr>
              <tr><th>Rejeitadas por país</th><td>${nfmt(r.playlistsRejected || 0)}</td><th>Country matches</th><td>${nfmt(r.countryMatches || 0)}</td></tr>
              <tr><th>Streams extraídos</th><td>${nfmt(r.streamsExtracted || 0)}</td><th>Após país</th><td>${nfmt(r.streamsAfterCountryFilter || 0)}</td></tr>
              <tr><th>Rejeitados país</th><td>${nfmt(r.streamsRejectedByCountry || 0)}</td><th>Testados</th><td>${nfmt(r.streamsTested || 0)}</td></tr>
              <tr><th>Funcionais</th><td>${nfmt(r.streamsWorking || 0)}</td><th>Falhados</th><td>${nfmt(r.streamsFailed || 0)}</td></tr>
              <tr><th>Taxa de sucesso</th><td>${r.successRatePercent != null ? pct(r.successRatePercent) : '—'}</td><th>Testados balanceados</th><td>${r.testsBalanced ? '<span class="badge ok">sim</span>' : '<span class="badge warn">não (W+F≠T)</span>'}</td></tr>
            </table>
          </div>`;
        try {
          const raw = await fetch('/api/run-report');
          if (raw.ok) document.getElementById('diagRawRunReport').textContent = JSON.stringify(await raw.json(), null, 2);
        } catch(e) {}
      }
      const inv = await safeFetchJson('/api/output/inventory', {});
      const rows = Object.keys(inv).map(k => `<tr><td>${k}</td><td>${typeof inv[k] === 'number' ? (nfmt(inv[k]) + ' B') : (inv[k] === false ? 'ausente' : inv[k])}</td></tr>`).join('');
      document.getElementById('diagInventory').innerHTML = `<table><thead><tr><th>Ficheiro</th><th>Tamanho</th></tr></thead><tbody>${rows}</tbody></table>`;

      // Glossário
      const gloss = Object.entries({
        candidates: 'URLs/attachments identificados como potenciais M3U antes do download.',
        playlistsDownloaded: 'Playlists distintas descarregadas com sucesso (passaram o gate #EXTM3U).',
        playlistsInvalid: 'Descartadas por não serem playlists M3U válidas.',
        playlistsRejected: 'Rejeitadas pelo filtro por país (fast-reject).',
        streamsExtracted: 'Entradas EXTINF extraídas, antes do filtro por país.',
        streamsAfterCountryFilter: 'Streams sobreviventes ao filtro por país (candidatos a teste).',
        streamsRejectedByCountry: 'Streams removidos pelo filtro por país.',
        streamsTested: 'Streams testados via HTTP.',
        streamsWorking: 'Streams que responderam OK no teste.',
        streamsFailed: 'Streams cujo teste falhou.',
        successRatePercent: 'streamsWorking / (streamsWorking + streamsFailed) × 100.',
        testsBalanced: 'Verdadeiro se working + failed == tested; usado para detectar streams pulados.',
        coverage: 'recognizedChannelCount / totalChannels × 100 — só calculado se houver base comparável.',
      });
      document.getElementById('diagGlossary').innerHTML = `<table><thead><tr><th>Métrica</th><th>Definição</th></tr></thead><tbody>${gloss.map(([k,v]) => `<tr><td><code>${k}</code></td><td>${v}</td></tr>`).join('')}</tbody></table>`;
    }

    // ===== CATALOG =====
    let currentCatalogTab = 'overview';
    async function loadCatalog() {
      console.log('[DEBUG] loadCatalog called');
      const stats = await safeFetchJson('/api/catalog/stats', null);
      console.log('[DEBUG] stats:', stats);
      if (!stats || stats.error) {
        console.error('[DEBUG] stats error:', stats?.error);
        document.getElementById('catalogStats').innerHTML = `<div class='card'><p class='muted'>Catálogo não disponível: ${stats ? stats.error : 'erro de rede'}</p></div>`;
        return;
      }
      const dbg = stats.dbPath || '—';
      const cards = [];
      cards.push(metricCard('Canais canónicos', nfmt(stats.canonicalChannels || 0), '', 'Canais comidentificados no catálogo.'));
      cards.push(metricCard('Aliases', nfmt(stats.channelAliases || 0), '', 'Aliases normalizados activos.'));
      cards.push(metricCard('Regras', nfmt(stats.identityRules || 0), '', 'Regras de identidade explícitas.'));
      cards.push(metricCard('Reviews (Open)', nfmt(stats.reviewItemsOpen || 0), `<span class='badge warn'>${nfmt(stats.reviewItemsOpen || 0)}</span>`, 'Aguardam decisão humana.'));
      cards.push(metricCard('Reviews (Approved)', nfmt(stats.reviewItemsApproved || 0), '', ''));
      cards.push(metricCard('Reviews (Excluded)', nfmt(stats.reviewItemsExcluded || 0), '', ''));
      cards.push(metricCard('Ownership canais', nfmt(stats.dispatcharrChannelOwnerships || 0), '', 'Canais do Dispatcharr registados.'));
      cards.push(metricCard('Ownership streams', nfmt(stats.dispatcharrStreamOwnerships || 0), '', 'Streams do Dispatcharr registadas.'));
      cards.push(metricCard('Sync runs', nfmt(stats.syncRuns || 0), '', 'Execuções de sync gravadas.'));
      cards.push(metricCard('Sources', nfmt(stats.sources || 0), '', 'Origens externas activas (Telegram, M3U, Xtream, HTTP, File, Manual).'));
      cards.push(metricCard('Channel Sources', nfmt(stats.channelSources || 0), '', 'Associações canal-canónico ↔ stream ↔ source.'));
      cards.push(metricCard('Ordering Lists', nfmt(stats.orderingLists || 0) + ' · ' + nfmt(stats.orderingItems || 0) + ' items', '', 'Listas de ordenação e respectivos items.'));
      cards.push(metricCard('Priority Policies', nfmt(stats.sourcePriorityPolicies || 0), '', 'Políticas de Source Priority persistidas (global + overrides).'));
      cards.push(metricCard('Import Policies', nfmt(stats.importPolicies || 0), '', 'Live/Radio/VOD policies configuráveis.'));
      cards.push(metricCard('Canonical Groups', nfmt(stats.canonicalGroups || 0) + ' · ' + nfmt(stats.groupMappings || 0) + ' mappings', '', 'Grupos canónicos persistentes e mappings source→canónico.'));
      cards.push(metricCard('Matching Audits', nfmt(stats.matchingAudits || 0), '', 'Auditoria de cada resolução do CatalogResolver (PHASE 3).'));
      cards.push(metricCard('ChannelSource Observations', nfmt(stats.channelSourceObservations || 0), '', 'Histórico Quality/EPG/Availability por ChannelSource (PHASE 9 b).'));
      cards.push(metricCard('Sync Run Steps', nfmt(stats.syncRunSteps || 0), '', 'Passos detalhados por SyncRun (PHASE 11).'));
      cards.push(metricCard('Scheduled Jobs', nfmt(stats.scheduledJobs || 0), '', 'Jobs agendados persistentes (PHASE 12).'));
      cards.push(metricCard('Pending (País)', nfmt(stats.pendingCountryApprovalsOpen || 0), `<span class='badge warn'>${nfmt(stats.pendingCountryApprovalsOpen || 0)}</span>`, 'Canais pendentes de aprovação manual por país.'));
      document.getElementById('catalogStats').innerHTML = cards.join('');
      console.log('[DEBUG] catalogStats innerHTML set, cards:', cards.length);
      document.getElementById('catalogStatsDetail').innerHTML = `
        <p><strong>DB:</strong> <code>${dbg}</code></p>
        <p class='row-counts'>Actualizado: ${tsLocal(stats.generatedAtUtc)}</p>`;
      loadCatalogTab(currentCatalogTab);
      console.log('[DEBUG] loadCatalogTab called');
    }

    function loadCatalogTab(tab) {
      console.log('[DEBUG] loadCatalogTab called with tab:', tab);
      currentCatalogTab = tab;
      document.querySelectorAll('#catalogTabs button').forEach(b => b.classList.toggle('active', b.dataset.ctab === tab));
      document.querySelectorAll('[id^="ctab-"]').forEach(d => {
        const shouldHide = d.id !== 'ctab-' + tab;
        console.log('[DEBUG] ctab', d.id, 'hidden:', shouldHide);
        d.hidden = shouldHide;
      });
      if (tab === 'channels') loadCatalogChannels();
      else if (tab === 'rules') loadCatalogRules();
      else if (tab === 'affinity') loadAffinityGroups();
      else if (tab === 'sources') { loadSources(); loadChannelSources(); }
      else if (tab === 'ordering') loadOrderingLists();
      else if (tab === 'priority') loadGlobalPriority();
      else if (tab === 'sourceselection') { loadSourceSelectionPolicy(); loadChannelSourceSelectionKeys(); loadChannelSourceSelectionPolicies(); }
      else if (tab === 'matching') loadMatchingAudits();
      else if (tab === 'degradation') loadDegradation();
      else if (tab === 'scheduled') { loadScheduledActions(); loadScheduledJobs(); }
      else if (tab === 'policies') loadImportPolicies();
      else if (tab === 'groups') { loadCanonicalGroups(); loadGroupMappings(); }
      else if (tab === 'reviews') loadCatalogReviews();
      else if (tab === 'syncruns') loadCatalogSyncRuns();
      else if (tab === 'pending') loadPendingCountryApprovals();
    }

    let _channelsCache = [];
    let _selectedChannelId = null;

    async function loadCatalogChannels() {
      const channels = await safeFetchJson('/api/catalog/channels', []);
      if (!Array.isArray(channels)) { document.getElementById('catalogChannelsTable').innerHTML = '<p class="muted">Erro ao carregar canais.</p>'; return; }
      _channelsCache = channels;
      document.getElementById('channelsCount').textContent = `${channels.length} canal(is).`;
      renderChannelsTable();
      renderChannelDetail();
    }

    function renderChannelsTable() {
      const search = (document.getElementById('channelsSearch').value || '').toLowerCase().trim();
      const cat = document.getElementById('channelsCategoryFilter').value;
      const status = document.getElementById('channelsStatusFilter').value;
      const policy = document.getElementById('channelsPolicyFilter').value;
      const filtered = _channelsCache.filter(c => {
        if (cat && c.editorialCategory !== cat) return false;
        if (status === 'enabled' && !c.isEnabled) return false;
        if (status === 'disabled' && c.isEnabled) return false;
        if (policy && c.publicationPolicy !== policy) return false;
        if (search) {
          const hay = [c.displayName, c.key, c.country, ...(c.aliases || [])].filter(Boolean).join(' ').toLowerCase();
          if (!hay.includes(search)) return false;
        }
        return true;
      });
      const sorted = filtered.slice().sort((a, b) => (a.displayName || '').localeCompare(b.displayName || ''));
      if (!sorted.length) {
        document.getElementById('catalogChannelsTable').innerHTML = '<p class="muted">Nenhum canal corresponde aos filtros.</p>';
        return;
      }
      const rows = sorted.map(c => {
        const aliases = (c.aliases || []).join(', ') || '—';
        const policyBadge = `<span class='badge ${c.publicationPolicy === 'CreateEligible' ? 'ok' : (c.publicationPolicy === 'Excluded' ? 'err' : (c.publicationPolicy === 'ReviewOnly' ? 'warn' : 'muted'))}'>${c.publicationPolicy}</span>`;
        const selected = c.id === _selectedChannelId ? ' style="background:rgba(47,129,247,0.12);"' : '';
        return `<tr${selected}>
          <td><a href='#' onclick='event.preventDefault(); selectChannel(${c.id});'>${c.displayName || '—'}</a></td>
          <td><code>${c.key || '—'}</code></td>
          <td>${c.country ? `<span class='badge' style='background:var(--accent);color:#fff;'>${escapeHtml(c.country)}</span>` : '—'}</td>
          <td>${c.editorialCategory || '—'}</td>
          <td>${c.editorialGroup || '—'}</td>
          <td>${policyBadge}</td>
          <td>${c.isEnabled ? '<span class="badge ok">sim</span>' : '<span class="badge err">não</span>'}</td>
          <td><span class='muted'>${aliases}</span></td>
        </tr>`;
      }).join('');
      document.getElementById('catalogChannelsTable').innerHTML = `
        <table><thead><tr><th>Display Name</th><th>Key</th><th>País</th><th>Categoria</th><th>Grupo</th><th>Política</th><th>Activo</th><th>Aliases</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function selectChannel(id) {
      _selectedChannelId = id;
      renderChannelsTable();
      renderChannelDetail();
    }

    function renderChannelDetail() {
      const target = document.getElementById('channelDetailPanel');
      if (!target) return;
      if (!_selectedChannelId) {
        target.innerHTML = '<p class="muted">Seleccione um canal na tabela para ver o detalhe.</p>';
        return;
      }
      const c = _channelsCache.find(x => x.id === _selectedChannelId);
      if (!c) {
        target.innerHTML = '<p class="muted">Canal não encontrado (recargue a lista).</p>';
        return;
      }
      const policyBadge = `<span class='badge ${c.publicationPolicy === 'CreateEligible' ? 'ok' : (c.publicationPolicy === 'Excluded' ? 'err' : (c.publicationPolicy === 'ReviewOnly' ? 'warn' : 'muted'))}'>${c.publicationPolicy}</span>`;
      const aliases = c.aliases || [];
      target.innerHTML = `
        <div class='card'>
          <h3>Detalhe do canal</h3>
          <div style='display:grid;gap:10px;grid-template-columns:1fr 1fr;'>
            <div><strong>Canonical ID:</strong> <code>${c.key}</code></div>
            <div><strong>Estado:</strong> ${c.isEnabled ? '<span class="badge ok">activo</span>' : '<span class="badge err">inactivo</span>'}</div>
            <div><strong>Display Name:</strong> <span id='detailDisplayName'>${escapeHtml(c.displayName)}</span></div>
            <div><strong>País:</strong> ${c.country ? `<span class='badge' style='background:var(--accent);color:#fff;'>${escapeHtml(c.country)}</span>` : '<span class="muted">global</span>'}</div>
            <div><strong>Política:</strong> <span id='detailPolicyBadge'>${policyBadge}</span> <code>${c.publicationPolicy}</code></div>
            <div><strong>Categoria:</strong> ${c.editorialCategory}</div>
            <div><strong>Grupo Editorial:</strong> ${c.editorialGroup}</div>
            <div><strong>Criado:</strong> <span class="muted">${tsLocal(c.createdAtUtc)}</span></div>
            <div><strong>Actualizado:</strong> <span class="muted">${tsLocal(c.updatedAtUtc)}</span></div>
          </div>
          <div style='margin-top:14px;'>
            <h4 style='margin:0 0 6px 0;font-size:13px;color:var(--muted);text-transform:uppercase;letter-spacing:0.04em;'>Aliases (${aliases.length})</h4>
            <ul id='detailAliasesList' style='margin:0;padding-left:18px;'>${aliases.length ? aliases.map(a => `<li><code>${escapeHtml(a)}</code> <button class='secondary' style='padding:2px 6px;font-size:11px;' onclick="removeAliasFromDetail(${c.id}, '${escapeAttr(a)}')">remover</button></li>`).join('') : '<li class="muted">—</li>'}</ul>
            <div style='display:flex;gap:8px;margin-top:8px;'>
              <input id='detailNewAlias' type='text' placeholder='alias normalizado…' style='flex:1;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:4px 8px;border-radius:6px;font:inherit;'>
              <button onclick='addAliasFromDetail(${c.id})'>+ Alias</button>
            </div>
          </div>
          <div style='margin-top:14px;display:flex;gap:8px;flex-wrap:wrap;'>
            <button onclick='toggleChannelEnabled(${c.id}, ${!c.isEnabled})'>${c.isEnabled ? 'Desactivar' : 'Activar'}</button>
            <button class='secondary' onclick='toggleChannelPolicy(${c.id}, "${c.publicationPolicy}")'>Alterar Política…</button>
            <button class='secondary' onclick='editChannelInline(${c.id})'>Editar</button>
            <button class='secondary' onclick='deleteChannel(${c.id})' style='color:var(--err);'>Eliminar</button>
          </div>
        </div>`;
    }

    function escapeHtml(s) {
      if (s == null) return '';
      return String(s).replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
    }
    function escapeAttr(s) {
      return escapeHtml(s).replace(/`/g, '&#96;');
    }

    async function reloadChannelDetail(id) {
      const fresh = await safeFetchJson('/api/catalog/channels/' + id, null);
      if (!fresh || fresh.error) { alert('Erro ao recarregar canal: ' + (fresh && fresh.error || 'desconhecido')); return; }
      const idx = _channelsCache.findIndex(x => x.id === id);
      if (idx >= 0) _channelsCache[idx] = fresh;
      renderChannelsTable();
      renderChannelDetail();
    }

    async function addAliasFromDetail(channelId) {
      const input = document.getElementById('detailNewAlias');
      const alias = (input.value || '').trim();
      if (!alias) { alert('Alias é obrigatório.'); return; }
      const r = await fetch('/api/catalog/channels/' + channelId + '/aliases', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ normalizedAlias: alias })
      });
      if (r.ok) {
        input.value = '';
        await reloadChannelDetail(channelId);
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function removeAliasFromDetail(channelId, alias) {
      if (!confirm('Remover alias "' + alias + '"?')) return;
      const r = await fetch('/api/catalog/channels/' + channelId + '/aliases/' + encodeURIComponent(alias), { method: 'DELETE' });
      if (r.ok) await reloadChannelDetail(channelId);
      else alert('Erro: ' + r.status);
    }

    async function toggleChannelEnabled(channelId, nextValue) {
      const c = _channelsCache.find(x => x.id === channelId);
      if (!c) return;
      const payload = {
        displayName: c.displayName,
        country: c.country,
        editorialCategory: c.editorialCategory,
        editorialGroup: c.editorialGroup,
        publicationPolicy: c.publicationPolicy,
        isEnabled: nextValue
      };
      const r = await fetch('/api/catalog/channels/' + channelId, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (r.ok) {
        await reloadChannelDetail(channelId);
        if (typeof loadCatalog === 'function') loadCatalog();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function toggleChannelPolicy(channelId, currentPolicy) {
      const order = ['CreateEligible', 'MergeOnly', 'ReviewOnly', 'Excluded'];
      const next = { CreateEligible: 'MergeOnly', MergeOnly: 'ReviewOnly', ReviewOnly: 'Excluded', Excluded: 'CreateEligible' };
      const choice = prompt('Política actual: ' + currentPolicy + '\nNova política (' + order.join(' | ') + '):', next[currentPolicy] || 'CreateEligible');
      if (!choice) return;
      const c = _channelsCache.find(x => x.id === channelId);
      if (!c) return;
      const payload = {
        displayName: c.displayName,
        country: c.country,
        editorialCategory: c.editorialCategory,
        editorialGroup: c.editorialGroup,
        publicationPolicy: choice.trim(),
        isEnabled: c.isEnabled
      };
      const r = await fetch('/api/catalog/channels/' + channelId, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (r.ok) {
        await reloadChannelDetail(channelId);
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function editChannelInline(channelId) {
      const c = _channelsCache.find(x => x.id === channelId);
      if (!c) return;
      const newDisplay = prompt('Display Name:', c.displayName);
      if (newDisplay == null) return;
      const newCountry = prompt('País (vazio = global):', c.country || '');
      if (newCountry == null) return;
      const payload = {
        displayName: newDisplay,
        country: newCountry.trim() || null,
        editorialCategory: c.editorialCategory,
        editorialGroup: c.editorialGroup,
        publicationPolicy: c.publicationPolicy,
        isEnabled: c.isEnabled
      };
      const r = await fetch('/api/catalog/channels/' + channelId, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (r.ok) await reloadChannelDetail(channelId);
      else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function deleteChannel(channelId) {
      const c = _channelsCache.find(x => x.id === channelId);
      if (!c) return;
      if (!confirm('Eliminar o canal "' + (c.displayName || c.key) + '" (#' + channelId + ')? Esta operação remove também os aliases.')) return;
      const r = await fetch('/api/catalog/channels/' + channelId, { method: 'DELETE' });
      if (r.ok) {
        _selectedChannelId = null;
        await loadCatalogChannels();
        if (typeof loadCatalog === 'function') loadCatalog();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    function showCreateChannelForm() {
      document.getElementById('newChannelKey').value = '';
      document.getElementById('newChannelDisplayName').value = '';
      document.getElementById('newChannelCountry').value = '';
      document.getElementById('newChannelCategory').value = 'Live';
      document.getElementById('newChannelGroup').value = 'PortugalLive';
      document.getElementById('newChannelPolicy').value = 'CreateEligible';
      document.getElementById('newChannelEnabled').value = 'true';
      document.getElementById('newChannelAliases').value = '';
      document.getElementById('createChannelForm').hidden = false;
      document.getElementById('createChannelForm').scrollIntoView({ behavior: 'smooth' });
    }
    function hideCreateChannelForm() { document.getElementById('createChannelForm').hidden = true; }

    async function submitCreateChannel() {
      const payload = {
        key: document.getElementById('newChannelKey').value.trim(),
        displayName: document.getElementById('newChannelDisplayName').value.trim(),
        country: (document.getElementById('newChannelCountry').value || '').trim() || null,
        editorialCategory: document.getElementById('newChannelCategory').value,
        editorialGroup: document.getElementById('newChannelGroup').value,
        publicationPolicy: document.getElementById('newChannelPolicy').value,
        isEnabled: document.getElementById('newChannelEnabled').value === 'true',
        aliases: document.getElementById('newChannelAliases').value.split(/\r?\n/).map(a => a.trim()).filter(Boolean),
      };
      if (!payload.key) { alert('Key é obrigatória.'); return; }
      if (!payload.displayName) { alert('Display Name é obrigatório.'); return; }
      const r = await fetch('/api/catalog/channels', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (r.ok) {
        const created = await r.json();
        hideCreateChannelForm();
        await loadCatalogChannels();
        _selectedChannelId = created.id;
        renderChannelsTable();
        renderChannelDetail();
        if (typeof loadCatalog === 'function') loadCatalog();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    document.getElementById('channelsSearch').addEventListener('input', renderChannelsTable);
    document.getElementById('channelsCategoryFilter').addEventListener('change', renderChannelsTable);
    document.getElementById('channelsStatusFilter').addEventListener('change', renderChannelsTable);
    document.getElementById('channelsPolicyFilter').addEventListener('change', renderChannelsTable);

    // === PHASE 9A — Stream Validation ===
    const _validationFields = [
      ['maxConcurrency', 'Concorrência máxima', 'number'],
      ['connectionTimeoutSeconds', 'Connection Timeout (s)', 'number'],
      ['readTimeoutSeconds', 'Read Timeout (s)', 'number'],
      ['overallTimeoutSeconds', 'Overall Timeout (s)', 'number'],
      ['maxRetries', 'Max retries', 'number'],
      ['retryDelayMilliseconds', 'Retry delay (ms)', 'number'],
      ['successCacheTtlSeconds', 'Cache TTL sucesso (s)', 'number'],
      ['failureCacheTtlSeconds', 'Cache TTL falha (s)', 'number'],
      ['earlyExit', 'Early exit', 'select'],
      ['earlyExitThreshold', 'Early exit threshold', 'number'],
      ['hostFailure', 'Host failure policy', 'select'],
      ['hostFailureThreshold', 'Host failure threshold', 'number'],
    ];

    async function loadValidationPolicy() {
      const policy = await safeFetchJson('/api/validation/policy', null);
      const status = document.getElementById('validationPolicyStatus');
      if (!policy) { status.textContent = 'Erro ao carregar política.'; return; }
      const form = document.getElementById('validationPolicyForm');
      form.innerHTML = _validationFields.map(([k, label, kind]) => {
        const v = policy[k];
        if (kind === 'select') {
          const opts = (k === 'earlyExit'
            ? [['TestAll','TestAll'],['StopAfterFirstValid','StopAfterFirstValid'],['StopAfterNValid','StopAfterNValid']]
            : [['Off','Off'],['ShortCircuitOnHostFailure','ShortCircuitOnHostFailure']]
          ).map(([val, lbl]) => `<option value="${val}" ${v===val?'selected':''}>${lbl}</option>`).join('');
          return `<div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>${label}</label><select data-vp-key="${k}">${opts}</select></div>`;
        }
        return `<div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>${label}</label><input data-vp-key="${k}" type='${kind}' value='${v ?? ''}'></div>`;
      }).join('');
      status.textContent = 'Política carregada.';
    }

    function readValidationPolicyForm() {
      const p = {};
      const inputs = document.querySelectorAll('#validationPolicyForm [data-vp-key]');
      inputs.forEach(el => {
        const k = el.getAttribute('data-vp-key');
        const raw = el.value;
        if (el.tagName === 'INPUT' && el.type === 'number') {
          const n = parseInt(raw, 10);
          p[k] = Number.isFinite(n) ? n : 0;
        } else {
          p[k] = raw;
        }
      });
      return p;
    }

    async function saveValidationPolicy() {
      const payload = readValidationPolicyForm();
      const r = await fetch('/api/validation/policy', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      const status = document.getElementById('validationPolicyStatus');
      if (r.ok) {
        status.textContent = 'Política guardada.';
      } else {
        const err = await r.json();
        status.textContent = 'Erro: ' + (err.error || r.status);
      }
    }

    async function runValidationTest() {
      const textarea = document.getElementById('validationTestUrls');
      const urls = (textarea.value || '').split(/\r?\n/).map(s => s.trim()).filter(Boolean);
      const out = document.getElementById('validationTestResult');
      if (!urls.length) { out.textContent = 'Fornece URLs.'; return; }
      out.textContent = 'A testar ' + urls.length + ' URL(s)…';
      const r = await fetch('/api/validation/test', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ urls }),
      });
      if (!r.ok) {
        const err = await r.json();
        out.textContent = 'Erro: ' + (err.error || r.status);
        return;
      }
      const body = await r.json();
      const m = body.metrics || {};
      const ok = m.succeeded || 0, total = m.tested || 0, cached = m.cached || 0, retries = m.retries || 0, peak = m.actualPeakConcurrency || 0;
      out.innerHTML = `<pre>tested=${total} succeeded=${ok} cached=${cached} retries=${retries} peak=${peak}\n${JSON.stringify(body.outcomes, null, 2).slice(0, 4000)}</pre>`;
    }

    async function loadCatalogRules() {
      const rules = await safeFetchJson('/api/catalog/identity-rules', []);
      if (!Array.isArray(rules)) { document.getElementById('catalogRulesTable').innerHTML = '<p class="muted">Erro ao carregar regras.</p>'; return; }
      document.getElementById('rulesCount').textContent = `${rules.length} regra(s).`;
      if (!rules.length) { document.getElementById('catalogRulesTable').innerHTML = '<p class="muted">Nenhuma regra de identidade.</p>'; return; }
      const rows = rules.map(r => {
        const dispBadge = `<span class='badge ${r.disposition === 'Excluded' ? 'err' : 'warn'}'>${r.disposition}</span>`;
        return `<tr>
          <td><code>${r.normalizedIdentity || '—'}</code></td>
          <td>${dispBadge}</td>
          <td>${r.reason || '—'}</td>
          <td>${tsLocal(r.createdAtUtc)}</td>
          <td><button class='secondary' style='padding:4px 8px;' onclick='deleteRule("${r.normalizedIdentity.replace(/"/g, '\\"')}")'>Eliminar</button></td>
        </tr>`;
      }).join('');
      document.getElementById('catalogRulesTable').innerHTML = `
        <table><thead><tr><th>Identidade</th><th>Disposição</th><th>Razão</th><th>Criado</th><th>Ações</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function loadCatalogReviews() {
      const reviews = await safeFetchJson('/api/catalog/reviews', []);
      if (!Array.isArray(reviews)) { document.getElementById('catalogReviewsTable').innerHTML = '<p class="muted">Erro ao carregar reviews.</p>'; return; }
      const open = reviews.filter(r => r.state === 'Open');
      document.getElementById('reviewsCount').textContent = `${open.length} em open · ${reviews.length} total.`;
      if (!reviews.length) { document.getElementById('catalogReviewsTable').innerHTML = '<p class="muted">Nenhum item de revisão.</p>'; return; }
      const rows = reviews.map(r => {
        const stateBadge = r.state === 'Open' ? '<span class="badge warn">Open</span>'
          : r.state === 'Approved' ? '<span class="badge ok">Approved</span>'
          : '<span class="badge err">Excluded</span>';
        const actions = r.state === 'Open'
          ? `<button style='padding:4px 8px;' onclick='approveReview("${r.fingerprint.replace(/"/g, '\\"')}")'>Approve</button>
             <button class='secondary' style='padding:4px 8px;' onclick='excludeReview("${r.fingerprint.replace(/"/g, '\\"')}")'>Exclude</button>`
          : '—';
        return `<tr>
          <td><code title='${r.fingerprint}'>${(r.fingerprint || '').slice(0, 12)}…</code></td>
          <td><code>${r.normalizedIdentity || '—'}</code></td>
          <td>${r.sourceGroup || '—'}</td>
          <td>${r.reasonSignature || '—'}</td>
          <td>${stateBadge}</td>
          <td>${r.note || '—'}</td>
          <td>${tsLocal(r.createdAtUtc)}</td>
          <td>${actions}</td>
        </tr>`;
      }).join('');
      document.getElementById('catalogReviewsTable').innerHTML = `
        <table><thead><tr><th>Fingerprint</th><th>Identidade</th><th>Source Group</th><th>Razão</th><th>Estado</th><th>Nota</th><th>Criado</th><th>Ações</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function loadSources() {
      const sources = await safeFetchJson('/api/catalog/sources', []);
      if (!Array.isArray(sources)) { document.getElementById('sourcesTable').innerHTML = '<p class="muted">Erro ao carregar sources.</p>'; return; }
      const filter = document.getElementById('channelSourceSourceFilter');
      const selected = filter.value;
      filter.innerHTML = '<option value="">todas</option>' + sources.map(s => `<option value="${s.id}" ${String(s.id)===selected?'selected':''}>${escapeHtml(s.name)} (${s.kind})</option>`).join('');
      if (!sources.length) { document.getElementById('sourcesTable').innerHTML = '<p class="muted">Nenhuma source registada.</p>'; return; }
      const rows = sources.map(s => {
        const badge = s.isEnabled ? '<span class="badge ok">activo</span>' : '<span class="badge err">inactivo</span>';
        const lastD = s.lastDiscoveryAtUtc ? tsLocal(s.lastDiscoveryAtUtc) : '—';
        const lastV = s.lastValidationAtUtc ? tsLocal(s.lastValidationAtUtc) : '—';
        return `<tr>
          <td><code>${s.id}</code></td>
          <td><code>${escapeHtml(s.key)}</code></td>
          <td>${escapeHtml(s.name)}</td>
          <td>${s.kind}</td>
          <td>${s.priority}</td>
          <td>${badge}</td>
          <td>${escapeHtml(s.origin || '—')}</td>
          <td>${lastD}</td>
          <td>${lastV}</td>
          <td><button class='secondary' onclick='deleteSource(${s.id})'>Eliminar</button></td>
        </tr>`;
      }).join('');
      document.getElementById('sourcesTable').innerHTML = `<table><thead><tr><th>#</th><th>Key</th><th>Nome</th><th>Tipo</th><th>Priority</th><th>Estado</th><th>Origem</th><th>Última descoberta</th><th>Última validação</th><th>Acções</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function submitCreateSource() {
      const payload = {
        key: document.getElementById('sourceKey').value.trim(),
        name: document.getElementById('sourceName').value.trim(),
        kind: document.getElementById('sourceKind').value,
        origin: document.getElementById('sourceOrigin').value.trim(),
        priority: parseInt(document.getElementById('sourcePriority').value, 10) || 0,
        isEnabled: document.getElementById('sourceEnabled').value === 'true',
      };
      if (!payload.key) { alert('Key é obrigatória.'); return; }
      if (!payload.name) { alert('Name é obrigatório.'); return; }
      const r = await fetch('/api/catalog/sources', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (r.ok) {
        document.getElementById('sourceKey').value = '';
        document.getElementById('sourceName').value = '';
        document.getElementById('sourceOrigin').value = '';
        await loadSources();
        await loadChannelSources();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function deleteSource(id) {
      if (!confirm('Eliminar a source #' + id + '? Os ChannelSources associados serão removidos em cascata.')) return;
      const r = await fetch('/api/catalog/sources/' + id, { method: 'DELETE' });
      if (r.ok) { await loadSources(); await loadChannelSources(); }
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function loadChannelSources() {
      const filter = document.getElementById('channelSourceSourceFilter').value;
      const url = filter ? '/api/catalog/sources/' + filter + '/streams' : '/api/catalog/sources/0/streams';
      // Quando filter vazio, listamos via primeira source ou via 0 (devolve lista vazia). Para "todas" usamos múltiplas chamadas.
      let allStreams = [];
      if (filter) {
        const r = await safeFetchJson(url, []);
        if (Array.isArray(r)) allStreams = r;
      } else {
        const sources = await safeFetchJson('/api/catalog/sources', []);
        if (Array.isArray(sources)) {
          for (const s of sources) {
            const r = await safeFetchJson('/api/catalog/sources/' + s.id + '/streams', []);
            if (Array.isArray(r)) allStreams = allStreams.concat(r);
          }
        }
      }
      if (!allStreams.length) {
        document.getElementById('channelSourcesTable').innerHTML = '<p class="muted">Nenhum channel source registado.</p>';
        return;
      }
      const rows = allStreams.map(cs => {
        const url = escapeHtml(cs.streamUrl || '');
        return `<tr>
          <td><code>${cs.id}</code></td>
          <td>${cs.canonicalChannelId}</td>
          <td>${cs.sourceId}</td>
          <td><code>${url}</code></td>
          <td>${cs.quality}</td>
          <td>${cs.epg}</td>
          <td>${cs.availability}</td>
          <td>${(cs.matchConfidence || 0).toFixed(2)}</td>
          <td>${escapeHtml(cs.matchMethod || '—')}</td>
          <td>${cs.isEnabled ? '<span class="badge ok">sim</span>' : '<span class="badge err">não</span>'}</td>
          <td><button class='secondary' onclick='toggleChannelSource(${cs.id}, ${!cs.isEnabled})'>${cs.isEnabled ? 'Desactivar' : 'Activar'}</button>
              <button class='secondary' onclick='deleteChannelSource(${cs.id})' style='color:var(--err);'>Eliminar</button></td>
        </tr>`;
      }).join('');
      document.getElementById('channelSourcesTable').innerHTML = `<table><thead><tr><th>#</th><th>Canal</th><th>Source</th><th>URL</th><th>Quality</th><th>EPG</th><th>Availability</th><th>Conf.</th><th>Método</th><th>Activo</th><th>Acções</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function toggleChannelSource(id, next) {
      const r = await fetch('/api/catalog/channel-sources/' + id, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ isEnabled: next }),
      });
      if (r.ok) { await loadChannelSources(); }
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function deleteChannelSource(id) {
      if (!confirm('Eliminar o channel source #' + id + '?')) return;
      const r = await fetch('/api/catalog/channel-sources/' + id, { method: 'DELETE' });
      if (r.ok) { await loadChannelSources(); }
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    document.getElementById('channelSourceSourceFilter').addEventListener('change', loadChannelSources);

    let _orderingListsCache = [];
    let _orderingListDetailCache = null;
    let _orderingChannelsCache = [];

    async function loadOrderingLists() {
      const lists = await safeFetchJson('/api/catalog/ordering-lists', []);
      if (!Array.isArray(lists)) { document.getElementById('orderingListsTable').innerHTML = '<p class="muted">Erro.</p>'; return; }
      _orderingListsCache = lists;
      if (!lists.length) { document.getElementById('orderingListsTable').innerHTML = '<p class="muted">Nenhuma ordering list.</p>'; return; }
      const rows = lists.map(l => `<tr${_orderingListDetailCache && _orderingListDetailCache.id === l.id ? ' style="background:rgba(47,129,247,0.12);"' : ''}>
        <td><code>${l.id}</code></td>
        <td><code>${escapeHtml(l.key)}</code></td>
        <td><a href='#' onclick='event.preventDefault(); openOrderingList(${l.id});'>${escapeHtml(l.name)}</a></td>
        <td>${escapeHtml(l.country || '—')}</td>
        <td>${l.itemCount || 0}</td>
        <td>${l.isEnabled ? '<span class="badge ok">sim</span>' : '<span class="badge err">não</span>'}</td>
        <td>
          <button class='secondary' onclick='previewOrderingList(${l.id})'>Preview</button>
          <button class='secondary' onclick='duplicateOrderingList(${l.id}, "${escapeHtml(l.key)}")'>Duplicar</button>
          <button class='secondary' style='color:var(--err);' onclick='deleteOrderingList(${l.id})'>Eliminar</button>
        </td>
      </tr>`).join('');
      document.getElementById('orderingListsTable').innerHTML = `<table><thead><tr><th>#</th><th>Key</th><th>Nome</th><th>País</th><th>Items</th><th>Activo</th><th>Acções</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function submitCreateOrderingList() {
      const payload = {
        key: document.querySelector("[data-ordering-create='key']").value.trim(),
        name: document.querySelector("[data-ordering-create='name']").value.trim(),
        country: document.querySelector("[data-ordering-create='country']").value.trim() || null,
        description: null,
        isEnabled: true,
      };
      if (!payload.key || !payload.name) { alert('Key e Name são obrigatórios.'); return; }
      const r = await fetch('/api/catalog/ordering-lists', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (r.ok) {
        document.querySelector("[data-ordering-create='key']").value = '';
        document.querySelector("[data-ordering-create='name']").value = '';
        document.querySelector("[data-ordering-create='country']").value = '';
        await loadOrderingLists();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function openOrderingList(id) {
      const detail = await safeFetchJson('/api/catalog/ordering-lists/' + id, null);
      if (!detail || detail.error) { alert('Erro: ' + (detail && detail.error || '?')); return; }
      _orderingListDetailCache = detail;

      // Carrega também todos os canais canónicos para o select de adicionar.
      const channels = await safeFetchJson('/api/catalog/channels', []);
      _orderingChannelsCache = Array.isArray(channels) ? channels : [];

      const detailDiv = document.getElementById('orderingDetail');
      const items = (detail.items || []).slice().sort((a, b) => a.position - b.position);
      const itemRows = items.map(i => `<tr>
        <td>${i.position}</td>
        <td><code>${i.canonicalChannelId}</code></td>
        <td>${escapeHtml((i.canonicalChannelDisplayName || '') + (i.canonicalChannelKey ? ' (' + i.canonicalChannelKey + ')' : ''))}</td>
        <td>${i.isEnabled ? '<span class="badge ok">sim</span>' : '<span class="badge err">não</span>'}</td>
        <td>
          <button class='secondary' onclick='moveOrderingItem(${i.id}, ${i.position - 1})' ${i.position === 0 ? 'disabled' : ''}>↑</button>
          <button class='secondary' onclick='moveOrderingItem(${i.id}, ${i.position + 1})' ${i.position === items.length - 1 ? 'disabled' : ''}>↓</button>
          <button class='secondary' onclick='toggleOrderingItem(${i.id}, ${!i.isEnabled})'>${i.isEnabled ? 'Desactivar' : 'Activar'}</button>
          <button class='secondary' style='color:var(--err);' onclick='removeOrderingItem(${i.id})'>Remover</button>
        </td>
      </tr>`).join('');
      const channelOptions = _orderingChannelsCache
        .filter(c => !items.some(i => i.canonicalChannelId === c.id))
        .map(c => `<option value="${c.id}">${escapeHtml(c.displayName || c.key)} (${escapeHtml(c.key)})</option>`).join('');
      detailDiv.innerHTML = `
        <div style='margin-bottom:8px;'><strong>${escapeHtml(detail.name)}</strong> · ${detail.items ? detail.items.length : 0} itens · <span class='muted'>${detail.isEnabled ? 'activo' : 'inactivo'}</span></div>
        <table><thead><tr><th>#</th><th>Canal</th><th>Display</th><th>Activo</th><th>Acções</th></tr></thead><tbody>${itemRows}</tbody></table>
        <div style='margin-top:10px;display:flex;gap:8px;align-items:center;'>
          <select id='addItemChannel'>${channelOptions}</select>
          <button onclick='addOrderingItem(${detail.id})'>+ Adicionar</button>
        </div>
      `;
      await loadOrderingLists();
    }

    async function previewOrderingList(id) {
      const preview = await safeFetchJson('/api/catalog/ordering-lists/' + id + '/preview', null);
      const out = document.getElementById('orderingPreview');
      if (!preview) { out.innerHTML = '<p class="muted">Erro.</p>'; return; }
      const entries = preview.entries || [];
      const missing = preview.missingChannels || [];
      out.innerHTML = `<p class='muted'>${preview.totalEntries || 0} entradas geradas · ${missing.length} canais sem stream.</p>
        ${entries.length ? `<table><thead><tr><th>#</th><th>Key</th><th>Display</th><th>Group</th><th>Source</th><th>Quality</th><th>URL</th></tr></thead><tbody>${entries.map((e, i) => `<tr>
          <td>${i + 1}</td>
          <td><code>${escapeHtml(e.canonicalKey)}</code></td>
          <td>${escapeHtml(e.displayName)}</td>
          <td>${escapeHtml(e.group)}</td>
          <td>${escapeHtml(e.sourceName)}</td>
          <td>${escapeHtml(e.quality)}</td>
          <td><code>${escapeHtml(e.streamUrl)}</code></td>
        </tr>`).join('')}</tbody></table>` : '<p class="muted">Lista vazia.</p>'}
        ${missing.length ? `<p class='muted' style='margin-top:8px;'>Canais sem stream: ${missing.map(m => '#' + m.canonicalChannelId + ' (' + m.reason + ')').join(', ')}</p>` : ''}
      `;
    }

    async function duplicateOrderingList(id, originalKey) {
      const newKey = prompt('Key da nova lista (slug único):', originalKey + '-copy');
      if (!newKey) return;
      const r = await fetch('/api/catalog/ordering-lists/' + id + '/duplicate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ newKey }),
      });
      if (r.ok) { await loadOrderingLists(); }
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function deleteOrderingList(id) {
      if (!confirm('Eliminar a ordering list #' + id + '?')) return;
      const r = await fetch('/api/catalog/ordering-lists/' + id, { method: 'DELETE' });
      if (r.ok) { _orderingListDetailCache = null; document.getElementById('orderingDetail').innerHTML = ''; await loadOrderingLists(); }
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function addOrderingItem(listId) {
      const sel = document.getElementById('addItemChannel');
      if (!sel || !sel.value) { alert('Escolhe um canal.'); return; }
      const r = await fetch('/api/catalog/ordering-lists/' + listId + '/items', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ canonicalChannelId: parseInt(sel.value, 10), isEnabled: true }),
      });
      if (r.ok) { await openOrderingList(listId); }
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function moveOrderingItem(itemId, newPos) {
      const r = await fetch('/api/catalog/ordering-items/' + itemId, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ position: newPos }),
      });
      if (r.ok && _orderingListDetailCache) await openOrderingList(_orderingListDetailCache.id);
      else if (!r.ok) { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function toggleOrderingItem(itemId, next) {
      const r = await fetch('/api/catalog/ordering-items/' + itemId, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ isEnabled: next }),
      });
      if (r.ok && _orderingListDetailCache) await openOrderingList(_orderingListDetailCache.id);
    }

    async function removeOrderingItem(itemId) {
      if (!confirm('Remover o item?')) return;
      const r = await fetch('/api/catalog/ordering-items/' + itemId, { method: 'DELETE' });
      if (r.ok && _orderingListDetailCache) await openOrderingList(_orderingListDetailCache.id);
    }

    async function loadGlobalPriority() {
      const p = await safeFetchJson('/api/catalog/priority-policies', null);
      const form = document.getElementById('globalPriorityForm');
      if (!p) { form.innerHTML = '<p class="muted">Erro.</p>'; return; }
      form.innerHTML = `
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Criteria JSON (ordem)</label><input id='gp_criteria' value='${escapeHtml(p.criteriaJson)}'></div>
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Preferred Quality (CSV)</label><input id='gp_quality' value='${escapeHtml(p.preferredQuality || "")}'></div>
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Allow fallback</label>
          <select id='gp_fallback'><option value='true' ${p.allowFallback?'selected':''}>sim</option><option value='false' ${!p.allowFallback?'selected':''}>não</option></select>
        </div>
      `;
    }

    async function saveGlobalPriority() {
      const payload = {
        scope: 'global',
        canonicalChannelId: null,
        criteriaJson: document.getElementById('gp_criteria').value,
        preferredQuality: document.getElementById('gp_quality').value,
        allowFallback: document.getElementById('gp_fallback').value === 'true',
      };
      const r = await fetch('/api/catalog/priority-policies', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (r.ok) alert('Política global guardada.');
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function loadChannelPriority() {
      const id = parseInt(document.getElementById('priorityChannelId').value, 10);
      if (!id) { alert('Indica um channel ID.'); return; }
      const p = await safeFetchJson('/api/catalog/priority-policies?channelId=' + id, null);
      const form = document.getElementById('channelPriorityForm');
      if (!p) { form.innerHTML = '<p class="muted">Sem override; aplicar-se-á a política global.</p>'; return; }
      form.innerHTML = `
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Scope</label><input value='channel' disabled></div>
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Channel ID</label><input value='${p.canonicalChannelId || id}' disabled></div>
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Criteria JSON</label><input id='cp_criteria' value='${escapeHtml(p.criteriaJson)}'></div>
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Preferred Quality (CSV)</label><input id='cp_quality' value='${escapeHtml(p.preferredQuality || "")}'></div>
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Allow fallback</label>
          <select id='cp_fallback'><option value='true' ${p.allowFallback?'selected':''}>sim</option><option value='false' ${!p.allowFallback?'selected':''}>não</option></select>
        </div>
      `;
    }

    async function saveChannelPriority() {
      const id = parseInt(document.getElementById('priorityChannelId').value, 10);
      if (!id) { alert('Indica um channel ID.'); return; }
      const payload = {
        scope: 'channel',
        canonicalChannelId: id,
        criteriaJson: document.getElementById('cp_criteria').value,
        preferredQuality: document.getElementById('cp_quality').value,
        allowFallback: document.getElementById('cp_fallback').value === 'true',
      };
      const r = await fetch('/api/catalog/priority-policies', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (r.ok) alert('Override guardado.');
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    // === PHASE 13 (Wave 13-4) — Global Source Selection Policy ===
    async function loadSourceSelectionPolicy() {
      const p = await safeFetchJson('/api/catalog/source-selection-policies', null);
      const form = document.getElementById('sourceSelectionPolicyForm');
      const status = document.getElementById('sourceSelectionPolicyStatus');
      if (!p) { form.innerHTML = '<p class="muted">Erro ao carregar política.</p>'; return; }
      form.innerHTML = `
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Max sources per channel (0 = não publicar nenhuma)</label>
          <input id='ssp_maxSourcesPerChannel' type='number' min='0' value='${p.maxSourcesPerChannel}' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
        </div>
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Max sources per provider (vazio = sem limite)</label>
          <input id='ssp_maxSourcesPerProvider' type='number' min='1' value='${p.maxSourcesPerProvider ?? ''}' style='width:100%;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:6px 10px;border-radius:6px;font:inherit;'>
        </div>
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Prefer distinct providers</label>
          <select id='ssp_preferDistinctProviders'><option value='true' ${p.preferDistinctProviders?'selected':''}>sim</option><option value='false' ${!p.preferDistinctProviders?'selected':''}>não</option></select>
        </div>
        <div><label class='muted' style='display:block;font-size:12px;margin-bottom:4px;'>Allow fallback to same provider</label>
          <select id='ssp_allowFallbackToSameProvider'><option value='true' ${p.allowFallbackToSameProvider?'selected':''}>sim</option><option value='false' ${!p.allowFallbackToSameProvider?'selected':''}>não</option></select>
        </div>
      `;
      status.textContent = 'Política carregada.';
    }

    async function saveSourceSelectionPolicy() {
      const status = document.getElementById('sourceSelectionPolicyStatus');
      const maxChannelRaw = document.getElementById('ssp_maxSourcesPerChannel').value.trim();
      const maxProviderRaw = document.getElementById('ssp_maxSourcesPerProvider').value.trim();
      const payload = {
        maxSourcesPerChannel: maxChannelRaw === '' ? null : parseInt(maxChannelRaw, 10),
        maxSourcesPerProvider: maxProviderRaw === '' ? null : parseInt(maxProviderRaw, 10),
        preferDistinctProviders: document.getElementById('ssp_preferDistinctProviders').value === 'true',
        allowFallbackToSameProvider: document.getElementById('ssp_allowFallbackToSameProvider').value === 'true',
      };
      const r = await fetch('/api/catalog/source-selection-policies', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (r.ok) {
        status.textContent = 'Política guardada.';
      } else {
        const err = await r.json();
        status.textContent = 'Erro: ' + (err.error || r.status);
      }
    }

    // === PHASE 13 (Wave 13-4b) — Per-channel Source Selection Policy ===
    let channelSourceSelectionPolicies = [];

    async function loadChannelSourceSelectionKeys() {
      const channels = await safeFetchJson('/api/catalog/channels', []);
      const datalist = document.getElementById('cssp_channelKeyList');
      if (!datalist || !Array.isArray(channels)) return;
      datalist.innerHTML = channels
        .filter(c => c && c.key)
        .map(c => `<option value='${escapeHtml(c.key)}'>${escapeHtml(c.displayName || '')}</option>`)
        .join('');
    }

    async function loadChannelSourceSelectionPolicies() {
      const table = document.getElementById('channelSourceSelectionPoliciesTable');
      const status = document.getElementById('channelSourceSelectionPolicyStatus');
      const data = await safeFetchJson('/api/catalog/source-selection-policies/channels', null);
      const list = data && Array.isArray(data.overrides) ? data.overrides : null;
      channelSourceSelectionPolicies = list || [];
      if (!list) { table.innerHTML = '<p class="muted">Erro ao carregar overrides.</p>'; return; }
      if (!list.length) { table.innerHTML = '<p class="muted">Nenhum override por canal.</p>'; return; }
      const rows = list.map((p, i) => `<tr>
        <td><code>${escapeHtml(p.canonicalChannelKey || '')}</code></td>
        <td>${p.maxSourcesPerChannel}</td>
        <td>${p.preferDistinctProviders ? 'sim' : 'não'}</td>
        <td>${p.maxSourcesPerProvider ?? '—'}</td>
        <td>${p.allowFallbackToSameProvider ? 'sim' : 'não'}</td>
        <td>
          <button class='secondary' onclick='editChannelSourceSelectionPolicy(${i})'>Editar</button>
          <button class='secondary' style='color:var(--err);' onclick='deleteChannelSourceSelectionPolicy(${i})'>Eliminar</button>
        </td>
      </tr>`).join('');
      table.innerHTML = `<table><thead><tr><th>Chave canónica</th><th>Max/canal</th><th>Distintos</th><th>Max/provedor</th><th>Fallback</th><th>Acções</th></tr></thead><tbody>${rows}</tbody></table>`;
      if (status) status.textContent = '';
    }

    function editChannelSourceSelectionPolicy(index) {
      const p = channelSourceSelectionPolicies[index];
      if (!p) return;
      document.getElementById('cssp_channelKey').value = p.canonicalChannelKey || '';
      document.getElementById('cssp_maxSourcesPerChannel').value = p.maxSourcesPerChannel;
      document.getElementById('cssp_maxSourcesPerProvider').value = p.maxSourcesPerProvider ?? '';
      document.getElementById('cssp_preferDistinctProviders').value = p.preferDistinctProviders ? 'true' : 'false';
      document.getElementById('cssp_allowFallbackToSameProvider').value = p.allowFallbackToSameProvider ? 'true' : 'false';
      document.getElementById('channelSourceSelectionPolicyStatus').textContent = 'Override carregado para edição.';
    }

    async function saveChannelSourceSelectionPolicy() {
      const status = document.getElementById('channelSourceSelectionPolicyStatus');
      const key = document.getElementById('cssp_channelKey').value.trim();
      if (!key) { status.textContent = 'Indica a chave canónica do canal.'; return; }
      const maxChannelRaw = document.getElementById('cssp_maxSourcesPerChannel').value.trim();
      const maxProviderRaw = document.getElementById('cssp_maxSourcesPerProvider').value.trim();
      const payload = {
        canonicalChannelKey: key,
        maxSourcesPerChannel: maxChannelRaw === '' ? null : parseInt(maxChannelRaw, 10),
        maxSourcesPerProvider: maxProviderRaw === '' ? null : parseInt(maxProviderRaw, 10),
        preferDistinctProviders: document.getElementById('cssp_preferDistinctProviders').value === 'true',
        allowFallbackToSameProvider: document.getElementById('cssp_allowFallbackToSameProvider').value === 'true',
      };
      const r = await fetch('/api/catalog/source-selection-policies/channels', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (r.ok) {
        status.textContent = 'Override guardado.';
        await loadChannelSourceSelectionPolicies();
      } else {
        const err = await r.json();
        status.textContent = 'Erro: ' + (err.error || r.status);
      }
    }

    async function deleteChannelSourceSelectionPolicy(index) {
      const p = channelSourceSelectionPolicies[index];
      if (!p || !p.canonicalChannelKey) return;
      if (!confirm('Eliminar o override do canal "' + p.canonicalChannelKey + '"?')) return;
      const status = document.getElementById('channelSourceSelectionPolicyStatus');
      const r = await fetch('/api/catalog/source-selection-policies/channels/' + encodeURIComponent(p.canonicalChannelKey), { method: 'DELETE' });
      if (r.ok) {
        status.textContent = 'Override eliminado.';
        await loadChannelSourceSelectionPolicies();
      } else {
        const err = await r.json();
        status.textContent = 'Erro: ' + (err.error || r.status);
      }
    }

    async function loadImportPolicies() {
      const list = await safeFetchJson('/api/catalog/import-policies', []);
      if (!Array.isArray(list)) { document.getElementById('importPoliciesTable').innerHTML = '<p class="muted">Erro.</p>'; return; }
      if (!list.length) { document.getElementById('importPoliciesTable').innerHTML = '<p class="muted">Nenhuma política. Cria abaixo (Live, Radio, VOD).</p>'; return; }
      const rows = list.map(p => `<tr>
        <td><code>${p.id}</code></td>
        <td>${p.mediaKind}</td>
        <td>${p.vodPolicy}</td>
        <td><code>${escapeHtml(p.targetGroupsCsv || '')}</code></td>
        <td><code>${escapeHtml(p.excludedGroupsCsv || '')}</code></td>
        <td>${p.isEnabled ? '<span class="badge ok">sim</span>' : '<span class="badge err">não</span>'}</td>
        <td>
          <select data-import-edit-vod data-row-key='${p.id}'>
            <option value='ImportVod' ${p.vodPolicy==='ImportVod'?'selected':''}>ImportVod</option>
            <option value='KeepVod' ${p.vodPolicy==='KeepVod'?'selected':''}>KeepVod</option>
            <option value='ExcludeVod' ${p.vodPolicy==='ExcludeVod'?'selected':''}>ExcludeVod</option>
          </select>
          <input data-import-edit-target data-row-key='${p.id}' value='${escapeHtml(p.targetGroupsCsv||"")}' placeholder='targets' style='margin-left:4px;width:140px;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:4px 6px;border-radius:6px;font:inherit;'>
          <input data-import-edit-excluded data-row-key='${p.id}' value='${escapeHtml(p.excludedGroupsCsv||"")}' placeholder='excluded' style='margin-left:4px;width:140px;background:var(--panel-2);color:var(--text);border:1px solid var(--border);padding:4px 6px;border-radius:6px;font:inherit;'>
          <button class='secondary' onclick='saveImportPolicy(` + p.id + `, "` + p.mediaKind + `")'>Guardar</button>
        </td>
      </tr>`).join('');
      document.getElementById('importPoliciesTable').innerHTML = `<table><thead><tr><th>#</th><th>MediaKind</th><th>VodPolicy</th><th>Targets (CSV)</th><th>Excluded (CSV)</th><th>Activo</th><th>Editar</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function saveImportPolicy(policyId, mediaKind) {
      const sel = "[data-import-edit-vod][data-row-key='" + policyId + "']";
      const vod = document.querySelector(sel).value;
      const target = document.querySelector("[data-import-edit-target][data-row-key='" + policyId + "']").value;
      const excluded = document.querySelector("[data-import-edit-excluded][data-row-key='" + policyId + "']").value;
      const r = await fetch('/api/catalog/import-policies', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mediaKind, vodPolicy: vod, targetGroupsCsv: target, excludedGroupsCsv: excluded, isEnabled: true }),
      });
      if (r.ok) { alert('Política guardada.'); await loadImportPolicies(); }
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function loadCanonicalGroups() {
      const groups = await safeFetchJson('/api/catalog/canonical-groups', []);
      if (!Array.isArray(groups)) { document.getElementById('canonicalGroupsTable').innerHTML = '<p class="muted">Erro.</p>'; return; }
      if (!groups.length) { document.getElementById('canonicalGroupsTable').innerHTML = '<p class="muted">Nenhum grupo canónico. Cria abaixo.</p>'; return; }
      const rows = groups.map(g => `<tr>
        <td><code>${g.id}</code></td>
        <td><code>${escapeHtml(g.key)}</code></td>
        <td>${escapeHtml(g.displayName)}</td>
        <td>${escapeHtml(g.country || '—')}</td>
        <td>${g.order}</td>
        <td>${g.isDefault ? '<span class="badge ok">default</span>' : '—'}</td>
        <td>${g.isEnabled ? '<span class="badge ok">sim</span>' : '<span class="badge err">não</span>'}</td>
        <td><button class='secondary' style='color:var(--err);' onclick='deleteCanonicalGroup(${g.id})'>Eliminar</button></td>
      </tr>`).join('');
      document.getElementById('canonicalGroupsTable').innerHTML = `<table><thead><tr><th>#</th><th>Key</th><th>Display</th><th>País</th><th>Ordem</th><th>Default</th><th>Activo</th><th>Acções</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function submitCreateGroup() {
      const payload = {
        key: document.querySelector("[data-group-create='key']").value.trim(),
        displayName: document.querySelector("[data-group-create='name']").value.trim(),
        country: document.querySelector("[data-group-create='country']").value.trim() || null,
        order: parseInt(document.querySelector("[data-group-create='order']").value, 10) || 0,
        isEnabled: true,
        isDefault: false,
      };
      if (!payload.key || !payload.displayName) { alert('Key e Display Name obrigatórios.'); return; }
      const r = await fetch('/api/catalog/canonical-groups', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (r.ok) {
        document.querySelector("[data-group-create='key']").value = '';
        document.querySelector("[data-group-create='name']").value = '';
        document.querySelector("[data-group-create='country']").value = '';
        await loadCanonicalGroups();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function deleteCanonicalGroup(id) {
      if (!confirm('Eliminar o grupo #' + id + '?')) return;
      const r = await fetch('/api/catalog/canonical-groups/' + id, { method: 'DELETE' });
      if (r.ok) await loadCanonicalGroups();
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function loadGroupMappings() {
      const list = await safeFetchJson('/api/catalog/group-mappings', []);
      if (!Array.isArray(list)) { document.getElementById('groupMappingsTable').innerHTML = '<p class="muted">Erro.</p>'; return; }
      if (!list.length) { document.getElementById('groupMappingsTable').innerHTML = '<p class="muted">Nenhum mapping. Cria abaixo.</p>'; return; }
      const rows = list.map(m => `<tr>
        <td><code>${m.id}</code></td>
        <td>${m.sourceKind}</td>
        <td><code>${escapeHtml(m.sourceGroupTitle)}</code></td>
        <td>${m.canonicalGroupKey ? `<code>${escapeHtml(m.canonicalGroupKey)}</code>` : m.canonicalGroupId}</td>
        <td>${escapeHtml(m.canonicalGroupDisplayName || '')}</td>
        <td>${m.isEnabled ? '<span class="badge ok">sim</span>' : '<span class="badge err">não</span>'}</td>
        <td><button class='secondary' style='color:var(--err);' onclick='deleteGroupMapping(${m.id})'>Eliminar</button></td>
      </tr>`).join('');
      document.getElementById('groupMappingsTable').innerHTML = `<table><thead><tr><th>#</th><th>Source</th><th>Group Title</th><th>Canonical Key</th><th>Display</th><th>Activo</th><th>Acções</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function submitCreateGroupMapping() {
      const payload = {
        sourceKind: document.querySelector("[data-mapping-create='kind']").value,
        sourceGroupTitle: document.querySelector("[data-mapping-create='title']").value.trim(),
        canonicalGroupId: parseInt(document.querySelector("[data-mapping-create='groupId']").value, 10),
        isEnabled: true,
      };
      if (!payload.sourceGroupTitle || !payload.canonicalGroupId) { alert('Group title e groupId obrigatórios.'); return; }
      const r = await fetch('/api/catalog/group-mappings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (r.ok) {
        document.querySelector("[data-mapping-create='title']").value = '';
        document.querySelector("[data-mapping-create='groupId']").value = '';
        await loadGroupMappings();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function deleteGroupMapping(id) {
      if (!confirm('Eliminar o mapping #' + id + '?')) return;
      const r = await fetch('/api/catalog/group-mappings/' + id, { method: 'DELETE' });
      if (r.ok) await loadGroupMappings();
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function loadDegradation() {
      const lookback = parseInt((document.getElementById('degLookback') || {value: '10080'}).value, 10) || 10080;
      const limit = parseInt((document.getElementById('degLimit') || {value: '200'}).value, 10) || 200;
      const params = new URLSearchParams();
      params.set('lookbackMinutes', String(lookback));
      params.set('limit', String(limit));

      const list = await safeFetchJson('/api/catalog/degradation/recent?' + params.toString(), []);
      const stats = await safeFetchJson('/api/catalog/degradation/stats?lookbackMinutes=' + lookback, null);

      if (stats) {
        const rate = (stats.terminalRate * 100).toFixed(1);
        document.getElementById('degradationStats').innerHTML =
          `<strong>Lookback:</strong> ${stats.lookbackMinutes} min · ` +
          `<strong>Validated:</strong> ${stats.validatedSamples} · ` +
          `<strong>Reachable:</strong> ${stats.reachableSamples} · ` +
          `<strong>Timeout:</strong> ${stats.timeoutSamples} · ` +
          `<strong>Unreachable:</strong> ${stats.unreachableSamples} · ` +
          `<strong>Dead:</strong> ${stats.deadSamples} · ` +
          `<strong>Taxa terminal:</strong> ${rate}%`;
      }

      if (!Array.isArray(list)) { document.getElementById('degradationTable').innerHTML = '<p class="muted">Erro ao carregar degradação.</p>'; return; }
      if (!list.length) { document.getElementById('degradationTable').innerHTML = '<p class="muted">Sem streams degradados no lookback seleccionado.</p>'; return; }
      const rows = list.map(d => {
        const rate = (d.failureRate * 100).toFixed(0);
        const lastHealthy = d.lastHealthyAtUtc ? tsLocal(d.lastHealthyAtUtc) : '—';
        const availability = d.latestAvailability;
        const badge = availability === 'Dead' ? 'err'
          : availability === 'Unreachable' ? 'err'
          : availability === 'Timeout' ? 'warn' : 'ok';
        return `<tr>
          <td><code>${d.channelSourceId}</code></td>
          <td>${d.canonicalChannelId}</td>
          <td>${escapeHtml(d.sourceName)}</td>
          <td><span class='badge ${badge}'>${availability}</span></td>
          <td>${d.quality}</td>
          <td>${d.latestResponseMs} ms</td>
          <td>${tsLocal(d.latestObservedAtUtc)}</td>
          <td>${lastHealthy}</td>
          <td>${d.failedSamples}/${d.totalSamples} (${rate}%)</td>
        </tr>`;
      }).join('');
      document.getElementById('degradationTable').innerHTML = `<table><thead><tr><th>CS</th><th>Canal</th><th>Source</th><th>Estado</th><th>Quality</th><th>Response</th><th>Última observação</th><th>Última saudável</th><th>Falhas</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function loadScheduledActions() {
      const actions = await safeFetchJson('/api/scheduled-actions', []);
      if (!actions || actions.length === 0) return;
      const input = document.querySelector("[data-sched-create='action']");
      const select = document.querySelector("[data-sched-create='action-select']");
      if (!input || !select) return;
      select.innerHTML = actions.map(a => `<option value='${escapeHtml(a)}'>${escapeHtml(a)}</option>`).join('');
      input.style.display = 'none';
      select.style.display = 'block';
    }
    async function loadScheduledJobs() {
      const list = await safeFetchJson('/api/catalog/scheduled-jobs', []);
      if (!Array.isArray(list)) { document.getElementById('scheduledJobsTable').innerHTML = '<p class="muted">Erro.</p>'; return; }
      if (!list.length) { document.getElementById('scheduledJobsTable').innerHTML = '<p class="muted">Sem jobs agendados.</p>'; return; }
      const rows = list.map(j => `<tr>
        <td><code>${j.id}</code></td>
        <td><code>${escapeHtml(j.name)}</code></td>
        <td><code>${escapeHtml(j.cronExpression)}</code></td>
        <td>${escapeHtml(j.actionName)}</td>
        <td>${j.isEnabled ? '<span class="badge ok">sim</span>' : '<span class="badge err">não</span>'}</td>
        <td>${j.lastRunAtUtc ? tsLocal(j.lastRunAtUtc) : '—'}</td>
        <td>${j.nextRunAtUtc ? tsLocal(j.nextRunAtUtc) : '—'}</td>
        <td>${escapeHtml(j.lastResult || '—')}</td>
        <td>
          <button class='secondary' onclick='toggleScheduledJob(${j.id}, ${!j.isEnabled})'>${j.isEnabled ? 'Desactivar' : 'Activar'}</button>
          <button class='secondary' style='color:var(--err);' onclick='deleteScheduledJob(${j.id})'>Eliminar</button>
        </td>
      </tr>`).join('');
      document.getElementById('scheduledJobsTable').innerHTML = `<table><thead><tr><th>#</th><th>Name</th><th>Cron</th><th>Action</th><th>Activo</th><th>Último</th><th>Próximo</th><th>Resultado</th><th>Acções</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function submitCreateScheduledJob() {
      const actionInput = document.querySelector("[data-sched-create='action']");
      const actionSelect = document.querySelector("[data-sched-create='action-select']");
      const actionName = (actionSelect && actionSelect.style.display !== 'none')
        ? actionSelect.value.trim()
        : actionInput.value.trim();
      const payload = {
        name: document.querySelector("[data-sched-create='name']").value.trim(),
        cronExpression: document.querySelector("[data-sched-create='cron']").value.trim(),
        actionName: actionName,
        isEnabled: document.querySelector("[data-sched-create='enabled']").value === 'true',
      };
      if (!payload.name || !payload.cronExpression || !payload.actionName) {
        alert('Name, Cron e Action são obrigatórios.'); return;
      }
      const r = await fetch('/api/catalog/scheduled-jobs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (r.ok) {
        document.querySelector("[data-sched-create='name']").value = '';
        document.querySelector("[data-sched-create='cron']").value = '';
        actionInput.value = '';
        if (actionSelect) actionSelect.value = '';
        await loadScheduledJobs();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function toggleScheduledJob(id, next) {
      const r = await fetch('/api/catalog/scheduled-jobs/' + id + '/enabled', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ isEnabled: next }),
      });
      if (r.ok) { await loadScheduledJobs(); }
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function deleteScheduledJob(id) {
      if (!confirm('Eliminar o scheduled job #' + id + '?')) return;
      const r = await fetch('/api/catalog/scheduled-jobs/' + id, { method: 'DELETE' });
      if (r.ok) { await loadScheduledJobs(); }
      else { const err = await r.json(); alert('Erro: ' + (err.error || r.status)); }
    }

    async function loadMatchingAudits() {
      const channelEl = document.getElementById('matchingChannelId');
      const limitEl = document.getElementById('matchingLimit');
      const channelId = channelEl && channelEl.value ? parseInt(channelEl.value, 10) : null;
      const limit = limitEl ? parseInt(limitEl.value, 10) : 200;
      const params = new URLSearchParams();
      if (channelId) params.set('channelId', String(channelId));
      if (limit) params.set('limit', String(limit));

      const list = await safeFetchJson('/api/catalog/matching/recent?' + params.toString(), []);
      const stats = await safeFetchJson('/api/catalog/matching/stats', null);
      if (!Array.isArray(list)) { document.getElementById('matchingAuditsTable').innerHTML = '<p class="muted">Erro ao carregar decisões.</p>'; return; }

      if (stats) {
        document.getElementById('matchingStats').innerHTML = `
          <strong>Total:</strong> ${stats.total} · <strong>Canonical:</strong> ${stats.canonical} ·
          <strong>Alias:</strong> ${stats.alias} · <strong>Rule:</strong> ${stats.rule} ·
          <strong>Unknown:</strong> ${stats.unknown} · <strong>Últimas 24h:</strong> ${stats.last24h}`;
      }

      if (!list.length) { document.getElementById('matchingAuditsTable').innerHTML = '<p class="muted">Nenhuma decisão registada.</p>'; return; }
      const rows = list.map(a => `<tr>
        <td>${tsLocal(a.atUtc)}</td>
        <td><code>${escapeHtml(a.normalizedIdentity)}</code></td>
        <td>${escapeHtml(a.originalTitle || '—')}</td>
        <td>${escapeHtml(a.sourceGroup || '—')}</td>
        <td>${a.resolutionKind}</td>
        <td>${a.canonicalChannelId ?? '—'}</td>
        <td>${(a.confidence || 0).toFixed(2)}</td>
        <td><code>${escapeHtml(a.reasonSignature || '—')}</code></td>
      </tr>`).join('');
      document.getElementById('matchingAuditsTable').innerHTML = `<table><thead><tr><th>Quando</th><th>Identidade</th><th>Título</th><th>Group</th><th>Decisão</th><th>Canal</th><th>Conf.</th><th>Razão</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function loadCatalogSyncRuns() {
      const runs = await safeFetchJson('/api/catalog/sync-runs', []);
      if (!Array.isArray(runs)) { document.getElementById('catalogSyncRunsTable').innerHTML = '<p class="muted">Erro ao carregar sync runs.</p>'; return; }
      document.getElementById('syncRunsCount').textContent = `${runs.length} execução(ões).`;
      if (!runs.length) { document.getElementById('catalogSyncRunsTable').innerHTML = '<p class="muted">Nenhuma sync run gravada.</p>'; return; }
      const rows = runs.map(r => {
        const resultBadge = r.result && r.result.startsWith('ok') ? '<span class="badge ok">ok</span>'
          : r.result && r.result.startsWith('error') ? '<span class="badge err">error</span>'
          : '<span class="badge muted">—</span>';
        return `<tr>
          <td>${r.id}</td>
          <td>${tsLocal(r.startedAtUtc)}</td>
          <td>${tsLocal(r.finishedAtUtc)}</td>
          <td>${r.appVersion || '—'}</td>
          <td>${nfmt(r.countCreatedCrawlerManaged || 0)}</td>
          <td>${nfmt(r.countMergedIntoExternal || 0)}</td>
          <td>${nfmt(r.countProtectedExternalStreams || 0)}</td>
          <td>${nfmt(r.countRemovedCrawlerManagedStreams || 0)}</td>
          <td>${nfmt(r.countReviewRequired || 0)}</td>
          <td>${nfmt(r.countExcluded || 0)}</td>
          <td>${resultBadge}</td>
          <td><button class='secondary' onclick='loadSyncRunSteps(${r.id})'>Passos</button></td>
        </tr>`;
      }).join('');
      document.getElementById('catalogSyncRunsTable').innerHTML = `
        <table><thead><tr><th>ID</th><th>Início</th><th>Fim</th><th>Versão</th><th>Created</th><th>Merged</th><th>Protected</th><th>Removed</th><th>Review</th><th>Excluded</th><th>Resultado</th><th>Acções</th></tr></thead><tbody>${rows}</tbody></table>
        <div id='syncRunStepsDetail' style='margin-top:12px;'></div>`;
    }

    async function loadSyncRunSteps(runId) {
      const steps = await safeFetchJson('/api/catalog/sync-runs/' + runId + '/steps', []);
      const out = document.getElementById('syncRunStepsDetail');
      if (!Array.isArray(steps)) { out.innerHTML = '<p class="muted">Erro.</p>'; return; }
      if (!steps.length) { out.innerHTML = '<p class="muted">Sem passos detalhados para esta execução.</p>'; return; }
      const rows = steps.map(s => `<tr>
        <td><code>${escapeHtml(s.step)}</code></td>
        <td>${tsLocal(s.startedAtUtc)}</td>
        <td>${s.durationMs} ms</td>
        <td>${nfmt(s.itemsProcessed)} (${nfmt(s.itemsSucceeded)} ok · ${nfmt(s.itemsFailed)} fail)</td>
        <td>${escapeHtml(s.result)}</td>
      </tr>`).join('');
      out.innerHTML = `<div class='card'><h4>Passos da execução #${runId}</h4><table><thead><tr><th>Passo</th><th>Início</th><th>Duração</th><th>Itens</th><th>Resultado</th></tr></thead><tbody>${rows}</tbody></table></div>`;
    }

    async function loadPendingCountryApprovals() {
      const pending = await safeFetchJson('/api/catalog/pending-country-approvals', []);
      if (!Array.isArray(pending)) { document.getElementById('pendingCountryApprovalsTable').innerHTML = '<p class="muted">Erro ao carregar aprovações pendentes.</p>'; return; }
      const open = pending.filter(r => r.state === 'Open');
      document.getElementById('pendingCount').textContent = `${open.length} pendente(s) · ${pending.length} total.`;
      const badge = document.getElementById('pendingBadge');
      if (open.length > 0) {
        badge.textContent = open.length;
        badge.style.display = 'inline-block';
      } else {
        badge.style.display = 'none';
      }
      if (!pending.length) { document.getElementById('pendingCountryApprovalsTable').innerHTML = '<p class="muted">Nenhuma aprovação pendente.</p>'; return; }
      const rows = pending.map(r => {
        const stateBadge = r.state === 'Open' ? '<span class="badge warn">Pendente</span>'
          : r.state === 'Approved' ? '<span class="badge ok">Aprovado</span>'
          : '<span class="badge err">Reprovado</span>';
        const reasonLabel = r.reasonSignature === 'weak_country_match' ? 'Indicação fraca de país'
          : r.reasonSignature === 'affinity_no_channel' ? 'Afinidade sem canal'
          : r.reasonSignature;
        const actions = r.state === 'Open'
          ? `<button style='padding:4px 8px;' onclick='approvePendingCountryApproval(${r.id})'>Aprovar</button>
             <button class='secondary' style='padding:4px 8px;' onclick='rejectPendingCountryApproval(${r.id})'>Reprovar</button>`
          : '—';
        return `<tr>
          <td>${r.id}</td>
          <td><code>${r.normalizedIdentity || '—'}</code></td>
          <td>${r.originalTitle || '—'}</td>
          <td><span class='badge' style='background:var(--accent);color:#fff;'>${r.countryCode || '—'}</span></td>
          <td>${r.sourceGroup || '—'}</td>
          <td><span class='muted'>${reasonLabel}</span></td>
          <td>${stateBadge}</td>
          <td>${tsLocal(r.createdAtUtc)}</td>
          <td>${actions}</td>
        </tr>`;
      }).join('');
      document.getElementById('pendingCountryApprovalsTable').innerHTML = `
        <table><thead><tr><th>ID</th><th>Identidade</th><th>Título original</th><th>País</th><th>Group</th><th>Motivo</th><th>Estado</th><th>Criado</th><th>Ações</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function approvePendingCountryApproval(id) {
      if (!confirm('Aprovar este canal? Será criada uma IdentityRule com ReviewOnly que permite fuzzy matching futuro.')) return;
      const r = await fetch('/api/catalog/pending-country-approvals/' + id + '/approve', { method: 'POST' });
      if (r.ok) { loadPendingCountryApprovals(); loadCatalog(); }
      else { alert('Erro: ' + r.status); }
    }

    async function rejectPendingCountryApproval(id) {
      if (!confirm('Reprovar este canal? Será criada uma IdentityRule com Excluded que impede este canal de ser aceite.')) return;
      const r = await fetch('/api/catalog/pending-country-approvals/' + id + '/reject', { method: 'POST' });
      if (r.ok) { loadPendingCountryApprovals(); loadCatalog(); }
      else { alert('Erro: ' + r.status); }
    }

    function showAddRuleForm() { document.getElementById('addRuleForm').hidden = false; }
    function hideAddRuleForm() { document.getElementById('addRuleForm').hidden = true; }

    async function submitAddRule() {
      const identity = document.getElementById('ruleIdentity').value.trim();
      const disposition = document.getElementById('ruleDisposition').value;
      const reason = document.getElementById('ruleReason').value.trim();
      if (!identity) { alert('Identidade é obrigatória.'); return; }
      const r = await fetch('/api/catalog/identity-rules', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ normalizedIdentity: identity, disposition, reason })
      });
      if (r.ok) {
        hideAddRuleForm();
        loadCatalogRules();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function deleteRule(identity) {
      if (!confirm('Eliminar regra "' + identity + '"?')) return;
      const r = await fetch('/api/catalog/identity-rules?identity=' + encodeURIComponent(identity), { method: 'DELETE' });
      if (r.ok) { loadCatalogRules(); loadCatalog(); }
      else { alert('Erro: ' + r.status); }
    }

    let _affinityEditId = null;

    let _affinityDelimiter = ',';
    let _affinityEditKey = null;
    let _affinityGroupsCache = [];

    async function loadAffinityGroups() {
      await loadAppSettings();
      const groups = await safeFetchJson('/api/catalog/affinity-groups', []);
      if (!Array.isArray(groups)) { document.getElementById('catalogAffinityTable').innerHTML = '<p class="muted">Erro ao carregar grupos.</p>'; return; }
      _affinityGroupsCache = groups;
      document.getElementById('affinityCount').textContent = `${groups.length} grupo(s).`;
      if (!groups.length) { document.getElementById('catalogAffinityTable').innerHTML = '<p class="muted">Nenhum grupo de afinidade.</p>'; return; }
      const rows = groups.map(g => {
        const members = (g.members || []).join(', ') || '—';
        const ccBadge = g.countryCode ? `<span class='badge' style='background:var(--accent);color:#fff;'>${escapeHtml(g.countryCode)}</span>` : '—';
        const isChannel = g.kind === 'Channel';
        const kindBadge = isChannel ? "<span class='badge ok'>Canal</span>" : "<span class='badge warn'>País</span>";
        const channel = isChannel
          ? `${escapeHtml(g.canonicalChannelDisplayName || '—')} <code>${escapeHtml(g.canonicalChannelKey || '—')}</code>`
          : '—';
        return `<tr>
          <td>${escapeHtml(g.name || '—')}</td>
          <td>${kindBadge}</td>
          <td>${ccBadge}</td>
          <td>${channel}</td>
          <td><code>${escapeHtml(members)}</code></td>
          <td>${tsLocal(g.createdAtUtc)}</td>
          <td>
            <button class='secondary' style='padding:4px 8px;' onclick='editAffinityGroup(${g.id})'>Editar</button>
            <button class='secondary' style='padding:4px 8px;' onclick='deleteAffinityGroup(${g.id})'>Eliminar</button>
          </td>
        </tr>`;
      }).join('');
      document.getElementById('catalogAffinityTable').innerHTML = `
        <table><thead><tr><th>Grupo</th><th>Tipo</th><th>País</th><th>Canal</th><th>Variantes</th><th>Criado</th><th>Ações</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function loadAppSettings() {
      const s = await safeFetchJson('/api/settings', null);
      if (s && s.affinityVariantDelimiter) {
        _affinityDelimiter = s.affinityVariantDelimiter;
        const input = document.getElementById('affinityDelimiter');
        if (input) input.value = _affinityDelimiter;
      }
    }

    async function saveAffinityDelimiter() {
      const value = (document.getElementById('affinityDelimiter').value || '').trim();
      const r = await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ affinityVariantDelimiter: value })
      });
      if (r.ok) {
        await loadAppSettings();
        alert('Separador guardado: ' + _affinityDelimiter);
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function loadAffinityFormOptions() {
      await loadAppSettings();
      const channels = await safeFetchJson('/api/catalog/channels', []);
      const groups = _affinityGroupsCache.length
        ? _affinityGroupsCache
        : await safeFetchJson('/api/catalog/affinity-groups', []);
      const usedKeys = new Set((groups || [])
        .filter(g => g.kind === 'Channel')
        .map(g => g.canonicalChannelKey));
      const select = document.getElementById('affinityChannelKey');
      if (!select) return;
      const current = select.value;
      const options = ['<option value="">— selecionar canal —</option>'];
      (channels || [])
        .filter(c => !usedKeys.has(c.key) || c.key === _affinityEditKey)
        .sort((a, b) => (a.displayName || '').localeCompare(b.displayName || ''))
        .forEach(c => {
          const cc = c.country ? ' · ' + c.country : '';
          options.push(`<option value="${escapeAttr(c.key)}">${escapeHtml(c.displayName)} (${escapeHtml(c.key)})${escapeHtml(cc)}</option>`);
        });
      select.innerHTML = options.join('');
      if (current) select.value = current;
    }

    function onAffinityKindChange() {
      const kind = document.getElementById('affinityKind').value;
      const isChannel = kind !== 'country';
      document.getElementById('affinityChannelField').hidden = !isChannel;
      document.getElementById('affinityCountryField').hidden = isChannel;
      const label = document.getElementById('affinityVariantsLabel');
      if (label) {
        label.textContent = (isChannel ? 'Variantes do canal' : 'Indicadores de país')
          + ' (separadas por "' + _affinityDelimiter + '")';
      }
    }

    function showAddAffinityForm() {
      _affinityEditId = null;
      _affinityEditKey = null;
      document.getElementById('affinityFormTitle').textContent = 'Nova Afinidade';
      document.getElementById('affinitySubmitBtn').textContent = 'Guardar';
      document.getElementById('affinityEditCancelBtn').hidden = true;
      document.getElementById('affinityKind').value = 'channel';
      document.getElementById('affinityKind').disabled = false;
      document.getElementById('affinityName').value = '';
      document.getElementById('affinityCountryCode').value = '';
      document.getElementById('affinityChannelKey').disabled = false;
      document.getElementById('affinityMembers').value = '';
      document.getElementById('addAffinityForm').hidden = false;
      loadAffinityFormOptions().then(onAffinityKindChange);
    }

    function hideAddAffinityForm() {
      document.getElementById('addAffinityForm').hidden = true;
      _affinityEditId = null;
      _affinityEditKey = null;
      document.getElementById('affinityFormTitle').textContent = 'Nova Afinidade';
      document.getElementById('affinitySubmitBtn').textContent = 'Guardar';
      document.getElementById('affinityEditCancelBtn').hidden = true;
    }

    async function editAffinityGroup(id) {
      const groups = await safeFetchJson('/api/catalog/affinity-groups', []);
      if (!Array.isArray(groups)) return;
      _affinityGroupsCache = groups;
      const g = groups.find(x => x.id === id);
      if (!g) return;
      _affinityEditId = id;
      _affinityEditKey = g.kind === 'Channel' ? g.canonicalChannelKey : null;
      document.getElementById('affinityFormTitle').textContent = 'Editar Afinidade';
      document.getElementById('affinitySubmitBtn').textContent = 'Atualizar';
      document.getElementById('affinityEditCancelBtn').hidden = false;
      document.getElementById('affinityKind').value = g.kind === 'Channel' ? 'channel' : 'country';
      document.getElementById('affinityKind').disabled = true;
      document.getElementById('affinityName').value = g.name || '';
      document.getElementById('affinityCountryCode').value = g.countryCode || '';
      document.getElementById('affinityMembers').value = (g.members || []).join(_affinityDelimiter + ' ');
      document.getElementById('addAffinityForm').hidden = false;
      await loadAffinityFormOptions();
      if (g.kind === 'Channel') {
        document.getElementById('affinityChannelKey').value = g.canonicalChannelKey || '';
        document.getElementById('affinityChannelKey').disabled = true;
      }
      onAffinityKindChange();
      document.getElementById('addAffinityForm').scrollIntoView({ behavior: 'smooth' });
    }

    function cancelAffinityEdit() {
      _affinityEditId = null;
      _affinityEditKey = null;
      document.getElementById('affinityFormTitle').textContent = 'Nova Afinidade';
      document.getElementById('affinitySubmitBtn').textContent = 'Guardar';
      document.getElementById('affinityEditCancelBtn').hidden = true;
      document.getElementById('affinityName').value = '';
      document.getElementById('affinityCountryCode').value = '';
      document.getElementById('affinityChannelKey').value = '';
      document.getElementById('affinityMembers').value = '';
    }

    async function submitAddAffinityGroup() {
      const kind = document.getElementById('affinityKind').value === 'country' ? 'country' : 'channel';
      const name = document.getElementById('affinityName').value.trim();
      const countryCode = document.getElementById('affinityCountryCode').value.trim() || null;
      const canonicalChannelKey = document.getElementById('affinityChannelKey').value || null;
      const membersRaw = document.getElementById('affinityMembers').value.trim();
      if (!name) { alert('Nome do grupo é obrigatório.'); return; }
      if (!membersRaw) { alert('Variantes são obrigatórias.'); return; }
      if (kind === 'channel' && !canonicalChannelKey) { alert('Selecione o canal canónico.'); return; }
      if (kind === 'country' && !countryCode) { alert('Indique o código do país.'); return; }
      const delim = _affinityDelimiter || ',';
      const members = membersRaw.split(delim).map(m => m.trim()).filter(m => m.length > 0);
      if (!members.length) { alert('Pelo menos uma variante é obrigatória.'); return; }
      const payload = { kind, name, members };
      if (kind === 'channel') payload.canonicalChannelKey = canonicalChannelKey;
      else payload.countryCode = countryCode;
      const url = _affinityEditId
        ? '/api/catalog/affinity-groups/' + _affinityEditId
        : '/api/catalog/affinity-groups';
      const r = await fetch(url, {
        method: _affinityEditId ? 'PUT' : 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (r.ok) {
        hideAddAffinityForm();
        loadAffinityGroups();
      } else {
        const err = await r.json();
        alert('Erro: ' + (err.error || r.status));
      }
    }

    async function deleteAffinityGroup(id) {
      if (!confirm('Eliminar grupo de afinidade #' + id + '?')) return;
      const r = await fetch('/api/catalog/affinity-groups/' + id + '/delete', { method: 'DELETE' });
      if (r.ok) { loadAffinityGroups(); loadCatalog(); }
      else { alert('Erro: ' + r.status); }
    }

    async function approveReview(fingerprint) {
      const r = await fetch('/api/catalog/reviews/' + encodeURIComponent(fingerprint) + '/approve', { method: 'POST' });
      if (r.ok) { loadCatalogReviews(); loadCatalog(); }
      else { alert('Erro: ' + r.status); }
    }

    async function excludeReview(fingerprint) {
      const r = await fetch('/api/catalog/reviews/' + encodeURIComponent(fingerprint) + '/exclude', { method: 'POST' });
      if (r.ok) { loadCatalogReviews(); loadCatalog(); }
      else { alert('Erro: ' + r.status); }
    }

    document.querySelectorAll('#catalogTabs button').forEach(b => b.addEventListener('click', () => loadCatalogTab(b.dataset.ctab)));

    function showView(name) {
      console.log('[DEBUG] showView called:', name);
      if (name !== 'liverun') stopLiveRunPolling();
      document.querySelectorAll('main > section').forEach(s => s.hidden = true);
      const targetSection = document.getElementById('view-' + name);
      targetSection.hidden = false;
      console.log('[DEBUG] view-' + name + ' hidden:', targetSection.hidden);
      document.querySelectorAll('nav button').forEach(b => b.classList.toggle('active', b.dataset.view === name));
      switch (name) {
        case 'overview': loadOverview(); break;
        case 'executions': loadHistory(); break;
        case 'discovery': loadDiscovery(); break;
        case 'countries': loadCountries(); break;
        case 'playlist': loadPlaylist(); break;
        case 'dispatcharr': loadDispatcharr(); break;
        case 'catalog': loadCatalog(); break;
        case 'validation': loadValidationPolicy(); break;
        case 'liverun': loadLiveRun(); startLiveRunPolling(); break;
        case 'diagnostics': loadDiagnostics(); break;
      }
    }

    document.querySelectorAll('nav button').forEach(b => { if (!b.dataset.view) return; b.addEventListener('click', () => showView(b.dataset.view)); });
    document.getElementById('countrySelect').addEventListener('change', () => loadCountryValidation());
    ['discState','discSource','discCountry'].forEach(id => document.getElementById(id).addEventListener('change', renderDiscovery));

    showView('overview');

    window.showAddRuleForm = showAddRuleForm;
    window.hideAddRuleForm = hideAddRuleForm;
    window.submitAddRule = submitAddRule;
    window.loadCatalogReviews = loadCatalogReviews;
    window.loadCatalogSyncRuns = loadCatalogSyncRuns;
    window.loadCatalogRules = loadCatalogRules;
    window.deleteRule = deleteRule;
    window.approveReview = approveReview;
    window.excludeReview = excludeReview;
    window.loadAffinityGroups = loadAffinityGroups;
    window.showAddAffinityForm = showAddAffinityForm;
    window.hideAddAffinityForm = hideAddAffinityForm;
    window.submitAddAffinityGroup = submitAddAffinityGroup;
    window.deleteAffinityGroup = deleteAffinityGroup;
    window.editAffinityGroup = editAffinityGroup;
    window.cancelAffinityEdit = cancelAffinityEdit;
    window.onAffinityKindChange = onAffinityKindChange;
    window.saveAffinityDelimiter = saveAffinityDelimiter;
    window.loadSourceSelectionPolicy = loadSourceSelectionPolicy;
    window.saveSourceSelectionPolicy = saveSourceSelectionPolicy;
    window.loadChannelSourceSelectionPolicies = loadChannelSourceSelectionPolicies;
    window.loadChannelSourceSelectionKeys = loadChannelSourceSelectionKeys;
    window.editChannelSourceSelectionPolicy = editChannelSourceSelectionPolicy;
    window.saveChannelSourceSelectionPolicy = saveChannelSourceSelectionPolicy;
    window.deleteChannelSourceSelectionPolicy = deleteChannelSourceSelectionPolicy;

    // === PHASE 9C.4 — Live Run (polling leve; sem SSE/WebSocket, sem tail de logs) ===
    var liveRunTimer = null;
    var liveRunInFlight = false;
    var liveRunLastPollUtc = null;

    function liveRunStatusBadge(status) {
      if (status === 'running') return "<span class='badge warn'>em execução</span>";
      if (status === 'completed') return "<span class='badge ok'>concluída</span>";
      if (status === 'failed') return "<span class='badge err'>falhada</span>";
      if (status === 'pipeline-not-configured') return "<span class='badge muted'>pipeline não configurada</span>";
      if (status === 'idle') return "<span class='badge muted'>idle</span>";
      return "<span class='badge muted'>" + escapeHtml(status || '—') + "</span>";
    }

    function liveRunPhaseLabel(phase, phases) {
      if (!phase) return '—';
      var list = Array.isArray(phases) ? phases : [];
      var idx = list.indexOf(phase);
      var pos = idx >= 0 ? (' (' + (idx + 1) + '/' + list.length + ')') : '';
      return escapeHtml(phase) + pos;
    }

    function liveRunDuration(ms) {
      if (typeof ms !== 'number' || ms < 0 || !isFinite(ms)) return '—';
      var s = Math.floor(ms / 1000);
      if (s < 60) return s + 's';
      var m = Math.floor(s / 60);
      if (m < 60) return m + 'm ' + (s % 60) + 's';
      var h = Math.floor(m / 60);
      return h + 'h ' + (m % 60) + 'm';
    }

    function liveRunCountEntries(counts) {
      if (!counts || typeof counts !== 'object') return [];
      var out = [];
      Object.keys(counts).forEach(function (k) {
        var v = counts[k];
        if (typeof v !== 'number' || v === 0) return;
        out.push([k, v]);
      });
      return out;
    }

    function renderLiveRun(data) {
      var host = document.getElementById('liveRunStatus');
      var trigger = document.getElementById('liveRunTriggerState');
      var btn = document.getElementById('liveRunStartBtn');
      var countsEl = document.getElementById('liveRunCounts');
      var actsEl = document.getElementById('liveRunActivities');
      var recentEl = document.getElementById('liveRunRecent');
      var allow = !!(data && data.webAllowTrigger);

      if (trigger) {
        trigger.innerHTML = allow
          ? "Trigger manual: <span class='badge ok'>activado</span> (<code>--web-allow-trigger</code>)."
          : "Trigger manual: <span class='badge muted'>desactivado</span>. Arranque via <code>--web-allow-trigger</code>.";
      }
      if (btn) {
        btn.disabled = !allow || !!(data && data.isRunning);
        btn.textContent = (data && data.isRunning) ? 'A executar…' : 'Run now';
      }

      if (!data || data.error) {
        host.innerHTML = "<div class='card'><p class='badge err'>erro de API</p><p class='muted'>" +
          escapeHtml((data && data.error) ? data.error : 'Sem resposta do servidor.') + "</p></div>";
        if (countsEl) countsEl.innerHTML = '';
        if (actsEl) actsEl.textContent = '—';
        if (recentEl) recentEl.innerHTML = '';
        return;
      }

      var status = data.status || 'idle';
      var running = status === 'running';
      var run = running ? data : (data.lastRun || null);

      var rows = [];
      rows.push(['Estado', liveRunStatusBadge(status)]);
      rows.push(['Run ID', run && run.runId ? "<code>" + escapeHtml(run.runId) + "</code>" : '—']);
      if (run && run.mode) rows.push(['Modo', "<code>" + escapeHtml(run.mode) + "</code>"]);
      if (run && run.source) rows.push(['Origem', "<code>" + escapeHtml(run.source) + "</code>"]);
      rows.push(['Fase', run ? liveRunPhaseLabel(run.phase, run.phases) : '—']);
      if (run && run.phaseStartedAtUtc) rows.push(['Fase desde', escapeHtml(tsLocal(run.phaseStartedAtUtc))]);
      if (run && run.startedAtUtc) rows.push(['Início', escapeHtml(tsLocal(run.startedAtUtc))]);
      if (run) rows.push(['Duração', liveRunDuration(run.durationMs)]);
      var updated = running ? data.lastUpdatedAtUtc : (run ? run.finishedAtUtc : null);
      if (updated) rows.push(['Última actualização', escapeHtml(tsLocal(updated))]);
      if (run && run.lastMessage) rows.push(['Mensagem', escapeHtml(run.lastMessage)]);

      host.innerHTML = "<div class='card'><table><tbody>" + rows.map(function (r) {
        return "<tr><th style='width:200px;'>" + escapeHtml(r[0]) + "</th><td>" + r[1] + "</td></tr>";
      }).join('') + "</tbody></table></div>";

      // Contadores (LiveRunCounts tipado; nunca derivado de logs).
      var entries = liveRunCountEntries(run && run.counts ? run.counts : null);
      if (countsEl) {
        countsEl.innerHTML = entries.length
          ? entries.map(function (e) {
              return "<div class='card'><div class='muted'>" + escapeHtml(e[0]) + "</div><div style='font-size:20px;'>" + nfmt(e[1]) + "</div></div>";
            }).join('')
          : "<div class='muted'>Sem contadores para mostrar.</div>";
      }

      // Últimas actividades (feed ring buffer; só existe para o run em memória).
      var acts = run && Array.isArray(run.recentActivities) ? run.recentActivities : [];
      if (actsEl) {
        if (!acts.length) {
          actsEl.textContent = 'Sem actividades disponíveis (o feed é em memória e não é persistido).';
        } else {
          var lastActs = acts.slice(-25).reverse();
          actsEl.innerHTML = "<table><thead><tr><th>Quando</th><th>Nível</th><th>Mensagem</th></tr></thead><tbody>" +
            lastActs.map(function (a) {
              var cls = a.level === 'error' ? 'badge err' : (a.level === 'warning' ? 'badge warn' : 'badge muted');
              return "<tr><td>" + escapeHtml(tsLocal(a.timestampUtc)) + "</td><td><span class='" + cls + "'>" +
                escapeHtml(a.level || 'info') + "</span></td><td>" + escapeHtml(a.message || '') + "</td></tr>";
            }).join('') + "</tbody></table>";
        }
      }

      // Últimas execuções (24h).
      var recent = Array.isArray(data.recentRuns) ? data.recentRuns : [];
      if (recentEl) {
        if (!recent.length) {
          recentEl.innerHTML = "<p class='muted'>Sem execuções registadas nas últimas 24h.</p>";
        } else {
          recentEl.innerHTML = "<table><thead><tr><th>Run ID</th><th>Modo</th><th>Origem</th><th>Início</th><th>Fim</th><th>Duração</th><th>Estado</th></tr></thead><tbody>" +
            recent.map(function (r) {
              var badge = r.terminalStatus === 'completed' ? "<span class='badge ok'>ok</span>"
                : (r.terminalStatus === 'failed' ? "<span class='badge err'>falhou</span>" : "<span class='badge muted'>—</span>");
              return "<tr><td><code>" + escapeHtml(r.runId || '') + "</code></td><td>" + escapeHtml(r.mode || '') +
                "</td><td>" + escapeHtml(r.source || '') + "</td><td>" + escapeHtml(tsLocal(r.startedAtUtc)) +
                "</td><td>" + escapeHtml(tsLocal(r.finishedAtUtc)) + "</td><td>" + liveRunDuration(r.durationMs) +
                "</td><td>" + badge + "</td></tr>";
            }).join('') + "</tbody></table>";
        }
      }
    }

    async function loadLiveRun() {
      if (liveRunInFlight) return;
      liveRunInFlight = true;
      try {
        var r = await fetch('/api/run/status');
        var data = null;
        try { data = await r.json(); } catch (e) { data = null; }
        if (!r.ok && data && data.status !== 'pipeline-not-configured') {
          data = data || { error: 'HTTP ' + r.status };
        }
        liveRunLastPollUtc = new Date();
        renderLiveRun(data);
        var st = document.getElementById('liveRunPollState');
        if (st) st.textContent = 'actualizado às ' + liveRunLastPollUtc.toLocaleTimeString() + ' (polling 3s)';
        await loadLiveRunScheduled();
      } catch (e) {
        renderLiveRun({ error: e && e.message ? e.message : 'falha de rede' });
      } finally {
        liveRunInFlight = false;
      }
    }

    async function loadLiveRunScheduled() {
      var el = document.getElementById('liveRunScheduled');
      if (!el) return;
      var list = await safeFetchJson('/api/catalog/scheduled-jobs', []);
      if (!Array.isArray(list)) { el.innerHTML = "<p class='muted'>Erro ao carregar agendamentos.</p>"; return; }
      var mine = list.filter(function (j) {
        return j && (j.actionName === 'telegramRun' || j.actionName === 'telegramMaintainRun');
      });
      if (!mine.length) {
        el.innerHTML = "<p class='muted'>Nenhuma execução Telegram agendada. Crie um job em <b>Scheduled Jobs</b> com a acção <code>telegramRun</code>.</p>";
        return;
      }
      el.innerHTML = "<table><thead><tr><th>Nome</th><th>Cron</th><th>Acção</th><th>Activo</th><th>Próximo</th><th>Último resultado</th></tr></thead><tbody>" +
        mine.map(function (j) {
          return "<tr><td><code>" + escapeHtml(j.name || '') + "</code></td><td><code>" + escapeHtml(j.cronExpression || '') +
            "</code></td><td>" + escapeHtml(j.actionName || '') + "</td><td>" +
            (j.isEnabled ? "<span class='badge ok'>sim</span>" : "<span class='badge err'>não</span>") +
            "</td><td>" + escapeHtml(j.nextRunAtUtc ? tsLocal(j.nextRunAtUtc) : '—') +
            "</td><td>" + escapeHtml(j.lastResult || '—') + "</td></tr>";
        }).join('') + "</tbody></table>";
    }

    async function startLiveRun() {
      var btn = document.getElementById('liveRunStartBtn');
      if (btn) { btn.disabled = true; btn.textContent = 'A arrancar…'; }
      try {
        var r = await fetch('/api/run/start', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: '{}'
        });
        var body = null;
        try { body = await r.json(); } catch (e) { body = null; }
        if (r.status === 503) {
          var msg = (body && body.error) ? body.error : 'trigger indisponível';
          if (btn) { btn.textContent = 'Run now'; }
          renderLiveRun({ error: msg, webAllowTrigger: false });
          return;
        }
        if (r.status === 409) {
          // Já existe execução: o polling mostra o estado real.
          if (btn) { btn.textContent = 'Run now'; }
          await loadLiveRun();
          return;
        }
        if (!r.ok) {
          if (btn) { btn.textContent = 'Run now'; }
          renderLiveRun({ error: (body && body.error) ? body.error : ('HTTP ' + r.status) });
          return;
        }
        await loadLiveRun();
      } catch (e) {
        renderLiveRun({ error: e && e.message ? e.message : 'falha de rede' });
      }
    }

    function stopLiveRunPolling() {
      if (liveRunTimer !== null) { clearInterval(liveRunTimer); liveRunTimer = null; }
    }

    function startLiveRunPolling() {
      stopLiveRunPolling();
      // Polling leve: 3s, apenas enquanto a vista estiver activa e sem
      // pedidos sobrepostos (liveRunInFlight). Sem SSE/WebSocket.
      liveRunTimer = setInterval(function () {
        var section = document.getElementById('view-liverun');
        if (!section || section.hidden) { stopLiveRunPolling(); return; }
        if (document.hidden) return;
        loadLiveRun();
      }, 3000);
    }

    window.startLiveRun = startLiveRun;
    window.loadLiveRun = loadLiveRun;
  })();
  </script>
</body>
</html>
""";
            return s;
        }

        // ---------- Autenticação opcional por token partilhado ----------

        /// <summary>
        /// Verifica se um pedido HTTP está autorizado quando o dashboard foi iniciado
        /// com um token (<paramref name="expectedToken"/> não vazio). Se o token não
        /// foi configurado, devolve true (compatibilidade com deployments locais).
        /// Caso contrário exige o token via header <c>Authorization: Bearer &lt;token&gt;</c>
        /// ou query string <c>?token=&lt;token&gt;</c>.
        /// </summary>
        // ================= PHASE 9C.2 — Auth helpers =================

        private sealed class CredentialsPayload
        {
            [JsonPropertyName("username")]
            public string? Username { get; set; }

            [JsonPropertyName("password")]
            public string? Password { get; set; }
        }

        /// <summary>
        /// PHASE 9C.5 (F6) — Decide o modo de autorização e devolve também o
        /// estado de lifecycle efectivo, para que respostas de diagnóstico
        /// (ex.: <c>403 bootstrap-required</c>) não mintam sobre o estado.
        /// Numa só leitura, evitando divergência entre modo e estado reportado.
        /// </summary>
        private static async Task<(AuthMode Mode, ConfigurationLifecycleState State)> ResolveAuthDecisionAsync()
        {
            if (_configurationLifecycle == null && _authService == null)
            {
                // PHASE 9C.2 (S1-E) — Ausência simultânea de lifecycle e auth.
                // Só é legítima num contexto explicitamente standalone/testes.
                // Em produção (wiring falhou, p.ex. catálogo indisponível) é
                // fail-closed: nunca Legacy aberto. UserAuth sem sessão resulta
                // em 401, mantendo apenas os endpoints públicos de diagnóstico.
                return (
                    _standaloneAuthContext ? AuthMode.Legacy : AuthMode.UserAuth,
                    ConfigurationLifecycleState.NotConfigured);
            }

            var state = _configurationLifecycle != null
                ? (await _configurationLifecycle.GetStateAsync()).State
                : ConfigurationLifecycleState.NotConfigured;

            if (_authService == null)
            {
                // PHASE 9C.2 (S1) — Auth indisponível (falha de wiring). NUNCA
                // tratar isto como autorização implícita (Legacy). Em READY
                // exige autenticação (fail-closed, 401); fora de READY é
                // bootstrap. Não há mecanismo de fallback novo.
                return (
                    state == ConfigurationLifecycleState.Ready
                        ? AuthMode.UserAuth
                        : AuthMode.Bootstrap,
                    state);
            }

            var hasAdmin = await _authService.HasActiveAdminAsync();
            return (AuthModeResolver.Resolve(state, hasAdmin), state);
        }

        private static bool IsAlwaysPublicPath(string requestPath)
        {
            return requestPath.Equals("/api/version", StringComparison.OrdinalIgnoreCase)
                || requestPath.Equals("/api/configuration/lifecycle", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resultado da avaliação do token partilhado de máquina
        /// (<c>--web-token</c>).
        /// </summary>
        private enum TokenAuthorization
        {
            /// <summary>Nenhum token configurado (comportamento aberto).</summary>
            NotConfigured = 0,

            /// <summary>Token configurado e válido para este pedido.</summary>
            Authorized = 1,

            /// <summary>Token configurado mas ausente/incorrecto.</summary>
            Rejected = 2,
        }

        /// <summary>
        /// PHASE 9C.2 (B1) — Distingue "sem token configurado" de "autorizado por
        /// token". Permite que a credencial de máquina autorize pedidos sem exigir
        /// sessão humana em READY + admin.
        /// </summary>
        private static TokenAuthorization EvaluateTokenAuthorization(
            HttpListenerRequest request,
            string? expectedToken)
        {
            if (string.IsNullOrWhiteSpace(expectedToken))
            {
                return TokenAuthorization.NotConfigured;
            }

            var authorized = IsAuthorized(
                request.Headers?["Authorization"],
                request.QueryString?["token"],
                expectedToken);
            return authorized ? TokenAuthorization.Authorized : TokenAuthorization.Rejected;
        }

        private static bool IsMutatingMethod(string method)
        {
            return method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
                || method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetCookieValue(HttpListenerRequest request, string name)
        {
            try
            {
                return request.Cookies?[name]?.Value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void RedirectTo(HttpListenerResponse response, string location)
        {
            response.StatusCode = (int)HttpStatusCode.Found;
            response.RedirectLocation = location;
            response.Close();
        }

        private static void SetSessionCookie(
            HttpListenerContext context,
            string sessionId,
            DateTime expiresUtc)
        {
            var secure = context.Request.IsSecureConnection ? "; Secure" : string.Empty;
            context.Response.AppendHeader(
                "Set-Cookie",
                $"{SessionCookieName}={sessionId}; Path=/; HttpOnly; SameSite=Strict{secure}; " +
                $"Expires={expiresUtc.ToUniversalTime():R}");
        }

        private static void ClearSessionCookie(HttpListenerContext context)
        {
            var secure = context.Request.IsSecureConnection ? "; Secure" : string.Empty;
            context.Response.AppendHeader(
                "Set-Cookie",
                $"{SessionCookieName}=; Path=/; HttpOnly; SameSite=Strict{secure}; Max-Age=0");
        }

        private static async Task<string> ReadJsonBodyAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(
                request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        private static bool CsrfValid(HttpListenerRequest request, AdminSessionEntity session)
        {
            var presented = request.Headers[CsrfHeaderName];
            return !string.IsNullOrEmpty(presented) && FixedEquals(presented, session.CsrfToken);
        }

        private static object BootstrapStatusToJson(BootstrapStatus status)
        {
            return new
            {
                state = status.State.ToWireName(),
                hasActiveAdmin = status.HasActiveAdmin,
                checks = status.Checks.Select(CheckToJson),
            };
        }

        private static object CheckToJson(BootstrapCheck check)
        {
            return new
            {
                key = check.Key,
                satisfied = check.Satisfied,
                detail = check.Detail,
            };
        }

        private static async Task HandleBootstrapEndpointAsync(
            HttpListenerContext context,
            string requestPath,
            AuthMode mode)
        {
            if (mode != AuthMode.Bootstrap)
            {
                await WriteJsonAsync(
                    context.Response,
                    new { error = "bootstrap-closed" },
                    HttpStatusCode.Conflict);
                return;
            }

            if (_bootstrapService == null)
            {
                await WriteJsonAsync(
                    context.Response,
                    new { error = "bootstrap-unavailable" },
                    HttpStatusCode.ServiceUnavailable);
                return;
            }

            var method = context.Request.HttpMethod;

            if (requestPath.Equals("/api/bootstrap/status", StringComparison.OrdinalIgnoreCase)
                && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(
                    context.Response,
                    BootstrapStatusToJson(await _bootstrapService.GetStatusAsync()));
                return;
            }

            if (requestPath.Equals("/api/bootstrap/start", StringComparison.OrdinalIgnoreCase)
                && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var outcome = await _bootstrapService.StartAsync();
                var status = await _bootstrapService.GetStatusAsync();
                await WriteJsonAsync(
                    context.Response,
                    new { outcome = outcome.ToString(), state = status.State.ToWireName() });
                return;
            }

            if (requestPath.Equals("/api/bootstrap/admin", StringComparison.OrdinalIgnoreCase)
                && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await TryReadCredentialsAsync(context.Request);
                var (outcome, error) = await _bootstrapService.CreateAdminAsync(
                    payload?.Username, payload?.Password);

                var code = outcome switch
                {
                    BootstrapAdminOutcome.Created => HttpStatusCode.OK,
                    BootstrapAdminOutcome.AlreadyCreated => HttpStatusCode.OK,
                    BootstrapAdminOutcome.AlreadyReady => HttpStatusCode.Conflict,
                    BootstrapAdminOutcome.NotStarted => HttpStatusCode.Conflict,
                    _ => HttpStatusCode.BadRequest,
                };

                // Nunca ecoar a password; apenas a chave de erro estável.
                await WriteJsonAsync(
                    context.Response,
                    new { outcome = outcome.ToString(), error },
                    code);
                return;
            }

            if (requestPath.Equals("/api/bootstrap/complete", StringComparison.OrdinalIgnoreCase)
                && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var validation = await _bootstrapService.CompleteAsync();
                var status = await _bootstrapService.GetStatusAsync();
                var code = validation.Outcome switch
                {
                    BootstrapCompleteOutcome.Completed => HttpStatusCode.OK,
                    BootstrapCompleteOutcome.AlreadyReady => HttpStatusCode.OK,
                    BootstrapCompleteOutcome.InvalidConfiguration => HttpStatusCode.BadRequest,
                    _ => HttpStatusCode.Conflict,
                };

                await WriteJsonAsync(
                    context.Response,
                    new
                    {
                        outcome = validation.Outcome.ToString(),
                        state = status.State.ToWireName(),
                        checks = validation.Checks.Select(CheckToJson),
                    },
                    code);
                return;
            }

            await WriteJsonAsync(context.Response, new { error = "not-found" }, HttpStatusCode.NotFound);
        }

        private static async Task HandleSessionEndpointAsync(HttpListenerContext context)
        {
            var method = context.Request.HttpMethod;

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                if (_authService == null || !await _authService.HasActiveAdminAsync())
                {
                    await WriteJsonAsync(
                        context.Response,
                        new { error = "login-unavailable" },
                        HttpStatusCode.Conflict);
                    return;
                }

                var payload = await TryReadCredentialsAsync(context.Request);
                var throttleKey = context.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                var outcome = await _authService.LoginAsync(
                    payload?.Username, payload?.Password, throttleKey);

                if (!outcome.Success || outcome.Session == null)
                {
                    await WriteJsonAsync(
                        context.Response,
                        new { error = outcome.Error ?? "invalid-credentials" },
                        HttpStatusCode.Unauthorized);
                    return;
                }

                SetSessionCookie(context, outcome.Session.SessionId, outcome.Session.ExpiresAtUtc);
                await WriteJsonAsync(context.Response, new
                {
                    authenticated = true,
                    csrfToken = outcome.Session.CsrfToken,
                    expiresAtUtc = outcome.Session.ExpiresAtUtc.ToString("o"),
                });
                return;
            }

            var sessionId = GetCookieValue(context.Request, SessionCookieName);

            if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                var session = _authService != null
                    ? await _authService.ValidateSessionAsync(sessionId)
                    : null;

                if (session == null || !CsrfValid(context.Request, session))
                {
                    await WriteJsonAsync(
                        context.Response,
                        new { error = "authentication-required" },
                        HttpStatusCode.Unauthorized);
                    return;
                }

                await _authService!.LogoutAsync(sessionId);
                ClearSessionCookie(context);
                await WriteJsonAsync(context.Response, new { loggedOut = true });
                return;
            }

            if (_authService == null)
            {
                await WriteJsonAsync(
                    context.Response,
                    new { error = "authentication-required" },
                    HttpStatusCode.Unauthorized);
                return;
            }

            var current = await _authService.ValidateSessionAsync(sessionId);
            if (current == null)
            {
                await WriteJsonAsync(
                    context.Response,
                    new { error = "authentication-required" },
                    HttpStatusCode.Unauthorized);
                return;
            }

            await WriteJsonAsync(context.Response, new
            {
                authenticated = true,
                csrfToken = current.CsrfToken,
                expiresAtUtc = current.ExpiresAtUtc.ToString("o"),
            });
        }

        /// <summary>
        /// PHASE 9C.4 — <c>GET /api/run/status</c>: devolve o estado
        /// operacional seguro. Lê do host (que detém o coordinator);
        /// se a pipeline Telegram não estiver configurada neste
        /// processo (apenas <c>--web</c> sem <c>--telegram</c>) responde
        /// 503 com <c>pipeline-not-configured</c> (ver §N do plano).
        /// </summary>
        private static async Task HandleRunStatusEndpointAsync(HttpListenerContext context)
        {
            var host = _liveRunHost;
            var coordinator = host?.Coordinator;
            var pipelineConfigured = host is not null && host.PipelineConfigured && coordinator is not null;

            if (!pipelineConfigured)
            {
                await WriteJsonAsync(
                    context.Response,
                    LiveRunApiMappings.ToStatusPayload(
                        live: null,
                        recentFinished: null,
                        pipelineConfigured: false,
                        webAllowTrigger: _webAllowTrigger),
                    HttpStatusCode.ServiceUnavailable);
                return;
            }

            var running = coordinator!.IsRunning;
            var current = coordinator.CurrentSnapshot;

            // Últimas execuções terminadas (janela de 24h) para a lista do
            // dashboard. Uma única query; nunca expõe credenciais.
            var recentRuns = await coordinator
                .GetRecentFinishedSnapshotsAsync(10, CancellationToken.None)
                .ConfigureAwait(false);

            LiveRunSnapshot? recent = null;
            if (!running)
            {
                // Preferir o snapshot terminal em memória: é o único que
                // transporta as actividades do feed (ring buffer em
                // memória, deliberadamente não persistido). Só se aplica
                // enquanto estiver dentro da janela de 24h; após restart a
                // BD é a fonte de verdade.
                var cutoff = DateTime.UtcNow.AddHours(-RunCoordinator.RecentRunWindowHours);
                if (current is not null
                    && current.TerminalStatus is LiveRunTerminalStatus.Completed or LiveRunTerminalStatus.Failed
                    && current.FinishedAtUtc is { } finishedAt
                    && finishedAt >= cutoff)
                {
                    recent = current;
                }
                else
                {
                    recent = recentRuns.Count > 0 ? recentRuns[0] : null;
                }
            }

            await WriteJsonAsync(context.Response, LiveRunApiMappings.ToStatusPayload(
                live: running ? current : null,
                recentFinished: recent,
                pipelineConfigured: true,
                webAllowTrigger: _webAllowTrigger,
                coordinatorRunning: running,
                recentRuns: recentRuns));
        }

        /// <summary>
        /// PHASE 9C.4 — <c>POST /api/run/start</c>: arranca uma execução
        /// operacional. Não bloqueia até ao fim do run (devolve 202 com
        /// o snapshot inicial; o caller usa <c>GET /api/run/status</c>
        /// para seguir o progresso).
        /// </summary>
        private static async Task HandleRunStartEndpointAsync(HttpListenerContext context)
        {
            var host = _liveRunHost;
            if (host is null || host.Coordinator is null)
            {
                await WriteJsonAsync(
                    context.Response,
                    new { error = "pipeline-not-configured" },
                    HttpStatusCode.ServiceUnavailable);
                return;
            }

            if (!_webAllowTrigger)
            {
                await WriteJsonAsync(
                    context.Response,
                    new { error = "web-allow-trigger-disabled" },
                    HttpStatusCode.ServiceUnavailable);
                return;
            }

            LiveRunStartPayload? payload;
            try
            {
                using var reader = new StreamReader(
                    context.Request.InputStream,
                    context.Request.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                payload = string.IsNullOrWhiteSpace(body)
                    ? new LiveRunStartPayload()
                    : JsonSerializer.Deserialize<LiveRunStartPayload>(body, JsonOptions);
            }
            catch
            {
                await WriteJsonAsync(
                    context.Response,
                    new { error = "invalid payload" },
                    HttpStatusCode.BadRequest);
                return;
            }

            var request = LiveRunApiMappings.ParseStartPayload(payload);
            if (request is null)
            {
                await WriteJsonAsync(
                    context.Response,
                    new { error = "invalid payload (mode/historyHours/maxStreams)" },
                    HttpStatusCode.BadRequest);
                return;
            }

            try
            {
                var outcome = await host.Coordinator.KickStartAsync(request, CancellationToken.None)
                    .ConfigureAwait(false);
                await WriteJsonAsync(
                    context.Response,
                    LiveRunApiMappings.ToStartAcceptedPayload(outcome.Snapshot),
                    HttpStatusCode.Accepted);
            }
            catch (LiveRunPipelineNotConfiguredException)
            {
                await WriteJsonAsync(
                    context.Response,
                    new { error = "pipeline-not-configured" },
                    HttpStatusCode.ServiceUnavailable);
            }
            catch (RunAlreadyInProgressException)
            {
                // 409: o snapshot corrente é construído pelo coordinator
                // (se já terminou uma run entretanto e outra começou, é
                // possível CurrentSnapshot ser null; nesse caso devolvemos
                // apenas o erro com runId desconhecido).
                var current = host.Coordinator.CurrentSnapshot;
                if (current is null)
                {
                    await WriteJsonAsync(
                        context.Response,
                        new { error = "already-running" },
                        HttpStatusCode.Conflict);
                    return;
                }
                await WriteJsonAsync(
                    context.Response,
                    LiveRunApiMappings.ToAlreadyRunningPayload(current),
                    HttpStatusCode.Conflict);
            }
        }

        private static async Task<CredentialsPayload?> TryReadCredentialsAsync(HttpListenerRequest request)
        {
            try
            {
                var body = await ReadJsonBodyAsync(request);
                return string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<CredentialsPayload>(body, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string BuildBootstrapHtml()
        {
            return """
<!doctype html><html lang="pt"><head><meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>m3uCrawler — Configuração inicial</title>
<style>
body{font-family:system-ui,sans-serif;max-width:640px;margin:40px auto;padding:0 16px;color:#1b1b1b}
h1{font-size:20px}fieldset{margin:16px 0;padding:12px;border:1px solid #ddd;border-radius:8px}
label{display:block;margin:8px 0 4px}input{width:100%;padding:8px;box-sizing:border-box}
button{margin-top:8px;padding:8px 14px;cursor:pointer}
pre{background:#f6f6f6;padding:8px;border-radius:6px;white-space:pre-wrap;font-size:12px}
#msg{margin-top:12px}
</style></head><body>
<h1>Configuração inicial</h1>
<p>Estado: <b id="state">...</b></p>
<p id="legacy" style="display:none">Configuração existente detectada (READY). Não é necessário iniciar o bootstrap: crie apenas o primeiro administrador.</p>
<pre id="status"></pre>
<fieldset><legend>1. Iniciar bootstrap</legend><button id="start">Iniciar</button></fieldset>
<fieldset><legend>2. Primeiro administrador</legend>
<label>Utilizador</label><input id="u" autocomplete="username"/>
<label>Password (mínimo 12 caracteres)</label><input id="p" type="password" autocomplete="new-password"/>
<label>Confirmar password</label><input id="p2" type="password" autocomplete="new-password"/>
<button id="create">Criar administrador</button></fieldset>
<fieldset><legend>3. Concluir</legend><button id="complete">Concluir e activar</button></fieldset>
<p id="msg"></p>
<script>
var msg=document.getElementById('msg');
function api(path,method,body){return fetch(path,{method:method,headers:body?{'Content-Type':'application/json'}:{},body:body?JSON.stringify(body):undefined}).then(function(r){return r.text().then(function(t){var j=null;try{j=t?JSON.parse(t):null}catch(e){}return {status:r.status,json:j};});});}
function refresh(){return api('/api/bootstrap/status','GET').then(function(r){if(r.json){document.getElementById('state').textContent=r.json.state;document.getElementById('status').textContent=JSON.stringify(r.json,null,2);var ready=r.json.state==='READY';document.getElementById('legacy').style.display=ready?'block':'none';document.getElementById('start').disabled=ready;}});}
document.getElementById('start').onclick=function(){api('/api/bootstrap/start','POST',{}).then(function(r){msg.textContent='start: '+r.status;return refresh();});};
document.getElementById('create').onclick=function(){var u=document.getElementById('u').value,p=document.getElementById('p').value,p2=document.getElementById('p2').value;if(p!==p2){msg.textContent='As passwords não coincidem.';return;}api('/api/bootstrap/admin','POST',{username:u,password:p}).then(function(r){var e=r.json&&r.json.error?(' ('+r.json.error+')'):'';msg.textContent='admin: '+r.status+e;return refresh().then(function(){if(r.status===200&&document.getElementById('state').textContent==='READY'){location.href='/';}});});};
document.getElementById('complete').onclick=function(){api('/api/bootstrap/complete','POST',{}).then(function(r){msg.textContent='complete: '+r.status;return refresh().then(function(){if(r.status===200){location.href='/';}});});};
refresh();
</script></body></html>
""";
        }

        private static string BuildLoginHtml()
        {
            return """
<!doctype html><html lang="pt"><head><meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>m3uCrawler — Login</title>
<style>
body{font-family:system-ui,sans-serif;max-width:420px;margin:80px auto;padding:0 16px;color:#1b1b1b}
h1{font-size:20px}label{display:block;margin:10px 0 4px}
input{width:100%;padding:8px;box-sizing:border-box}button{margin-top:12px;padding:8px 14px;cursor:pointer}
#msg{margin-top:12px;color:#b00020}
</style></head><body>
<h1>Entrar</h1>
<label>Utilizador</label><input id="u" autocomplete="username"/>
<label>Password</label><input id="p" type="password" autocomplete="current-password"/>
<button id="go">Entrar</button>
<p id="msg"></p>
<script>
document.getElementById('go').onclick=function(){
var u=document.getElementById('u').value,p=document.getElementById('p').value;
fetch('/api/session',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username:u,password:p})})
.then(function(r){if(r.ok){location.href='/';return;}document.getElementById('msg').textContent='Credenciais inválidas.';});
};
</script></body></html>
""";
        }

        public static bool IsRequestAuthorized(HttpListenerRequest request, string? expectedToken)
        {
            if (string.IsNullOrWhiteSpace(expectedToken)) return true;
            return IsAuthorized(request.Headers?["Authorization"], request.QueryString?["token"], expectedToken);
        }

        /// <summary>
        /// Lógica pura (testável sem HttpListener): valida token via header Bearer
        /// ou query <c>?token=</c>. Comparação em tempo constante.
        /// </summary>
        public static bool IsAuthorized(string? authorizationHeader, string? queryToken, string? expectedToken)
        {
            if (string.IsNullOrWhiteSpace(expectedToken)) return true;

            if (!string.IsNullOrEmpty(authorizationHeader))
            {
                const string prefix = "Bearer ";
                if (authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var presented = authorizationHeader.Substring(prefix.Length).Trim();
                    if (FixedEquals(presented, expectedToken)) return true;
                }
            }

            if (!string.IsNullOrEmpty(queryToken))
            {
                var presented = queryToken.Trim();
                if (FixedEquals(presented, expectedToken)) return true;
            }

            return false;
        }

        private static bool FixedEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var ab = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            return CryptographicOperations.FixedTimeEquals(ab, bb);
        }

        private static async Task WriteUnauthorizedAsync(HttpListenerResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            response.Headers["WWW-Authenticate"] = "Bearer realm=\"m3uCrawler\"";
            await WriteTextAsync(response, "Não autorizado. Forneça o token via 'Authorization: Bearer <token>' ou '?token=<token>'.", HttpStatusCode.Unauthorized);
        }
    }
}

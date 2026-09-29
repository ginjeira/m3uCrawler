# W-FIRST-E2E-TEST — prova E2E HTTP do primeiro Run

- Data: 2026-09-29
- Estado: Concluída
- Wave pai: `2026-09-29-w-pre-first-e2e.md` (P0.1, F-22) — fechou o caminho do trigger.
- Próxima wave: First Real Test (manual, com Telegram/Dispatcharr reais; procedimento documentado em §13).

## 1. Objectivo

Demonstrar, **com teste automatizado** e de ponta-a-ponta sobre a superfície HTTP real (`HttpListener` + `WebDashboardService`), que um administrador autenticado consegue:

1. Iniciar um Run via `POST /api/run/start`.
2. Acompanhar o progresso via `GET /api/run/status`.
3. Ver o Run chegar a estado terminal (`Completed` ou `Failed`).
4. Encontrar o estado persistido em `LiveRunEntity`.
5. Encontrar os artefactos de publicação em disco (`telegram_playlist_*.m3u`, `telegram_report_*.json`, `telegram_run_report.json`, `import_history.json`).
6. Ver o cursor `publicationPending` do DL-130 a convergir para `false`.
7. Distinguir **falha real** (acquisition Telegram falha → Run termina `Failed`) de um falso `Completed`.

Sem rede, sem WTelegram, sem Dispatcharr HTTP, sem acquisition HTTP.

## 2. Arquitectura do teste

### 2.1 Topologia

```
HttpListener (127.0.0.1:<port>)
       ↑ HTTP request
       │
HttpClient (.NET, com CookieContainer para sessão)
       ↑ POST /api/run/start (+ X-CSRF-Token)
       │ GET /api/run/status (polling 50 ms até terminal)
       │ GET /api/publication/status (DL-130 cursor)
       │
WebDashboardService.HandleRequestWithAuthOnTestAsync
       │ (gate CSRF + Bootstrap + UserAuth avaliados aqui — gate do
       │  trigger avaliado dentro de HandleRunStartEndpointAsync)
       ↓
HandleRunStartEndpointAsync
       ↓
LiveRunHost.Coordinator.KickStartAsync → RunCoordinator.StartAsync
       ↓
TelegramLiveRunExecutor REAL (com delegate fake na fronteira)
       ├── TelegramDiscoveryDelegate (fake mínimo): parse M3U, IsWorking=true,
       │     PipelineIngestionService REAL, RunReport autoritativo.
       ↓
RunPublicationService REAL (Dispatcharr desactivado por config)
       ↓
PlaylistManagerService.SaveToM3uPlaylistAtomic (DL-019)
ImportHistoryService.RecordImportAsync
       ↓
LiveRunEntity persistido (TerminalStatus=Completed)
publicationStatusService lê cursors (DL-130)
```

### 2.2 Fronteiras substituídas

| Fronteira | Substituição | Motivo |
|---|---|---|
| `TelegramScraperService.SearchAndTestM3UInTelegramAsync` | `TelegramDiscoveryDelegate` lambda que replica o contrato interno do scraper | Telegram real requer autenticação, sessão, rede, e introduz flake. O delegate é a fronteira oficial exposta por `TelegramLiveRunExecutor` (linha 21). |
| Acquisition HTTP de streams (`M3uTesterService`) | `streams.IsWorking = true` no delegate | Acquisition real requer rede; `OperationalEndToEndRunTests.cs:196` adopta a mesma convenção (documentada como "acquisition bypass — fora do scope"). |
| `DispatcharrSyncCoordinator.RunAsync` | `new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled())` | Dispatcharr real requer HTTP contra Dispatcharr externo (ou stub local). Esta wave valida o caminho de publicação, não o caminho Dispatcharr activo — esse é objecto de uma wave separada (já parcialmente coberto por `DispatcharrSyncServiceTests`, `WaveW6cScheduledDispatcharrSelectionTests`). |

Tudo o resto (RunCoordinator, TelegramLiveRunExecutor, PipelineIngestionService, CatalogResolver, RecognitionPolicyResolver, RunPublicationService, PlaylistManagerService, ImportHistoryService, PublicationStatusService, LiveRunHost, WebDashboardService) é a implementação real.

### 2.3 HTTP server (harness)

Classe privada `E2EFirstRunHarness` (em `m3uCrawler.Tests/WFirstE2EHttpTests.cs`), cópia deliberada do padrão `Phase94RunApiHarness` (`m3uCrawler.Tests/Phase94LiveRunApiTests.cs:791-906`):

- `HttpListener` em `127.0.0.1:<port>` (porta livre alocada via `TcpListener(IPAddress.Loopback, 0)`).
- Loop `listener.GetContextAsync()` + dispatch por scope `StaticLiveRunHostScope` (que troca os statics `_liveRunHost` e `_webAllowTrigger` para a duração de uma request e restaura no `Dispose`).
- Chama `WebDashboardService.HandleRequestWithAuthOnTestAsync` (modo autenticado), que avalia os gates CSRF + Bootstrap + UserAuth antes de delegar ao `HandleRunStartEndpointAsync`.

Padrão de cópia por classe de teste (e não partilha entre classes) é a convenção do projecto — ver AGENTS.md §4 e os múltiplos testes `*Harness` em `WaveW2AcquisitionRetryTests`, `WaveW2AcquisitionPersistenceTests`, `WaveW5TelegramClientReuseTests`, `WaveW55ReviewApiTests`, etc.

### 2.4 Autenticação

Segue o protocolo `Phase94LiveRunApiTests.ReachReadyAndLoginAsync` (`Phase94LiveRunApiTests.cs:128-154`):

1. `POST /api/bootstrap/start` → `{}`
2. `POST /api/bootstrap/admin` → `{username: "admin", password: ...}`
3. `POST /api/bootstrap/complete` → `{}`
4. `POST /api/session` → `{username, password}` → resposta inclui `csrfToken`
5. CSRF devolvido é ecoado em cada POST subsequente no header `X-CSRF-Token`.

## 3. Fixture M3U

Idêntica à de `OperationalEndToEndRunTests.M3UFixture` (`OperationalEndToEndRunTests.cs:137-144`) — reutilização deliberada:

```m3u
#EXTM3U
#EXTINF:-1 tvg-id="rtp1.pt" group-title="Portugal",RTP 1
http://test.example/rtp1.m3u8
#EXTINF:-1 tvg-id="unknown.pt" group-title="Portugal",Canal Misterioso
http://test.example/misterio.m3u8
#EXTINF:-1 tvg-id="" group-title="Portugal",RTP 2
http://test.example/rtp2.m3u8
```

3 streams PT: 2 com `tvg-id` exacto (`rtp1.pt`/`RTP 2` — match directo, viram `ChannelSource`), 1 sem identidade canónica (`unknown.pt`/`Canal Misterioso` — vai para `ReviewItem`).

Baseline PT `pt.json` mínimo é gerado no directório da fixture pelo próprio teste (`WFirstE2EHttpTests.CreateCountryValidator`, escreve `pt.json` em `_root`), contendo os 2 canais conhecidos + 1 alias que aceita o Canal Misterioso para que o country gate o deixe passar antes do recognition (idêntico a `EndToEndPublicationTests`).

## 4. Fluxo executado (happy path)

### 4.1 Arrange

- `TestTempDb.SuitePath("e2e-first-<guid>")` para root isolado.
- `ChannelCatalogBootstrapper` migra SQLite.
- `CatalogResolver`, `PlaylistComposerService`, `ImportHistoryService`, `ConfigurationLifecycleService`, `AuthService`, `BootstrapService`, `RecognitionPolicyResolver`, `CountryChannelValidator` — instâncias reais, isoladas.
- `RunPublicationService` construído com `DispatcharrSyncCoordinator(DispatcharrConfig.Disabled())`.
- `TelegramLiveRunExecutor` construído com o delegate fake e o publication service.
- `LiveRunHost` configurado com `host.ConfigureExecutor(_ => executor)`.
- `host.Coordinator.SetRecognitionPolicyResolver(...)` para activar o snapshot lifecycle.
- `PublicationStatusService` ligado via `WebDashboardService.SetPublicationStatusService(...)` (necessário para validar `publicationPending`).

### 4.2 Act

- `E2EFirstRunHarness.Start(...)` arranca o `HttpListener` real.
- `ReachReadyAndLoginAsync(harness)` → csrf token.
- `POST /api/run/start` com `X-CSRF-Token: <csrf>`, body `{}` → `202 Accepted` com `{runId: <guid>}`.
- `PollUntilTerminalAsync(30s)` faz GET a `/api/run/status` em loop de 50ms até `status == "completed" | "failed"`. Nunca observa `"idle"` durante o fecho (contrato garantido em `LiveRunApiMappings.ToStatusPayload`).
- `GET /api/publication/status` → `200 OK` com `{publicationPending, catalogChangedAtUtc, lastSuccessfulPublicationAtUtc, ...}`.

### 4.3 Assert

- HTTP `202` na partida.
- Poll retorna `("completed", "completed")`.
- `GET /api/run/status` final: `isRunning=false`, `status="completed"`, `webAllowTrigger=true`, `lastRun.runId` presente.
- DB: 1 `LiveRunEntity` com `TerminalStatus=Completed`, `LastMessage="completed"`, `FinishedAtUtc` não-nulo.
- DB: 2 `ChannelSourceEntity` com `MatchMethod="CanonicalExact"`, 1 `ReviewItem` (`canal misterioso`, `Open`), N `DiscoveryCandidate` todos com o `RunId` correcto.
- Disco: 1 `telegram_playlist_*.m3u` (3 http lines — SourceSelection NoOp preserva as 3 streams), 1 `telegram_report_*.json`, `telegram_run_report.json`, `import_history.json`.
- DL-130: `publicationPending=false`, `lastSuccessfulPublicationAtUtc != null`.

## 5. Fluxo de falha

`Post_run_start_with_failing_acquisition_terminates_failed` substitui o delegate por um lambda que lança `InvalidOperationException("W-FIRST-E2E-TEST simulated Telegram failure")`. O `RunCoordinator` deve propagar a falha como `LiveRunTerminalStatus.Failed`.

Assert:
- Poll retorna `("failed", "failed")`.
- DB: `LiveRunEntity.TerminalStatus == Failed`.
- Disco: **nenhum** `telegram_playlist_*.m3u` (publicação não foi atingida).

## 6. Gates obrigatórios (dois testes extra)

### 6.1 Sem CSRF

`Post_run_start_without_csrf_returns_403_and_does_not_trigger_run` faz bootstrap+login mas envia POST sem header `X-CSRF-Token`. Espera:
- `403 Forbidden`
- Body contém `"csrf-invalid"`
- DB: 0 `LiveRunEntity` (gate bloqueou antes do coordinator)

### 6.2 Trigger desactivado

`Post_run_start_with_trigger_disabled_returns_503_and_does_not_trigger_run` faz bootstrap+login (necessário porque gate CSRF/Bootstrap roda antes do gate do trigger em `HandleRequestWithAuthOnTestAsync`), mas o `webAllowTrigger=false` no harness. Espera:
- `503 ServiceUnavailable`
- Body contém `"web-allow-trigger-disabled"`
- DB: 0 `LiveRunEntity`

## 7. Resultados

### 7.1 Build

```
$ dotnet build m3uCrawler.sln --configuration Release
Build succeeded.
0 errors, 53 warnings (todas pré-existentes, xUnit analyzer; sem warnings novos introduzidos por WFirstE2EHttpTests.cs)
```

### 7.2 Testes desta wave

```
$ dotnet test --filter "FullyQualifiedName~WFirstE2EHttpTests"
Total: 4 | Passed: 4 | Failed: 0 | Skipped: 0
Duration: 6.97 s

  ✓ Post_run_start_without_csrf_returns_403_and_does_not_trigger_run       666 ms
  ✓ Post_run_start_walks_real_pipeline_to_completed_terminal_state         ~1 s
  ✓ Post_run_start_with_trigger_disabled_returns_503_and_does_not_trigger_run  344 ms
  ✓ Post_run_start_with_failing_acquisition_terminates_failed              591 ms
```

### 7.3 Suite completa (regression check)

```
$ dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release
Total: 2710 | Passed: 2708 | Failed: 1 | Skipped: 1
Duration: 3 m 5 s
```

O **1 teste que falha** é `WaveW6b2ObservabilityTests.Dashboard_endpoints_return_newly_produced_runs_and_observations` — falha **pré-existente** nesta branch (verificada via reprodução isolada 3/3 vezes após mover `WFirstE2EHttpTests.cs` para fora do build). Não foi introduzida nem exacerbada por esta wave. Está fora do âmbito de W-FIRST-E2E-TEST; documentar como finding.

O **1 teste skipped** é `HttpTimeoutAutopsyTests.LEGACY_PATTERN_blackhole_blocks_until_30s_HttpClient_timeout` — pre-existing skip.

**Zero regressões atribuíveis a esta wave.**

## 8. Artefactos produzidos

`m3uCrawler.Tests/WFirstE2EHttpTests.cs` (684 linhas) — uma única classe de teste nova. Não foram criados ficheiros auxiliares, fixtures externas, ou helpers partilhados (o harness HTTP é uma cópia local do padrão estabelecido, conforme convenção do projecto).

## 9. Limitações

1. **Dispatcharr não é exercitado em modo activo.** O coordinator é injectado com `DispatcharrConfig.Disabled()` e produz zero HTTP. O caminho Dispatcharr activo está coberto por `DispatcharrSyncServiceTests` + `WaveW6cScheduledDispatcharrSelectionTests` (suite de testes dedicada). Combinando este teste com os existentes, temos cobertura completa: pipeline → publicação (este teste) + plan/apply Dispatcharr (outros testes).
2. **Acquisition HTTP não é exercitada.** O delegate marca `streams.IsWorking = true` directamente. O tester (`M3uTesterService`) real nunca é invocado. Esta é a mesma convenção que `OperationalEndToEndRunTests.cs:196` e `EndToEndPublicationTests.cs:190`. O seam existe (`M3uTesterService` aceita `HttpMessageHandler` via ctor `internal`), mas exercitá-lo no teste HTTP E2E seria trabalho de outra wave.
3. **Run começa, mas a discovery não é "real".** O delegate ingere as 3 streams da fixture directamente sem passar pelo scraper; o `RunReport.CandidatesFound=1` reflecte isso.
4. **Sem `m3uCrawler_main.json` (DL-130 não exercita `lastDispatcharrSuccessAtUtc`)** — D-OPEN-05 do DL-130 está fora do scope desta wave.
5. **Cobertura parcial do UI Bootstrap flow.** Esta wave usa o caminho não-standalone (`HandleRequestWithAuthOnTestAsync`). O fluxo `BuildBootstrapHtml` é exercitado pelo login CSRF (que passa pelo gate `bootstrap-required` quando aplicável).

## 10. Findings / observações

### 10.1 Falha pré-existente (não bloqueante)

`WaveW6b2ObservabilityTests.Dashboard_endpoints_return_newly_produced_runs_and_observations` (`WaveW6b2ObservabilityTests.cs:315`) falha deterministicamente desde antes desta wave. Reproduz 3/3 vezes isolado. Suspeita: dependência de ordem entre testes da mesma classe (`IAsyncLifetime` + `DashboardHarness.Start` partilhado). Recomendação para wave futura: dividir o teste em dois ou usar `IDbContextFactory` único por teste. **Fora do scope W-FIRST-E2E-TEST.**

### 10.2 Ordem dos gates CSRF vs trigger (observação de design)

Em `WebDashboardService.HandleRequestWithAuthOnTestAsync` (linhas 595-650), os gates são avaliados **nesta ordem**:
1. Bootstrap (se lifecycle indica NOT_CONFIGURED → 403)
2. UserAuth (sessão+CSRF; ou machine token)
3. → `HandleRunStartEndpointAsync`:
4. pipeline-not-configured (503)
5. `webAllowTrigger` (503)

Isto significa que um POST sem login **nunca** vê o gate do trigger — recebe 403 bootstrap-required primeiro. **Decisão de design correcta** (não revela estado do trigger a utilizadores não autenticados). O teste `Post_run_start_with_trigger_disabled_returns_503_and_does_not_trigger_run` faz bootstrap+login para chegar ao gate do trigger. Documentado inline no teste.

### 10.3 `terminalStatus` aninhado em `lastRun` no payload terminal

No `GET /api/run/status` para um Run terminado, o campo top-level `status` é `"completed"`/`"failed"` (string), mas `terminalStatus` está dentro de `lastRun.terminalStatus` (não top-level). Ver `LiveRunApiContracts.SnapshotToFinishedPayload:185-222`. Documentado no `PollUntilTerminalAsync` como referência cruzada.

### 10.4 Convenção `E2EFirstRunHarness` é uma cópia local

Seguindo a convenção do projecto (cada classe de teste que precisa de HTTP tem o seu próprio harness privado), `E2EFirstRunHarness` é cópia de `Phase94RunApiHarness`. AGENTS.md §4 adverte contra refactor especulativo; refactor para extrair harness partilhado é trabalho de outra wave.

### 10.5 Gap de cobertura detectado no First Real Test (W-FIX-WT-AUTH-PHONE-NUMBER)

O First Real Test manual (execução real contra `/opt/m3ucrawler-first-test/runtime-data/wtelegram.config`) apanhou um bug que esta suite de testes E2E **não detectava**:

* **Sintoma:** o pipeline Telegram levantava `WTException: You must provide a config value for phone_number` no segundo login (`SearchM3UInTelegramInternal` → `client.LoginUserIfNeeded()`), apesar de `wtelegram.config` conter `phone_number` correctamente.
* **Causa:** o delegate privado `Config(string what)` de `WTelegramAuthBackend` (linhas 49-58, antes desta wave) tinha apenas três casos (`api_id`, `api_hash`, `session_pathname`) — `phone_number` caía no default `_ => null`. O telefone era passado directamente em `BeginLoginAsync` (`_client.Login(phone)`, `WTelegramAuthBackend.cs:39`), pelo que o primeiro login funcionava; mas `LoginUserIfNeeded()` consulta o delegate e falhava.
* **Por que o E2E HTTP não apanhou:** `WFirstE2EHttpTests` (e `OperationalEndToEndRunTests`) injectam um `TelegramDiscoveryDelegate` fake — nunca instanciam `WTelegramAuthBackend` nem chamam `LoginUserIfNeeded`. A contract do delegate `Config` não estava coberta por nenhum teste.
* **Fix (wave seguinte):** adicionar `"phone_number" => _options.Phone` ao switch em `WTelegramAuthBackend.Config` + teste unitário `WTelegramAuthBackendConfigDelegateTests` que invoca o delegate via reflection. Resultado: 4/4 testes do fix passam; suite completa 2711/1/1 (única falha continua a ser o pré-existente `WaveW6b2ObservabilityTests`).

A lição para waves futuras: testes que usem apenas fakes no seam Telegram validam o pipeline, mas não a contract privada do backend WTelegram. Para cobrir a contract do delegate, é necessário um teste unitário dedicado (como o que foi adicionado em W-FIX-WT-AUTH-PHONE-NUMBER).

## 11. Validação runtime

Realizada **no ambiente** (`/root/.dotnet/dotnet` instalado via `dotnet-install.sh`):

| Comando | Resultado |
|---|---|
| `dotnet build m3uCrawler.sln --configuration Release` | 0 errors, 53 warnings (pré-existentes) |
| `dotnet test ... --filter WFirstE2EHttpTests` | 4/4 passed |
| `dotnet test ...` (full suite) | 2708/1/1 (1 fail pré-existente, 1 skip pré-existente) |
| `dotnet test ...` (WFirstE2EHttpTests movido para fora) | WaveW6b2ObservabilityTests.Dashboard_endpoints_* falha 3/3 (confirmado pré-existente) |

## 12. Estado Git

### 12.1 Antes
- Branch: `feature/phase-9c-first-run-dashboard`
- HEAD: `16a6b3c8c5db8281cbf657f70821e051e21646c0` (commit de W-PRE-FIRST-E2E)
- Working tree: limpo (além de waves docs untracked pré-existentes)

### 12.2 Depois (esperado)
- 1 ficheiro novo: `m3uCrawler.Tests/WFirstE2EHttpTests.cs`
- 1 ficheiro novo: `docs/project/waves/2026-09-29-w-first-e2e-test.md` (este)
- 0 ficheiros de código fora do âmbito alterados
- Sem push

## 13. FIRST REAL TEST — procedimento manual (próximo passo)

Este procedimento é o teste operacional seguinte, com Telegram e Dispatcharr **reais**. Não automatizado nesta wave; documentado para a próxima sessão de teste manual.

### 13.1 Pré-condições

- Docker Desktop a correr (Windows ou Linux).
- Bind mount `C:\Users\ULSSJOSE\m3ucrawler\runtime-data` (local dev) ou `/opt/m3ucrawler/runtime-data` (prod) disponível.
- Sessão Telegram válida (`session.dat` byte-equivalente ao de produção, ou nova sessão via autenticação interactiva).
- `wtelegram.config` configurado com `api_id`, `api_hash`, `phone_number`.

### 13.2 Passos

```text
1.  Abrir terminal em C:\Users\ULSSJOSE\Repos\m3uCrawler (Windows) ou equivalente.

2.  Confirmar que --web-allow-trigger está no compose:
    - docker-compose.yml:22-25 deve listar --web-allow-trigger após --web-port "5000".
    - docker-compose.local.yml:39 e :69 devem listar --web-allow-trigger nos dois serviços.

3.  Reconstruir imagem local (após merge desta wave):
    docker build -t m3ucrawler:local -f m3uCrawler/Dockerfile \
      --build-arg M3uCrawlerVersion=refs/heads/main \
      --build-arg M3uCrawlerCommitSha=$(git rev-parse HEAD) \
      --build-arg M3uCrawlerBuildNumber=0 \
      --build-arg M3uCrawlerBuildDate=$(Get-Date -Format 'o') .

4.  Arrancar dashboard (sem Telegram, port 5000):
    docker compose -f docker-compose.local.yml up -d
    # Esperar ~3s; abrir http://localhost:5000/

5.  Bootstrap wizard:
    - Step 1: Start (botão "Start")
    - Step 2: Criar admin (username + password ≥ 12 chars)
    - Step 3: Complete
    - Redirect para /login.

6.  Autenticar:
    - Username + password
    - Cookie m3u_session + CSRF token persistidos.

7.  Configurar Telegram:
    - Dashboard → Telegram → preencher api_id, api_hash, phone_number.
    - "Start authentication" → inserir verification_code (enviado pelo Telegram).
    - Se pedido, inserir password 2FA.
    - Estado: Authenticated.

8.  Confirmar configuração:
    - Dashboard → "Discovery settings": keyword="portugal", historyHours=24, maxStreams=500.
    - Dashboard → "Dispatcharr config": se aplicável, URL + API key + dry_run=true.
    - Dashboard → "Dispatcharr test": ver "Connected".

9.  Iniciar Run:
    - Dashboard → Overview → botão "Run now".
    - Ver Live Run status (polling 3s): reading-telegram → discovering → downloading → analyzing → validating → composing → syncing-dispatcharr → completed.

10. Ver playlists encontradas:
    - Dashboard → "Run report" → candidates, playlists, streams.

11. Confirmar streams funcionais:
    - Run report summary → counts.StreamsWorking ≥ 1.

12. Confirmar catálogo:
    - Dashboard → "Catalog" → Channels → RTP 1, RTP 2 devem estar activos (se foram matched).

13. Confirmar playlist.m3u:
    - ls output/playlist.m3u (ou bind mount /opt/playlists/playlist.m3u em prod).
    - head output/playlist.m3u → ver linhas http://.

14. Confirmar Dispatcharr (se dry_run=true):
    - ls output/dispatcharr_plan_<ts>.json (plano produzido).
    - ls output/dispatcharr_report_<ts>.json (relatório produzido).
    - Ver dashboard card "Publicação do catálogo": publicationPending=false.

15. Aplicar Dispatcharr (se dry_run=false em wtelegram.config):
    - Dashboard → "Dispatcharr" → botão "Sync".
    - Verificar em Dispatcharr que os streams/channels foram criados (is_custom=true).

16. Confirmar estado final:
    - Dashboard → Overview → 5 cards OK.
    - /api/publication/status → publicationPending=false, lastSuccessfulPublicationAtUtc não-nulo.

17. (Opcional) Rever items pendentes:
    - Dashboard → "Review" → items Open → ignore / resolve.

18. (Opcional) Verificar histórico:
    - Dashboard → "History" → últimas 72h → run com terminalStatus=completed.
```

### 13.3 Critérios de aceitação

O teste manual é considerado bem-sucedido quando:

- [ ] Botão "Run now" está habilitado (UI mostra badge "Trigger manual: activado").
- [ ] Run completa em ≤ 5 min (Telegram discovery + testes de streams).
- [ ] `output/playlist.m3u` contém ≥ 1 stream funcional.
- [ ] `output/dispatcharr_plan_*.json` + `output/dispatcharr_report_*.json` existem (se Dispatcharr enabled).
- [ ] `GET /api/publication/status` retorna `publicationPending: false`.
- [ ] Dashboard "Overview" mostra estado terminal `completed`.

Se algum critério falhar, o diagnóstico deve seguir a ordem:
1. `docker logs <container>` — verificar erros de Telegram/Dispatcharr.
2. `output/telegram_run_report.json` — verificar counts.
3. `GET /api/audit` — verificar entradas de erro.
4. `GET /api/run/status` — verificar terminal message.

## 14. Critérios de sucesso da wave (do brief)

| # | Critério | Cumprido? | Evidência |
|---|---|---|---|
| E1 | Existe teste HTTP E2E real | ✓ | `WFirstE2EHttpTests.cs` |
| E2 | O teste inicia o Run através da superfície HTTP real | ✓ | `POST /api/run/start` com CSRF; `Accept 202` |
| E3 | O Run atravessa o pipeline real | ✓ | RunCoordinator, TelegramLiveRunExecutor, PipelineIngestionService, CatalogResolver, RunPublicationService — todos reais; apenas a fronteira Telegram é substituída por delegate |
| E4 | O teste observa o estado terminal real | ✓ | `GET /api/run/status` retorna `status="completed"` (happy) ou `"failed"` (failure); polling a 50ms com timeout 30s |
| E5 | A publicação é validada | ✓ | `telegram_playlist_*.m3u` existe; 3 http lines; `import_history.json` existe |
| E6 | A interação Dispatcharr é validada, quando aplicável | parcial | Esta wave valida o caminho `Disabled()` (não invocado). O caminho activo está coberto por `DispatcharrSyncServiceTests` + `WaveW6cScheduledDispatcharrSelectionTests` (suite dedicada). |
| E7 | Existe pelo menos um failure path significativo | ✓ | `Post_run_start_with_failing_acquisition_terminates_failed` |
| E8 | Os testes existentes continuam a passar | ✓ | 2708/1/1 baseline preservado (1 fail pré-existente, não relacionado) |
| E9 | O teste é determinístico e não depende da Internet | ✓ | Sem rede; sem WTelegram; sem Dispatcharr HTTP; sem acquisition HTTP |
| E10 | Fica documentado o procedimento para o primeiro teste manual com Telegram/Dispatcharr reais | ✓ | §13 |

## 15. Resposta às perguntas do brief §29

1. **Estado Git inicial:** `feature/phase-9c-first-run-dashboard` @ `16a6b3c` (W-PRE-FIRST-E2E commit).
2. **Arquitectura E2E:** HttpListener real + WebDashboardService.HandleRequestWithAuthOnTestAsync + RunCoordinator + TelegramLiveRunExecutor + RunPublicationService; apenas as 3 fronteiras externas (Telegram scraper, M3uTesterService acquisition, Dispatcharr HTTP) são substituídas.
3. **Fronteiras externas substituídas:** §2.2.
4. **Testes criados:** 4 (`WFirstE2EHttpTests.cs`).
5. **Fixture utilizado:** `M3UFixture` 3 streams PT (idêntica a `OperationalEndToEndRunTests.M3UFixture`).
6. **Happy path:** `Post_run_start_walks_real_pipeline_to_completed_terminal_state` — 1s.
7. **Failure path:** `Post_run_start_with_failing_acquisition_terminates_failed` — 591ms.
8. **Resultados dos testes:** 4/4 passed.
9. **Build:** 0 errors, 53 warnings (pré-existentes, xUnit analyzers).
10. **Validação HTTP/runtime:** §11.
11. **Artefactos produzidos:** `WFirstE2EHttpTests.cs` (684 linhas) + este wave doc.
12. **Limitações:** §9.
13. **Ficheiro de documentação:** este (`docs/project/waves/2026-09-29-w-first-e2e-test.md`).
14. **Estado Git final:** 1 novo ficheiro versionado + 1 wave doc untracked até ao commit.
15. **Commit:** a criar nesta wave.
16. **Sem push:** confirmado.

## 16. Próximas waves

A sequência recomendada a partir daqui (extraída da auditoria `2026-09-25-w-audit-web-admin-e2e.md` §16):

1. **First Real Test (manual)** — executar §13 em ambiente real com Telegram/Dispatcharr. **Resultado do primeiro intento:** o pipeline falhou com `WTException: phone_number`. A causa está documentada em §10.5 e corrigida na wave `W-FIX-WT-AUTH-PHONE-NUMBER`. Re-executar §13 após deploy do fix.
2. **W-MULTI-COUNTRY-UI** — country por Run na UI (P1.6).
3. **W-REVIEW-EXPAND** — fechar W5.5 gap (P1.7).
4. **W-DISPATCHARR-CURSOR** — `lastDispatcharrSuccessAtUtc` (P2.3, DL-130 D-OPEN-05).
5. **W-FIRST-E2E-TEST-DISPATCHARR** — wave futura para acrescentar variante deste teste com Dispatcharr activo (HttpMessageHandler fake), exercitando o caminho de plan/apply sem Dispatcharr externo. Estimativa: 1 dia.

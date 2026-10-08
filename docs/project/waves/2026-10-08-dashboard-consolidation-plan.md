# Consolidação do Dashboard — auditoria e plano de waves (2026-10-08)

> **Documento de planeamento (docs-only).** Regista a auditoria do Dashboard (navegação/IA, cobertura
> backend↔UI, robustez do front-end, sanitização e testes), a **decisão registada DC-D1 (Opção A)** e o
> plano de waves `DC-1…DC-12`. Subordinado à BÍBLIA (`docs/Reestructure/00-BIBLE.md`) e a `AGENTS.md`;
> não redefine conceitos normativos nem implementa nada.
>
> **Estado:** implementado — waves `DC-1…DC-12` executadas em 2026-10-08 (ver `CHANGELOG.md` [Unreleased] e `docs/project/waves/README.md`). `HEAD` no momento do recon: `bca34ea1fab874ea76ffb0a412a31d0e035ddbb7`.
> **Cross-references:** `docs/project/waves/2026-10-04-runtime-audit-dashboard-recovery-plan.md`
> (`W1–W6` concluídas; `W7` parcial), `docs/architecture/run-observability-and-manual-trigger.md`,
> `docs/PROJECT_STATUS.md`, `docs/project/waves/README.md`, `m3uCrawler/README.md`.

**Legenda de classificação:** **CONFIRMADO** (observado no código e/ou runtime), **DECISÃO** (já decidido),
**PENDENTE** (trabalho futuro; não implementado).

## 1. Método e âmbito

Auditoria **read-only** em quatro frentes, todas sobre o `HEAD` indicado:

- **Frente A — estado/planos existentes:** `docs/project/waves/2026-10-04-runtime-audit-dashboard-recovery-plan.md`,
  `docs/project/waves/2026-09-25-w-audit-web-admin-e2e.md`, `docs/project/waves/2026-09-29-w-pre-first-e2e.md`,
  `docs/project/waves/2026-09-29-w-first-e2e-test.md`, `docs/architecture/run-observability-and-manual-trigger.md`,
  `docs/PROJECT_STATUS.md`, `CHANGELOG.md`.
- **Frente B — navegação e vistas:** inventário de nav/views/sub-tabs em `m3uCrawler/Services/WebDashboardService.cs`.
- **Frente C — rotas backend vs uso na UI:** inventário de rotas HTTP e chamadas `fetch`/`safeFetchJson` no JS embutido.
- **Frente D — robustez/testes/sanitização:** handlers, tratamento de erros, DTOs de saída, cobertura de testes.

**Âmbito:** consolidação do Dashboard (HTML/JS embutido + os contratos HTTP que o servem). **Fora de âmbito:**
validação runtime/deployment (`W7`), decisões de produto pendentes e o pipeline de discovery (ver §6).

Nota estrutural: todo o Dashboard é uma única `string` C# em `BuildDashboardHtml()` (`WebDashboardService.cs:5352-10109`),
com o `<script>` em IIFE (`6490-10105`). **Todas as waves DC-1…DC-7 e a parte de UI de DC-9 editam o mesmo ficheiro** (DC-9 toca ainda ficheiros de backend) e, por isso,
não são paralelizáveis entre si.

## 2. Decisão registada — DC-D1 (Opção A: escolha manual de modo no Live Run)

**DECISÃO.** Manter a vista `Live Run` e expor, no botão "Run now", a escolha de modo
`telegram` / `telegram-maintain` (com overrides opcionais `keyword`/`historyHours`/`maxStreams`).
Rejeitada a hipótese de eliminar a vista e mover a lógica para Catálogo → Scheduled Jobs.

Justificação **CONFIRMADO**:

1. É o desenho original que ficou por implementar: `docs/architecture/run-observability-and-manual-trigger.md`
   §7.1 (linhas 705-708) especifica "Run now" com dropdown `Mode` (Telegram single/maintenance) e inputs
   opcionais. A UI actual só envia `{}` (`WebDashboardService.cs:9931-9939`).
2. O backend já suporta: `LiveRunStartPayload` (`LiveRunApiContracts.cs:25-31`), `ParseStartPayload`
   (`LiveRunApiContracts.cs:294-318`), `LiveRunMode` (`Services/LiveRun/LiveRunTypes.cs:27-34`) e
   `TelegramLiveRunExecutor` (`Services/LiveRun/TelegramLiveRunExecutor.cs:96`).
3. O Live Run é a observabilidade do `RunCoordinator` e cobre as **três** fontes (`LiveRunSource`:
   cli/scheduler/manual, `LiveRunTypes.cs:11-21`); os Scheduled Jobs são apenas **uma** dessas fontes.
4. A alternativa exigiria um caminho não-bloqueante novo: a acção agendada usa `StartAsync` **bloqueante**
   (`Services/Automation/ScheduledTelegramRunAction.cs:113`) enquanto o trigger manual usa `KickStartAsync`
   (`WebDashboardService.cs:11198`); e sobreporia configuração (scheduler) a observabilidade (run model).
5. Menor risco: alteração de front-end apenas; o contrato backend não muda.

Implementação em **DC-1**.

### DC-D2 — Configuração de discovery por Scheduled Job (overrides sobre defaults globais)

**DECISÃO.** Cada `ScheduledJobEntity` pode definir os seus próprios parâmetros de discovery
(`Keyword`, `MinHistoryHours`, `HistoryHours`, `MaxStreams`), aplicados como **overrides sobre os
defaults globais** resolvidos por `DiscoverySettingsProvider`. A configuração global
(`runtime-data/app_settings.json`) mantém-se como **base/fallback** para CLI, trigger manual sem
overrides e jobs sem overrides. **Rejeitada** a eliminação da configuração global.

Justificação **CONFIRMADO**:

1. Necessidade operacional concreta: um job noturno (janela larga) e um job horário (só a última hora),
   ambos em `telegram-maintain`, são hoje **inexprimíveis** — `ScheduledTelegramRunAction` lê sempre a
   config global (`Services/Automation/ScheduledTelegramRunAction.cs:94-105`) e `ScheduledJobEntity`
   não tem parâmetros (`Services/Catalog/CatalogEntities.cs:693-713`).
2. Encaixa no mecanismo existente `IJobAwareScheduledAction`/`ScheduledJobContext`
   (`Services/Automation/ScheduledJobRunner.cs:39-48`) e na precedência `WithOverrides`
   (`Services/Configuration/DiscoverySettings.cs:157-184`); **não** cria um segundo pipeline
   (respeita DL-016 e o invariante `AGENTS.md` "não duplicar pipelines").
3. A config global continua necessária como default (CLI `--telegram`/`--telegram-maintain`, manual sem
   overrides, fallback); logo a secção de configuração do menu Descoberta **permanece configurável** —
   passa a "Configuração de discovery predefinida" — e **não** se torna apenas informativa.

Precedência: `efectivo = defaults_globais.WithOverrides(overrides_do_run_ou_do_job)`.

Implementação em **DC-9**.

### DC-D3 — Dispatcharr segue a Ordering List (membros e ordem)

**DECISÃO.** O sync do Dispatcharr passa a usar uma **Ordering List** como fonte autoritativa de
(a) **membros** (que canais são criados) e (b) **ordem** (os canais são criados na ordem da lista).
Rejeita-se o comportamento actual em que o sync lê `output/playlist.m3u` e re-deriva a ordem por
`(tier, identidade)` ignorando as Ordering Lists.

**Clarificação (2026-10-08):** a ordenação é **dos canais**, não dos grupos. O **agrupamento no
Dispatcharr não é determinado pela Ordering List** — mantém-se o grupo do canal canónico
(`CanonicalChannel.Group.DisplayName`, invariante `AGENTS.md`).

Justificação **CONFIRMADO**:

1. Hoje o sync ignora as Ordering Lists e lê `output/playlist.m3u`
   (`Services/Sync/DispatcharrSyncService.cs:140`); não há qualquer referência a `OrderingList` no path de sync (D-5).
2. O sync do run 7 criou 24 canais fora da ordem da `pt-principal` (D-7).
3. `ProposedChannelNumber` existe mas nunca é atribuído e o Dispatcharr não recebe posição
   (`Models/MatchPlan.cs:105`; `Services/Sync/DispatcharrSyncService.cs:919-924`).

Implementação em **DC-11**. Pontos de desenho a fixar no plano: tratamento dos canais
`CrawlerManaged` que deixem de constar da lista, e a **selecção da lista por país** (uma Ordering
List por país — **DC-D4**).

### DC-D4 — Uma Ordering List por país

**DECISÃO.** É permitida no máximo **uma Ordering List por país**. A selecção da lista pelo sync é
feita por país; múltiplas listas para o mesmo país tornariam a selecção ambígua e são rejeitadas.

Justificação **CONFIRMADO**:

1. `OrderingListEntity` tem `Country` (`Services/Catalog/CatalogEntities.cs:1112-1130`) e a UI actual permite criar várias listas para o mesmo país.
2. Sem unicidade por país, não é determinístico escolher a lista que o Dispatcharr deve seguir.

Implementação em **DC-11** (índice único EF sobre `Country` + validação na API/UI).

## 3. Estado actual (evidência)

### 3.1 IA / navegação
- 11 botões de nav ↔ 11 `view-*` (`WebDashboardService.cs:5446-5458`); Catálogo tem **16** sub-tabs (`5587-5605`).
- Sem breadcrumbs nem hash routing: `showView(name)` (`9598-9619`) não reflecte a vista no URL.
- Mistura PT/EN nos rótulos (ex.: `Overview`, `Stream Validation`, `Live Run` vs `Execuções`, `Descoberta`).
- `setupConfigure` mapeia `countryData` para Catálogo em vez de `view-countries` (`9375`).
- **Cross-view DOM write**: `loadPlaylist` escreve `#diagInventory` (`6908`), nó que pertence à vista Diagnóstico (`6348`).
- **Duplicação**: a subsecção "Execução agendada (Telegram)" do Live Run (`6373-6380`; loader `loadLiveRunScheduled` `9909`)
  lê o mesmo `GET /api/catalog/scheduled-jobs` que Catálogo → Scheduled Jobs (`8602`).

### 3.2 Cobertura backend ↔ UI
- **UI-absent (rota existe, UI não usa):**
  - `GET /api/audit` (`751`, handler `10409`) — não há visualizador de auditoria.
  - `GET /api/classification-summary` (`1025`) — não documentada em README, inalcançável.
  - `GET/POST /api/catalog/channel-sources/{id}/observations` (`2929`/`2953`) — histórico de observações sem UI.
  - Nova Review API `GET /api/reviews` + `POST /api/review/{resolve|ignore|reopen}` (`3840-3888`; paginação `3908-3951`)
    — a UI usa as rotas **legacy** `7707/9263/9284`.
  - `GET /api/playlist_temp/preview` (`946`) — o inventário lista `playlist_temp.m3u`, mas não há preview.
- **Parcial (API expõe, form não expõe):** Dispatcharr `matchThreshold`, `targetGroupName`, `providerPriority`,
  `aliasFile`, `autoCreateGroups`, username/password — API lê/escreve (`11507-11517`, `11893-11904`), form omite
  (`6431-6452`, `9528-9534`); e `mode` no `POST /api/run/start` (ver DC-1).
- **Assimetria de segurança:** a rota legacy de Reviews devolve `note`/`reasonSignature` sem sanitização
  (`1609-1611`); a nova API sanitiza (`4284-4285`).

### 3.3 Robustez do front-end
- **~40** handlers de mutação com `fetch` sem `try/catch` → rejeições não tratadas e falhas silenciosas
  (ex.: `6844`, `6864`, `7355`, `7371`, `7456`, `7504`, `7651`, `7671`, `7774`, `7912`, `7943`, `8174`,
  `8259`, `8430`, `8503`, `8677`, `8822`, `8958`, `9263`, `9484`, `9575`).
- `await r.json()` em ramos de erro pode lançar em corpos vazios/não-JSON; só os handlers de Review (W5) guardam.
- `catch (e) {}` silencioso em `loadPlaylist` (`6909`) e `loadDiagnostics` (`7062`).
- Superfícies de erro inconsistentes (`alert()` vs status inline vs `{error}`); não há helper comum — só o
  `api()` do bootstrap (`11398`).
- Debug residual: `console.log('[DEBUG] …')` (`7090`, `7092`, `7094`, `7122`, `7127`, `7131`, `7136`, `9599`, `9604`).
- `prompt()` remanescente em `toggleChannelPolicy` (`7404`) e `duplicateOrderingList` (`8018`).

### 3.4 Sanitização no boundary de saída (invariante `AGENTS.md:55-56`)
- Confirmado sanitizado: `/api/playlist/preview` e `/api/playlist_temp/preview` (`927`/`956`), source-selection
  preview (campo `streamUrlSanitized`, `5296`/`5306`), `RunReport`, `/api/classification-summary` (`1041-1050`).
- **Defense-in-depth em falta:** DTOs ecoam `streamUrl` confiando apenas na sanitização em persistência —
  `ChannelSourceToJson` (`4899`), `PlaylistCompositionToJson` (`5336`), pending-country-approvals (`1931`).
  `dispatcharr baseUrl` é devolvido sem sanitização (`11893`/`9521`).

### 3.5 Contrato HTTP/JSON
- `WriteMethodNotAllowedAsync` fixa `Allow: GET, POST` mesmo em rotas POST-only (`11925`).
- Verbos errados caem em **404** em vez de 405 (`/api/run/start` `1170`, `/api/country/save` `873`,
  `/api/validation/test` `3635`); `/api/country` e `/api/playlist*` aceitam verbos indevidos (`821/840`, `900-959`).
- `HandlePublicationStatusEndpointAsync` usa propriedades PascalCase (`11260-11262`), contrariando o camelCase geral.
- Drift de nomes no recurso Review entre API nova (`4266-4290`) e legacy (`1603-1616`).

### 3.6 Testes
- W1 (regressão IIFE) totalmente remediado: 0 handlers mortos, guardado por `DashboardInlineHandlerScopeTests`.
- **Sem harness de execução JS.** O fluxo "Run now" (`9931-9963`) só é testado por presença de string
  (`Phase94LiveRunApiTests.cs:608-611`); sem teste de sanitização dos 3 DTOs; sem testes de acessibilidade;
  sem guarda contra `[DEBUG]`.

## 4. Plano de waves

| Wave | Título | Tipo | Ficheiro principal | Depende de |
|---|---|---|---|---|
| DC-1 | Live Run: seletor de modo no "Run now" (Opção A) | Front-end + docs | `WebDashboardService.cs` | — |
| DC-2 | Resiliência do front-end (helper de fetch + erros) | Front-end | `WebDashboardService.cs` | — |
| DC-3 | Sanitização no boundary de saída (defense-in-depth) | Backend/segurança | `WebDashboardService.cs` | — |
| DC-4 | IA: navegação, de-duplicação e routing | Front-end | `WebDashboardService.cs` | DC-1, DC-2 |
| DC-5 | Cobertura UI de API existente | Front-end | `WebDashboardService.cs` | DC-2 |
| DC-6 | Higiene de contrato HTTP/JSON | Backend | `WebDashboardService.cs` | — |
| DC-7 | Acessibilidade, UX e limpeza | Front-end | `WebDashboardService.cs` | DC-4 |
| DC-8 | Infra de testes do Dashboard (harness JS) | Testes (enabler) | `m3uCrawler.Tests/` | — (paralelizável) |
| DC-9 | Configuração de discovery por Scheduled Job | Backend + UI + migração | `CatalogEntities.cs`, `WebDashboardService.cs` | DC-1 |
| DC-10 | Correcção do `buildDate`/label OCI no workflow de publicação | Workflow/CI | `.github/workflows/docker-ghcr.yml` | — (paralelizável) |
| DC-11 | Dispatcharr segue a Ordering List (membros e ordem) | Backend + sync + UI | `DispatcharrSyncService.cs`, `ChannelMatcher.cs` | — |
| DC-12 | Ordering: "mover para posição N" (inserir em posição arbitrária) | Front-end | `WebDashboardService.cs` | — |

### DC-1 — Live Run: seletor de modo no "Run now" (Opção A)
- **Objectivo:** implementar DC-D1.
- **Âmbito:** adicionar controlo de modo (`telegram`/`telegram-maintain`) e overrides opcionais
  (`keyword`, `historyHours`, `maxStreams`) à vista Live Run (`WebDashboardService.cs:6356-6381`);
  `startLiveRun()` (`9931-9939`) passa a enviar `{ mode, keyword?, historyHours?, maxStreams? }`;
  preservar o tratamento 503 (sem `--web-allow-trigger`) e 409 (run já em curso). Sem alterar o contrato backend.
- **Fora de âmbito:** cancelamento, ETA, `StartAtUtc` (explicitamente out-of-scope da 9C.4).
- **Ficheiros:** `m3uCrawler/Services/WebDashboardService.cs` (HTML+JS); docs
  `docs/architecture/run-observability-and-manual-trigger.md` §7.1 e `m3uCrawler/README.md`.
- **Testes:** novo teste HTML (presença do selector e de `mode` no corpo do POST); extensão de `Phase94LiveRunApiTests`.
- **Critérios de aceitação:** a UI permite escolher ambos os modos e envia-os; default `telegram`;
  `dotnet build`/`dotnet test` em Release 0 warnings/0 errors.
- **Riscos:** baixo.

### DC-2 — Resiliência do front-end (helper de fetch + erros)
- **Objectivo:** eliminar rejeições não tratadas e falhas silenciosas nos handlers de mutação.
- **Âmbito:** introduzir um helper único de fetch/erro no script do Dashboard (reutilizável; espelhar o `api()`
  do bootstrap `11398` e `readErrorBody` `8699`); encaminhar os ~40 handlers listados em §3.3; corrigir
  `await r.json()` em ramos de erro; remover os `catch (e) {}` silenciosos (`6909`, `7062`).
- **Ficheiros:** `m3uCrawler/Services/WebDashboardService.cs`.
- **Testes:** teste HTML/string para a presença do helper e ausência de `fetch` cru nos handlers migrados;
  (idealmente) teste de comportamento via DC-8.
- **Critérios de aceitação:** nenhum handler de mutação sem tratamento de erro; superfície de erro consistente;
  build/test Release 0/0.
- **Riscos:** médio (muitos call-sites); mitigar migrando por blocos funcionais.

### DC-3 — Sanitização no boundary de saída (defense-in-depth)
- **Objectivo:** garantir o invariante `AGENTS.md:55-56` mesmo para linhas legacy/direct-DB.
- **Âmbito:** aplicar `CredentialSanitizer.SanitizeUrl` na projecção de `ChannelSourceToJson` (`4899`),
  `PlaylistCompositionToJson` (`5336`) e pending-country-approvals (`1931`); sanitizar `dispatcharr baseUrl`
  na saída (`11893`/`9521`).
- **Ficheiros:** `m3uCrawler/Services/WebDashboardService.cs` (e eventualmente `CredentialSanitizer` reutilizado).
- **Testes:** testes unitários/HTTP que injetam uma URL com credenciais e verificam a máscara na resposta.
- **Critérios de aceitação:** nenhum endpoint de preview/DTO devolve URL Xtream com credenciais; build/test 0/0.
- **Riscos:** baixo (isolada, segurança).

### DC-4 — IA: navegação, de-duplicação e routing
- **Objectivo:** reduzir duplicação e corrigir incoerências de navegação.
- **Âmbito:** substituir a subsecção de agendamentos inline do Live Run (`6373-6380`) por referência/deep-link
  a Catálogo → Scheduled Jobs; corrigir o cross-view DOM write `loadPlaylist`→`#diagInventory` (`6908`);
  corrigir o destino de `countryData` (`9375`) para `view-countries`; agrupar/rótulos consistentes na nav (PT).
- **Stretch (deferível):** hash routing para deep-link das vistas (`showView` `9598`).
- **Ficheiros:** `m3uCrawler/Services/WebDashboardService.cs`.
- **Testes:** `WebDashboardHtmlTests`, `WebDashboardHtmlAuditTests` (ids duplicados, hooks).
- **Critérios de aceitação:** sem duplicação de listagem de jobs; `#diagInventory` escrito só por Diagnóstico;
  `countryData` abre `view-countries`.
- **Riscos:** baixo/médio.

### DC-5 — Cobertura UI de API existente
- **Objectivo:** aproximar a UI das capacidades já servidas pelo backend (§3.2).
- **Âmbito (por prioridade):**
  1. Review: migrar a UI para a nova API (`resolve`/`ignore`/`reopen`, paginação) **ou**, no mínimo, sanitizar
     a rota legacy (`1609-1611`).
  2. Visualizador de auditoria (`GET /api/audit`, `751`/`10409`).
  3. Histórico de observações de channel-source (`2929`/`2953`).
  4. `classification-summary` (`1025`).
  5. Preview de `playlist_temp.m3u` (`946`).
  6. Campos avançados de Dispatcharr no Setup (`6431-6452`, `9528-9534`).
- **Ficheiros:** `m3uCrawler/Services/WebDashboardService.cs`.
- **Testes:** HTML tests por vista + HTTP tests dos endpoints consumidos.
- **Critérios de aceitação:** cada item tem superfície de UI e teste; nada de credenciais expostas.
- **Riscos:** médio; dividir em sub-ondas se necessário.

### DC-6 — Higiene de contrato HTTP/JSON
- **Objectivo:** consistência de status/métodos/payloads.
- **Âmbito:** `Allow` correcto e corpo JSON nos 405 (`11925`); regressar 405 (não 404) para verbos errados em
  `/api/run/start` (`1170`), `/api/country/save` (`873`), `/api/validation/test` (`3635`); guardar métodos em
  `/api/country` (`821/840`) e `/api/playlist*` (`900-959`); trocar propriedades PascalCase por camelCase em
  `HandlePublicationStatusEndpointAsync` (`11260-11262`); alinhar nomes de campos do recurso Review (`4266-4290` vs `1603-1616`).
- **Ficheiros:** `m3uCrawler/Services/WebDashboardService.cs`.
- **Testes:** suites de endpoints existentes (`WebDashboardServiceTests`, `ConfigurationLifecycleEndpointTests`, etc.).
- **Critérios de aceitação:** contrato de erro uniforme; build/test 0/0.
- **Riscos:** médio (alterar status pode afetar clientes/testes existentes — verificar cada caso).

### DC-7 — Acessibilidade, UX e limpeza
- **Objectivo:** elevar acessibilidade e remover ruído.
- **Âmbito:** `<label for>`/`aria-label` nos inputs que só têm placeholder; `aria-live` nas regiões de estado
  (`5500`, `6359`, `6482`, `7668`, `8710`); `aria-current` na nav (`5446-5458`); focus trap em `openModalPanel`
  (`9988-10013`); substituir `prompt()` remanescente (`7404`, `8018`) pelo padrão modal (W5); remover os
  `console.log('[DEBUG] …')` (`7090`…`9604`).
- **Ficheiros:** `m3uCrawler/Services/WebDashboardService.cs`.
- **Testes:** asserções HTML de labels/aria e teste que proíbe `[DEBUG]` no HTML servido.
- **Critérios de aceitação:** inputs com rótulo acessível; regiões anunciáveis; sem `prompt()` nem `[DEBUG]`.
- **Riscos:** baixo.

### DC-8 — Infra de testes do Dashboard (harness JS) [enabler]
- **Objectivo:** permitir testar comportamento JS (hoje só há testes de markup/string).
- **Âmbito:** harness de smoke que serve o HTML e exercita `startLiveRun`, criação/edição de scheduled job,
  abertura/fecho de modal e um fetch em falha; regressões do helper de DC-2 e da sanitização de DC-3.
- **Ficheiros:** `m3uCrawler.Tests/` (+ eventual pasta de harness). **Não** edita `WebDashboardService.cs` → paralelizável.
- **Critérios de aceitação:** harness executa em CI com determinismo; cobre G2/G3/G4/G6 de §3.6.
- **Riscos:** médio (introduzir dependência de teste exige aprovação explícita — `AGENTS.md §4`).

### DC-9 — Configuração de discovery por Scheduled Job
- **Objectivo:** permitir overrides de discovery por job, resolvidos como `defaults.WithOverrides(jobOverrides)`.
- **Âmbito:**
  1. `ScheduledJobEntity` (`Services/Catalog/CatalogEntities.cs:693-713`): novo campo de parâmetros
     (`ParametersJson` ou tipado) + **migração EF** na BD de catálogo.
  2. `ScheduledJobContext` (`Services/Automation/ScheduledJobRunner.cs:39`): transportar os parâmetros ao
     action (ou o action carregar o job por `Id`).
  3. `ScheduledTelegramRunAction` (`Services/Automation/ScheduledTelegramRunAction.cs:94-105`): implementar
     `IJobAwareScheduledAction`; ler os overrides do job e aplicá-los; fallback para a config global.
  4. `LiveRunRequest` (`Services/LiveRun/LiveRunTypes.cs:107-114`) e `DiscoverySettingsProvider.Resolve`
     (`Services/Configuration/DiscoverySettingsProvider.cs:30`): acrescentar `MinHistoryHours`.
  5. Dashboard: form de Scheduled Jobs (`WebDashboardService.cs:6134-6172`), `ScheduledJobPayload`
     (`4861-4867`) e endpoint CRUD (`2645`) com campos de discovery por job, mostrando o default herdado.
  6. Relabel da secção global para "Configuração de discovery predefinida" na vista Descoberta (`5486-5506`).
- **Fora de âmbito:** `country` por job (hoje CLI-only); perfis de discovery nomeados (alternativa caso
  muitos jobs partilhem a mesma config).
- **Ficheiros:** `Services/Catalog/CatalogEntities.cs` (+ migração), `Services/Automation/*`,
  `Services/LiveRun/*`, `Services/Configuration/*`, `Services/WebDashboardService.cs`, `Program.cs` (DI);
  testes em `m3uCrawler.Tests/`.
- **Testes:** precedência (job override > global; herança quando ausente); validação server-side
  (`DiscoverySettings.TryValidate`); acção job-aware; CRUD do endpoint; form HTML; migração.
- **Critérios de aceitação:** dois jobs `telegram-maintain` com janelas distintas coexistem e usam cada um a
  sua; sem override, herdam a global; build/test Release 0/0.
- **Dependências:** DC-1 (mesma superfície de overrides).
- **Riscos:** médio — migração EF na BD de catálogo; `telegram-maintain` horário re-testa o `playlist.m3u`
  completo (carga); scheduler sequencial → `blocked:already-running` em sobreposição; a config efectiva por
  run não é hoje registada no `RunReport` (considerar observabilidade).

### DC-10 — Correcção do `buildDate`/label OCI no workflow de publicação
- **Objectivo:** `/api/version` e os labels OCI passarem a reportar a **data real do build**, não metadados do repositório.
- **Âmbito:** em `.github/workflows/docker-ghcr.yml`, substituir `github.event.repository.updated_at` (linha 93, label `org.opencontainers.image.created`; linha 122, build-arg `M3uCrawlerBuildDate`) por um timestamp do build (ex.: `${{ github.run_started_at }}` ou `date -u +%Y-%m-%dT%H:%M:%SZ`). O valor entra no `InformationalVersion` (`Directory.Build.props:37`, `Directory.Build.targets:50`) e no `/api/version` via `BuildInfo`.
- **Ficheiros:** `.github/workflows/docker-ghcr.yml` (ficheiro sensível, `AGENTS.md §3`); documentação se descrever o campo.
- **Testes:** `DockerBuildInfoContractTests` (contrato `/api/version` ↔ labels OCI).
- **Critérios de aceitação:** após novo build, `buildDate` do `/api/version` coincide com a hora do build e com o label `created`; sem regressão no contrato.
- **Dependências:** nenhuma. **Não** edita `WebDashboardService.cs` → paralelizável.
- **Riscos:** baixo/médio — ficheiro de publicação; a validação exige um novo build.

### DC-11 — Dispatcharr segue a Ordering List (membros e ordem)
- **Decisões base:** DC-D3, DC-D4.
- **Objectivo:** tornar a Ordering List a fonte autoritativa do plano de Dispatcharr para **membros** e **ordem** (não para agrupamento).
- **Âmbito (a fixar em plano próprio antes de implementar — `AGENTS.md §1`):**
  1. **Input:** compor o plano a partir da Ordering List (via `PlaylistComposerService.ComposeAsync` e/ou o bridge existente `ChannelMatcher.BuildPlanFromCompositionAsync`, `Services/Matching/ChannelMatcher.cs:131-160`, hoje sem caller em produção), em vez de ler `output/playlist.m3u` cru.
  2. **Membros:** criar/actualizar apenas canais `IsEnabled` da lista **com ≥1 fonte elegível** (o compositor omite canais sem fonte, `Services/Catalog/PlaylistComposerService.cs:116-145`); decidir o destino dos canais `CrawlerManaged` existentes que já não constem da lista (manter vs remover).
  3. **Ordem:** os canais são criados no Dispatcharr **pela ordem da lista** (`Position`) — atribuir `ProposedChannelNumber`/`ChannelNumber` (`Models/MatchPlan.cs:105`; `Services/Sync/DispatcharrSyncService.cs:919-924`) e confirmar o campo que o Dispatcharr usa para ordenar canais.
  4. **Agrupamento:** **não** é definido pela lista — mantém-se o grupo do canal canónico (`CanonicalChannel.Group.DisplayName`; `OrderingListEntity`/`OrderingItemEntity` em `Services/Catalog/CatalogEntities.cs:1112-1153` não têm grupo).
  5. **Selecção da lista (DC-D4):** escolha por país (uma lista por país); impor **unicidade por país** (índice único EF sobre `Country` + validação na API/UI).
- **Ficheiros prováveis:** `Services/Sync/DispatcharrSyncService.cs`, `Services/Sync/DispatcharrSyncCoordinator.cs`, `Services/Matching/ChannelMatcher.cs`, `Services/Catalog/PlaylistComposerService.cs`, `Services/Catalog/CatalogEntities.cs` (+ migração de unicidade por país), `Services/LiveRun/RunPublicationService.cs`, `Models/DispatcharrConfig.cs`, `Services/WebDashboardService.cs` (config/UI), `Program.cs`.
- **Testes:** plano a partir de composição; ordem (números de canal = `Position`); membros (incl./excl.); unicidade por país; ownership/remoção de streams; regressão do caminho legado.
- **Dependências:** nenhuma técnica directa; relaciona-se com DC-9 se o binding for por job. Requer **plano de implementação próprio** e aprovação.
- **Riscos:** alto — altera o contrato do sync (input, ownership/remoção, modelo "playlist única"), acrescenta uma restrição de unicidade (migração) e altera o comportamento observado (D-7).

### DC-12 — Ordering: "mover para posição N"
- **Origem:** pedido de utilizador (2026-10-08). Hoje, reordenar exige clicar ↑/↓ uma vez por posição (numa lista de 200 canais, colocar um novo canal na posição 10 exigiria ~190 cliques).
- **Achado — o backend já suporta:** `PUT /api/catalog/ordering-items/{id}` com `{ position: N }` chama `CatalogResolver.MoveOrderingItemAsync`, que reindexa os irmãos e insere em `N` (0-based; valida `0 <= N <= count`) — `Services/Catalog/CatalogResolver.cs:3657-3709`. A UI usa já este endpoint, mas apenas com `position ± 1` (`Services/WebDashboardService.cs:7977-7978,8048-8056`). Adicionar directamente numa posição também já é suportado (`OrderingItemAddPayload.Position`, `Services/WebDashboardService.cs:4942-4947`; `AddOrderingItemAsync`, `Services/Catalog/CatalogResolver.cs:3586-3630`), mas o fluxo "+ Adicionar" (`Services/WebDashboardService.cs:8036-8046`) não envia `position`.
- **Âmbito (front-end, reutilizando o endpoint existente):**
  1. Input "Mover para" (nº 1-based, 1..total) + botão na linha do canal → `moveOrderingItem(itemId, n-1)` (conversão para 0-based) ou `PUT` directo com `{ position }`.
  2. (Opcional) campo "posição" no fluxo "+ Adicionar", enviando `position` no `POST /items`.
  3. Validação no cliente (1..total) e feedback de erro; usar input inline (não `prompt()`, alinhado com DC-7).
- **Ficheiros:** `Services/WebDashboardService.cs` (HTML+JS). **Sem alterações de backend.**
- **Testes:** teste HTML/string (presença do controlo "Mover para"); idealmente teste comportamental via DC-8.
- **Critérios de aceitação:** indicar a posição N e confirmar coloca o canal em N (reindexação garantida pelo backend); entrada inválida é rejeitada com mensagem; build/test Release 0/0.
- **Dependências:** nenhuma. **Riscos:** baixo (front-end).

## 5. Sequenciação e dependências
- DC-1…DC-7 editam o **mesmo** ficheiro (`WebDashboardService.cs`) → **executar sequencialmente**.
- DC-8 edita apenas testes → pode correr **em paralelo** com o trabalho de produção.
- **DC-9** depende de DC-1 e toca vários ficheiros (catálogo/migração, automation, live-run, configuração)
  além do `WebDashboardService.cs`: a parte de UI serializa com as restantes DC; as partes de
  backend/migração são independentes da UI.
- **DC-10** edita apenas o workflow de publicação (não toca `WebDashboardService.cs`) → pode correr **em paralelo** com as restantes DC.
- **DC-11** toca `WebDashboardService.cs` (UI de config) e ficheiros de sync/matching → a parte de UI serializa com as restantes DC; as partes de sync/matching são independentes da UI mas exigem plano próprio (risco alto).
- **DC-12** edita `WebDashboardService.cs` → serializa com as restantes DC que tocam esse ficheiro.
- **Ordem proposta:** DC-3 (segurança isolada) → DC-1 (decisão registada) → DC-8 (enabler de testes) →
  DC-2 → DC-6 → DC-4 → DC-5 → DC-7 → DC-9.
- Cada wave segue `AGENTS.md §1` (plano → aprovação → implementação → testes → doc/CHANGELOG quando aplicável).

## 6. Fora de escopo / decisões pendentes (não resolvidas por este plano)
- `W7` (validação runtime/deployment) e execução real controlada de `W2` — ver recovery plan 2026-10-04.
- `W6b-3`: `import-policies` inerte/oculta e `pending-country-approvals` pendente (`docs/PROJECT_STATUS.md:101,110`).
- `M.4`, `C7`, Review→Output, `POST /api/publication`/"Publicar" e `pendingReviewsCount` (DL-130).
- **Ordering → Dispatcharr (DECIDIDO — DC-D3/DC-D4):** o Dispatcharr segue a Ordering List para **membros e ordem** dos canais (a ordem é dos canais, não dos grupos; o agrupamento mantém-se o do canal canónico). É permitida **uma Ordering List por país**. Implementação em **DC-11**. Evidência D-5/D-7.

## 7. Reconciliação documental (follow-up docs-only)
- `docs/PROJECT_STATUS.md`: **F-22** ainda marcado "Pendente", mas fechado por `W-PRE-FIRST-E2E`
  (`docs/project/waves/2026-09-29-w-pre-first-e2e.md`); wording de `W7` divergente.
- `AGENTS.md:59`: a frase de que a UI não expõe Min/Max está desactualizada para **discovery** (exposto em
  W-DASHBOARD) e para o **Schedule Min/Max** (implementado na DC-9, 2026-10-08); **resolvido** — o invariante
  em `AGENTS.md` §2 foi corrigido para reflectir a exposição na UI (global, por job e no "Run now").
- Vários docs de recon citados (2026-09-24/25/29) estão **untracked**; citados como evidência, não como fonte commitada.

## 8. Achados de diagnóstico no servidor (2026-10-08)

Contexto: diagnóstico **read-only** na instância `192.168.68.142` (container `m3ucrawler`, imagem `ghcr.io/ginjeira/m3ucrawler:latest`, digest `sha256:56c0d6d11c2eb343644b26ed32abe2c6bf0fb640f928bd45ace87b4694b21dd5`, commit `d05e5d3`, created `2026-10-07T17:44:58Z`; bind mount `/opt/m3ucrawler/runtime-data`). Sem alterações ao servidor.

| # | Achado | Classificação | Evidência | Acção |
|---|---|---|---|---|
| D-1 | `buildDate` e label OCI `created` do `/api/version` enganadores | CONFIRMADO (defeito) | `/api/version` devolveu `buildDate=2026-09-16` para o commit `d05e5d3` (2026-10-07); causa em `docker-ghcr.yml:93,122` (`github.event.repository.updated_at`) | **DC-10** |
| D-2 | Job `TelegramSemanal` com cron **diário** | CONFIRMADO (dados) | `scheduled_jobs`: `cron='0 0 * * *'`, `ActionName=telegramMaintainRun`, `IsEnabled=1`, `LastRunAtUtc` vazio, `NextRunAtUtc=2026-10-09 00:00Z` | Renomear/ajustar na UI Scheduled Jobs (sem código) |
| D-3 | `app_settings.json` em caminho aninhado `runtime-data/runtime-data/` | CONFIRMADO (deploy) | Container com `WORKDIR=/data` (bind `/opt/m3ucrawler/runtime-data`) e resolução de `runtime-data/` relativa ao cwd | Alinhar caminho/volumes no deployment (observação) |
| D-4 | Vista "Descoberta" aparentava estar estagnada | CONFIRMADO (não é bug) | Relatório reescrito em 2026-10-08 10:48Z (`finishedAt=10:47:03Z`, `status=completed`); o run usou `window 500-700h` (`live_run_steps` + `audit_records` `24/0→500/0→700/500→72/0`); as 567 entradas vêm de 4 `messageId` com datas 2026-09-09…09-14 | Confirma que a vista é um **snapshot de 1 run**; reforça DC-1/DC-9 (janelas por run/job). Janela já reposta para `72/0` |
| D-5 | Ordering Lists não são consumidas pelo Dispatcharr | CONFIRMADO (arquitectura) | Zero referências a `OrderingList` em `Services/Dispatcharr`, `Services/Sync`, `Services/Matching`, `Services/LiveRun`; o sync lê `output/playlist.m3u` (`Services/Sync/DispatcharrSyncService.cs:140`); a composição vive em `PlaylistComposerService.ComposeAsync` → `WriteComposedAsync` (`Services/Catalog/PlaylistComposerService.cs:52-161`; `Services/Automation/ScheduledPlaylistGenerationAction.cs:69-88`); o bridge `ChannelMatcher.BuildPlanFromCompositionAsync` (`Services/Matching/ChannelMatcher.cs:131-160`) não tem caller em produção | Decisão em aberto (ver §6); para reflectir a `pt-principal` no Dispatcharr, correr `generatePlaylist:1` antes do sync |
| D-6 | `dispatcharr_dry_run=true` não trava o botão "Sync Dispatcharr" | CONFIRMADO (segurança/UX) | Config viva `dispatcharr_dry_run=true`, mas `sync_runs` run 7 (2026-10-08 11:10) e run 4 (2026-10-07 20:04) aplicaram (`ok`, 264 e 2633 crawler-managed); `POST /api/dispatcharr/sync` passa `forceDryRun:false` e sobrepõe a flag (`Services/WebDashboardService.cs:1129-1132,10776`; `Services/Sync/DispatcharrSyncCoordinator.cs:91-97,113-117,143`) | Decidir se o botão deve respeitar a flag ou exigir confirmação explícita |
| D-7 | Sync do run 7 criou apenas 24 canais | CONFIRMADO (dados) | Plano/report `dispatcharr_*_20261008_111013.json`: `read-plan 871→24`; `classification {Channel:180, Unknown:691}`; `newChannels=24`, `newStreams=240` (10 por canal = `MaxSourcesPerChannel` default); `plan.unknownReviewRequired=631` | Evidência de que o Dispatcharr só cria canais canónicos com `PublicationPolicy=CreateEligible` reconhecidos; os restantes ficam em review |

Nota de segurança: as credenciais usadas eram de desenvolvimento e serão rodadas; nenhuma foi persistida. A password do dashboard em `192.168.68.142` difere da indicada pelo proprietário (username `admin` confirmado; `401 invalid-credentials` deveu-se a password, não a username). Isto é uma observação operacional, não um defecto de código.

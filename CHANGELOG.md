# 📈 Changelog - m3uCrawler

Todas as mudanças notáveis neste projeto serão documentadas neste arquivo.

O formato é baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.0.0/),
e este projeto adere ao [Semantic Versioning](https://semver.org/lang/pt-BR/).

## [Unreleased]

### 📋 Decisões
- **PHASE 9C.4 — fecho (2026-09-17): decisões sobre upgrade de instalações existentes (bootstrap).**
  - **1. Instalação nova** (sem configuração persistente): arranca em `NOT_CONFIGURED`; Dashboard disponível em modo `Bootstrap`; primeiro acesso conduz ao First-Run / Setup Wizard; o wizard cria o primeiro administrador e valida o mínimo (L2) para atingir `READY`; **não** reconfigura elementos já correctamente configurados.
  - **2. Instalação legacy / upgrade** (com dados persistentes, sem administrador): detecta a evidência via `LegacyConfigurationEvidenceEvaluator`, adopta `READY` com `AdoptedFromLegacy=true`, preserva integralmente os dados existentes, entra em modo `Legacy` (token-only) e mantém o First-Run Wizard disponível para criar o primeiro administrador. Após a criação, transita automaticamente para `READY` operacional. **Não** é necessário recriar nem importar a configuração. O rótulo conceptual deste cenário é `BOOTSTRAP_REQUIRED` (`READY` ∧ sem admin); não é um novo valor da enumeração `ConfigurationLifecycleState`.
  - **3. Instalação já configurada** (administrador válido existente): fluxo normal de login em `UserAuth`. **Não** se apresenta o First-Run Wizard.
  - **4. Separação entre bootstrap e configuração**: o bootstrap do primeiro administrador é independente da configuração subjacente. O sistema preserva sempre a configuração existente e trata apenas a ausência de administrador como condição de bootstrap. Nenhum tooling sobre `admin_users` deve assumir reconfiguração.
  - **5. Upgrade em produção**: quando chegar o momento de fazer o upgrade da instalação real, o processo deve primeiro ser testado contra uma cópia do `runtime-data` da instalação de produção, conforme prática já descrita em `AGENTS.md` §9.
  - **Sem alterações de código nesta wave.** A extensão efectiva do gate `Bootstrap` para cobrir `READY` ∧ sem administrador activo (decisão 2) pertence a uma wave de implementação futura (PHASE 9C.5 ou posterior) e **não** é feita aqui. Esta é uma onda exclusivamente documental.
  - **Nota de implementação (PHASE 9C.5, 2026-09-17):** a decisão 2 foi implementada com uma revisão da sua forma: em vez de manter o modo `Legacy` (token-only) com o wizard activo, `READY` ∧ sem administrador activo resolve agora para `AuthMode.Bootstrap` (`BOOTSTRAP_REQUIRED`). O `Legacy` fica restrito ao contexto explicitamente standalone/testes. Ver a entrada da PHASE 9C.5 em §Adicionado.
  - **Documentação**: nova secção `# Upgrade de instalações existentes (decisão pós-9C.4)` em `docs/architecture/configuration-lifecycle.md`, usando exclusivamente terminologia já estabelecida no projecto (`ConfigurationLifecycleState`, `AdoptedFromLegacy`, `AuthMode.Legacy`, `L2`, `ConfigurationGate`, etc.).

### 🛡️ Correcções / Hardening
- **PHASE 13 — Wave 13-1 hardening (2026-09-17): determinismo absoluto e cobertura de testes.**
  - **R1 — determinismo em empates extremos:** `ChannelSourceSelector` passa a terminar a ordenação numa **ordem total** sobre todos os campos do candidato (Provider, URL ordinal, `SourceId`, `ExternalStreamId`, prioridade, qualidade, EPG, disponibilidade, response time, `IsWorking`), sem hashes, referências de objecto, relógio ou aleatoriedade. Elimina a dependência da ordem de entrada na deduplicação e na ordem dos rejeitados; a URL ordinal só distingue o que a URL normalizada não distingue.
  - **R2 — `not-selected`:** mantido por estabilidade de vocabulário, mas marcado explicitamente como **reservado/nunca emitido** (a Fase B esgota os casos possíveis); documentado no XML e na arquitectura, com teste que fixa que não é emitido.
  - **R3 — precedência de motivos:** documentada e fixada por testes de caracterização — `duplicate-url` → inelegibilidade → `limit-reached` → `provider-limit` → `fallback-disabled`.
  - **R4 — identidade de URL:** cobertura explícita para fragmento (equivalente), scheme `http` vs `https` (distinto), query (preservada) e userinfo (preservado).
  - **R5 — limites combinados:** teste de `MaxSourcesPerChannel < MaxSourcesPerProvider` (10 candidatos/1 provider, max=2, per=5 → 2 seleccionados, 8 `limit-reached`), confirmando o tecto absoluto do canal.
  - **R6 — determinismo:** testes comparam a assinatura completa (Selected/rank/motivo, Rejected/motivo) em múltiplas permutações (original, inversa, rotações, permutação fixa) e num dataset com empates deliberados.
  - **Testes:** `ChannelSourceSelectorTests` passa de 44 para 53; sem alterações ao pipeline de produção. Documentado em `docs/architecture/dispatcharr-source-selection.md`.

- **Wave 10-0 (2026-09-17): consolidação segura do caminho agendado Dispatcharr.**
  - **Falha corrigida:** `ScheduledDispatcharrSyncAction` construía `new ChannelMatcher(aliases)` e `DispatcharrSyncService` **sem** `CatalogResolver`, activando o modo legacy (todas as streams tratadas como `CrawlerManaged`). O ownership guard ficava inactivo no caminho agendado, podendo emitir `DELETE`/rename de streams `External`/`Unknown`. A acção passa a injectar o `CatalogResolver` (DI singleton em `ScheduledAutomationHost`) no `ChannelMatcher` e no `DispatcharrSyncService`, exactamente como o caminho principal `Program.cs`.
  - **Contrato da playlist explicitado:** existe uma única playlist funcional, `output/playlist.m3u` (constante `FunctionalPlaylistFileName`), consumida pelo sync; não é criada uma segunda playlist. Documentado em `docs/architecture/channel-catalog-and-ownership.md` §13.
  - **Preservado:** `dispatcharr-disabled`, `no-playlist`, dry-run/default, `SyncRun`/observabilidade e o gate de lifecycle `READY` do scheduler.
  - **Testes:** novo `ScheduledDispatcharrSyncActionTests` (6 testes, sem rede real): `External`/`Unknown`/sem-registo nunca geram `DELETE`; `CrawlerManaged` continua a poder ser removido; contraste do fallback legacy sem catalog; `dispatcharr-disabled` e `no-playlist` sem qualquer chamada HTTP.
  - **Nota factual:** `BuildPlanFromCompositionAsync` continua **sem call site de produção e sem testes**; anotado no roadmap e removida a afirmação inexacta sobre `DispatcharrCompositionTests.cs`. Nada da PHASE 13 (selecção/diversidade/limites) foi implementado.

- **PHASE 9C.5 — simplificação de scope e correcção F6 (2026-09-17).**
  - **F6 (diagnóstico):** a resposta `403 bootstrap-required` deixou de devolver `state="NOT_CONFIGURED"` fixo; passa a reportar o estado de lifecycle efectivo. `ResolveAuthModeAsync` deu lugar a `ResolveAuthDecisionAsync`, que devolve modo **e** estado numa única leitura, evitando divergência entre o modo decidido e o estado reportado.
  - **Scope explicitado:** o first-run da 9C destina-se a instalações novas/limpas. Migração/recuperação de configuração de administrador de versões anteriores **não** faz parte do ciclo de vida suportado. A invariante mantém-se: **qualquer** registo em `admin_users` (activo ou desactivado) fecha a criação do primeiro administrador (`HasAnyAsync`); `HasActiveAdminAsync` não é usado para autorizar um segundo admin. Não é adicionada qualquer recuperação/reactivação. `READY` + administrador desactivado é um estado não suportado, podendo exigir reinicialização do `runtime-data`.
  - **Testes:** 5 novos — 3 de caracterização do estado não suportado (`BootstrapServiceTests.Legacy_ready_with_disabled_admin_is_bootstrap_but_creation_closed`, `DashboardBootstrapEndpointTests.Ready_with_disabled_admin_is_bootstrap_locked_out`, `..._and_web_token_keeps_machine_access_only`) e 2 do diagnóstico F6 (`Bootstrap_required_reports_not_configured_for_fresh_install`, `Bootstrap_required_reports_ready_when_ready_without_admin`).
  - Documentação alinhada (`docs/architecture/configuration-lifecycle.md`, `m3uCrawler/README.md`).

- **PHASE 9C.4 — correcções pós-revisão (2026-09-17): wiring de recovery e isolamento do sweeper.**
  - **F-002 (recovery)**: `RunCoordinator.RecoverInterruptedRunsAsync()` é agora invocado no startup de produção, em ambos os caminhos (`--web --telegram` e `--telegram` standalone), imediatamente após o `RunCoordinator` ser configurado e **antes** de qualquer `StartAsync`. Runs interrompidos por crash anterior são marcados como `Failed` antes do próximo ciclo arrancar. A chamada é best-effort: uma falha não bloqueia o startup.
  - **F-001 (sweeper scoped)**: `TestTempDb.SweepKnownPatterns()` foi restringido a um directório dedicado (`%TEMP%\m3uCrawler.Tests.tmp\`). Já não opera destrutivamente sobre `%TEMP%` global e a heurística wildcard `*.db.lock` (`baseName.Contains("-") || baseName.Contains("_")`) foi removida. Ficheiros fora do namespace da suite são preservados. Testes 9C.4 (`Phase94*`) usam `TestTempDb.SuitePath(...)` para colocar artefactos dentro do namespace isolado.
  - **Testes adicionados**: `Phase94TestTempDbIsolationTests` (canaries fora + artefactos dentro + ficheiros estranhos preservados) e `Phase94StartupRecoveryIntegrationTests` (wiring + idempotência).

- **PHASE 9C.2 — correcção pós-revisão (2026-09-16): preservar credencial de máquina e CSRF do Dashboard.**
  - **B1 — `--web-token` volta a autorizar automação em `READY` + administrador.** O gate distinguia mal "sem token configurado" de "autorizado por token", pelo que o modo `UserAuth` exigia sessão humana mesmo com token válido (`401`). Agora a credencial de máquina válida autoriza o pedido sem sessão (e sem CSRF), mantendo-se distinta da autenticação humana e sem criar utilizador/sessão. Precedência documentada: token avaliado primeiro; token válido **ou** sessão humana autorizam; com `--web-token` configurado, o token continua a ser exigido a todos os pedidos.
  - **B2 — operações mutáveis do Dashboard autenticado passam a funcionar.** O servidor exigia `X-CSRF-Token` mas a UI não o enviava (todas as escritas devolviam `403`). A página autenticada recebe agora o token **apenas em memória JavaScript** (nunca em URL/query/`localStorage`/logs) e um helper de `fetch` de mesma origem adiciona automaticamente o header. A protecção CSRF do servidor mantém-se intacta.
  - **S1 — fail-closed em falha de inicialização do auth.** Se o `AuthService` não for inicializado, o modo deixa de cair para `Legacy` (autorização implícita); em `READY` exige autenticação (`401`) e fora de `READY` mantém-se em bootstrap. Diagnóstico (`/api/version`, `/api/configuration/lifecycle`) permanece acessível.
  - **Testes**: `ready + admin + valid web-token → autorizado`, `token inválido → recusado`, `sessão + token → determinístico`, `legacy + web-token`, `token não cria utilizador/sessão`, operação mutável real do Dashboard (criação de job agendado via cookie + CSRF, com verificação de persistência) e falha de wiring de auth (fail-closed).
  - Documentação alinhada (`docs/architecture/configuration-lifecycle.md`, `m3uCrawler/README.md`).

### ✨ Adicionado
- **PHASE 13 — Wave 13-4 (2026-09-18): política global de selecção de fontes persistida + Dashboard.**
  - **Persistência:** nova entidade `SourceSelectionPolicyEntity` → tabela `source_selection_policies` na BD do catálogo (`channel-catalog.db`), por migration **aditiva** `AddSourceSelectionPolicies` (índice único em `ScopeKey`, sem FK, identidade por `CanonicalChannelKey` — nunca `CanonicalChannelId`). Linha global (`ScopeKey="global"`, `CanonicalChannelKey=null`) criada lazily por `CatalogResolver.GetOrCreateGlobalSourceSelectionPolicyAsync`, com os defaults da 13-3 (`MaxSourcesPerChannel=10`, `PreferDistinctProviders=true`, `MaxSourcesPerProvider=null`, `AllowFallbackToSameProvider=true`). Tabela excluída do `LegacyConfigurationEvidenceEvaluator`, como `source_priority_policies`.
  - **Resolução:** novo `SourceSelectionPolicyResolver` resolve a política global efectiva; `SourceSelectionStage` mantém-se sem persistência e recebe a `SourceSelectionPolicy` explícita. Os dois pontos de publicação Telegram (`Program.cs:540-541`, `:1108-1109`) passam a resolver via resolver.
  - **Dashboard:** `GET/POST /api/catalog/source-selection-policies` + cartão *data-driven* na área Catálogo (apenas global), sob o gate de autenticação/CSRF existente.
  - **Contrato ratificado (alteração semântica deliberada):** `ChannelSourceSelector` aceita `MaxSourcesPerChannel >= 0`, com `0` **válido** (selecciona zero fontes) e negativos inválidos; `MaxSourcesPerProvider` mantém `null` = sem limite, com `0`/negativos inválidos.
  - **Fora de âmbito:** overrides por canal reservados à Wave 13-4b; sem auditoria de alterações administrativas. Documentado em `docs/architecture/phase-13-4-source-selection-policy.md` e `docs/architecture/dispatcharr-source-selection.md` §10.

- **PHASE 13 — Wave 13-3 (2026-09-17): selecção de fontes aplicada à publicação Telegram.**
  - Nova `SourceSelectionStage` (`m3uCrawler/Services/SourceSelection/`): junta as streams do pipeline (URL real, só em memória) aos `ChannelSource` do catálogo pela chave `CredentialSanitizer.SanitizeUrl(realUrl)` — a mesma chave de unicidade do catálogo — projecta `SelectionCandidate`, aplica `IChannelSourceSelector` por canal canónico e devolve a lista publicável. Catálogo apenas lido; não escreve ficheiros.
  - Projection: `Provider` = host normalizado (`Uri.Host`, lowercase, sem `www.`/ponto final/porta); `Availability` = runtime `IsWorking`; `LastResponseTimeMs` = runtime; `Quality`/`Epg` = `Unknown`; `SourcePriority` do catálogo (actualmente 0).
  - Semântica: `ChannelSource.IsEnabled=false` → excluída (`source-disabled`); 0 correspondências ou URL mapeada a >1 canal canónico → pass-through (não conta para limites); catálogo ausente/vazio/falha de leitura → **no-op** (pipeline resiliente).
  - Defaults em memória (sem persistência nem migration): `MaxSourcesPerChannel=10`, `MaxSourcesPerProvider=null`, `PreferDistinctProviders=true`, `AllowFallbackToSameProvider=true`.
  - Integração nos dois pontos de publicação Telegram (single-cycle e manutenção, após `MergeStreams`); a playlist recebe apenas seleccionadas + não correspondidas, com as URLs reais preservadas; `DispatcharrSyncService`, `MatchPlan` e ownership inalterados.
  - Diagnóstico agregado em `RunReport.SourceSelection` (contagens; nunca URLs/credenciais). Contrato congelado do `RunReport` actualizado (`+SourceSelection`, 55→56 propriedades).
  - **25 testes** novos em `SourceSelectionStageTests` (junção exacta incl. URL Xtream, projecção, matching/ambiguous/no-op, `source-disabled`, limites/diversidade/fallback/dedup, ordem de publicação, determinismo, segurança de credenciais). Documentado em `docs/architecture/dispatcharr-source-selection.md` §10.

- **PHASE 13 — Wave 13-1 (2026-09-17): política pura de selecção de ChannelSources.**
  - Nova abstracção isolada em `m3uCrawler/Services/SourceSelection/`: `SelectionCandidate`, `SourceSelectionPolicy`, `ProviderIdentity`, `IChannelSourceSelector`/`ChannelSourceSelector` e `SourceSelectionResult`. Função pura `Candidates + Policy → Result`, sem filesystem, base de dados, HTTP, Dispatcharr, scheduler, Telegram ou Dashboard.
  - Algoritmo em duas fases: **A** diversidade (um representante por fornecedor distinto quando `PreferDistinctProviders`) e **B** preenchimento (respeita `MaxSourcesPerProvider` e `AllowFallbackToSameProvider`). `MaxSourcesPerChannel` é o tecto absoluto; nenhum valor está hardcoded.
  - Deduplicação por URL normalizada (http/https, scheme/host em minúsculas, porta por omissão e fragmento removidos; path/query/userinfo preservados). Ranking determinístico por `Availability` → `SourcePriority` → `Quality` → `Epg` → `LastResponseTimeMs` → desempate estável (URL normalizada/SourceId/ExternalStreamId).
  - `ProviderIdentity.Unknown` colapsa todos os fornecedores não determináveis numa única identidade (desconhecidos não ganham diversidade artificial). `SourceSelectionResult` expõe `Selected` (com `Rank`/motivo) e `Rejected` (com motivo) — base para preview/auditoria futura.
  - **44 testes** em `ChannelSourceSelectorTests` (volume 100/1, 100/10, 100/100, <10, =10, >10; dedup; fornecedores; diversidade on/off; limites por fornecedor; fallback; edge cases; motivos; ranking; determinismo), incluindo o teste crítico **100 fontes → no máximo `MaxSourcesPerChannel` selecções**.
  - Integração (persistência, migrations, Dashboard/preview, composer, `MatchPlan`, `DispatcharrSyncService`) **não** incluída nesta wave. Documentado em `docs/architecture/dispatcharr-source-selection.md`.

- **PHASE 9C.5 (2026-09-17): first-run / legacy bootstrap — `READY` sem administrador passa a `Bootstrap`.**
  - **AuthMode**: `AuthModeResolver` passa a resolver `READY` ∧ sem administrador activo para `AuthMode.Bootstrap` (cenário conceptual `BOOTSTRAP_REQUIRED`) em vez de `AuthMode.Legacy`. Fecha o gap identificado na documentação pós-9C.4: uma instalação legacy adoptada `READY` sem admin deixa de ter o Dashboard aberto e passa a disponibilizar o First-Run Wizard. O modo `Legacy` mantém-se apenas no contexto explicitamente standalone/testes (lifecycle e auth não ligados).
  - **BootstrapService**: `CreateAdminAsync` permite criar o primeiro administrador em `READY` sem qualquer admin, **sem** descer o estado para `CONFIGURING` e **sem** reconfigurar nada; `AdoptedFromLegacy`/`AdoptedAtUtc`/`LastReason` são preservados. Assim que exista administrador, a criação fecha (`AlreadyReady`) e o modo transita para `UserAuth`.
  - **Wizard**: campo de confirmação de password (verificado no cliente) e aviso quando o estado é `READY` (basta criar o administrador). Nenhum endpoint/contrato novo: reutiliza `GET /bootstrap` e `POST /api/bootstrap/*`.
  - **Segurança/concorrência**: unicidade do primeiro administrador garantida pela serialização do serviço (`SemaphoreSlim`) + transacção de `AdminUserStore.CreateFirstAdminAsync` (verificação de tabela vazia) + índice único `Username`; nenhuma password é logada ou devolvida; sem credenciais default; sem bypass de autenticação. Em `READY` sem admin os endpoints administrativos ficam bloqueados (`403 bootstrap-required`) — melhoria face ao anterior `Legacy` aberto.
  - **Testes**: 5 novos (`Legacy_ready_without_admin_creates_first_admin_and_keeps_ready`, `Legacy_ready_admin_creation_preserves_adoption_metadata`, `Legacy_ready_second_admin_after_creation_is_rejected`, `Legacy_ready_concurrent_admin_creation_creates_only_one`, `Admin_user_store_concurrent_first_admin_persists_single_row`) e actualização dos testes que codificavam o contrato `Legacy` aberto (`AuthPrimitivesTests`, `DashboardBootstrapEndpointTests`, `Phase94LiveRunApiTests`). Documentado em `docs/architecture/configuration-lifecycle.md`.

- **PHASE 9C.4 (2026-09-17): Live Run Monitor — observabilidade persistente, trigger manual e arranque agendado.**
  - **Modelo persistente**: `live_runs` + `live_run_steps` (migration aditiva `AddLiveRuns`), com `LiveRunTerminalStatus` (`Unknown`/`Completed`/`Failed`), fases `Idle`→`Error` e `CountsJson` como representação persistente tipada (`LiveRunCounts`). `SyncRun`/`SyncRunStep` permanecem uma família separada e inalterada.
  - **Instrumentação aditiva**: `ILiveRunProgress` + `NullLiveRunProgress` reportam fase, contadores (espelhados do `RunReport`, nunca recalculados a partir de logs) e actividades; com `null` (default) o comportamento prévio mantém-se exactamente igual. `LiveRunActivityFeed` é um **ring buffer em memória** (capacidade 200, thread-safe) e **não é persistido**.
  - **Coordinator único**: `RunCoordinator` (`LiveRunHost`) garante lock anti-dupla execução partilhado por CLI, scheduler e API. `StartAsync` (bloqueante) para CLI/scheduler e `KickStartAsync` (não bloqueante) para HTTP invocam **a mesma** pipeline Telegram. `Source` (`cli`/`manual`/`scheduler`) e `Mode` (`telegram`/`telegram-maintain`) são persistidos. `RecoverInterruptedRunsAsync` marca runs interrompidos por restart como `Failed`.
  - **API**: `GET /api/run/status` (snapshot sanitizado; `503 pipeline-not-configured` em `--web` sem `--telegram`) e `POST /api/run/start` (`202`, `409 already-running`, `503 web-allow-trigger-disabled`, `400`, `401`/`403` conforme o gate 9C.2). Nova flag opt-in `--web-allow-trigger` (default `false`). Sem autenticação própria: reutiliza sessão+CSRF, `--web-token` e o gate de bootstrap/legacy existentes.
  - **Scheduler**: duas acções com nomes estáveis, `telegramRun` e `telegramMaintainRun`, registadas no `ScheduledAutomationHost` existente e executadas pelo `ScheduledJobRunner` — **sem** scheduler paralelo e **sem** `StartAtUtc` (a UI calcula a `CronExpression`). Cron inválido é rejeitado de forma segura (job neutralizado, tick continua).
  - **Dashboard**: nova vista "Live Run" com estado, runId, fase, duração, última actualização, mensagem, contadores, últimas actividades, últimas execuções (24 h), estado do trigger, botão **Run now** e jobs agendados Telegram. Actualização automática por **polling leve de 3 s** com a vista activa; sem SSE/WebSocket, sem tail de logs e sem parsing de `docker logs`.
  - **Sanitização**: `LiveRunSanitizer` cobre credenciais embutidas em URLs (`CredentialSanitizer`) e, a partir desta subwave, também segredos em texto livre (`Authorization: Bearer …`, `cookie`, `api_key=…`, `session=…`). Mensagens truncadas a 200 caracteres, metadata limitada e sanitizada.
  - **Contrato preservado**: `RunReport` inalterado (55 propriedades públicas congeladas por teste), CLI existente inalterada e sem segundo pipeline. Documentado em `docs/architecture/run-observability-and-manual-trigger.md` §18 e `m3uCrawler/README.md`.
  - **Testes**: `Phase94LiveRunModelTests`, `Phase94RunCoordinatorTests`, `Phase94LiveRunInstrumentationTests`, `Phase94LiveRunApiTests` (auth/CSRF/machine token/409/503/standalone/segredos/dashboard) e `Phase94ScheduledRunTests` + `Phase94LiveRunHardeningTests` (concorrência, cron inválido, restart, contrato `RunReport`, sanitização).

- **PHASE 9C.3 (2026-09-16): afinidades com identidade estável, catálogo canónico por país e naming canónico no Dispatcharr.**
  - **R1 — `CanonicalChannel.Country`** (opcional, máx. 10; `null` = global). Não faz parte da identidade: `CanonicalChannel.Key` continua único e imutável. O `CatalogBaselineImporter` passa a propagar `baseline.Country`. A UI de canais ganha campo/coluna/filtro de país.
  - **R2 — `AffinityKind` (`Channel` | `Country`)** substitui a inferência implícita por `CanonicalChannelId == null`. Channel affinity é 0..1 por `CanonicalChannelKey` e resolve identidade; Country affinity preserva a injeção no `CountryChannelValidator` e o fluxo `affinity_no_channel`. A edição não permite mudar o canal de uma afinidade existente.
  - **Unicidade de `NormalizedMember`**: índice único filtrado (`WHERE Kind = 0`), pelo que uma variante não resolve para dois canais, mas a mesma variante pode coexistir numa Channel e numa Country affinity. Identidade referenciada por `Key` (nunca `Id`); `CanonicalChannelId` mantém-se apenas como coluna de transição.
  - **R3 — delimiter global** em `runtime-data/app_settings.json` (`affinityVariantDelimiter`, default `,`, `GET/POST /api/settings`). As variantes continuam a persistir uma por `AffinityMember`; mudar o delimiter não exige migration.
  - **UI/API**: dropdown de canais canónicos (fonte = catálogo canónico por país) que exclui canais já com Channel affinity; variantes num único campo separadas pelo delimiter; edição mantém o canal fixo.
  - **Dispatcharr**: criação usa o `DisplayName` actual do canal canónico; canais criados pelo crawler são registados como `ChannelOwnership.CrawlerManaged` e só esses são renomeados. `External`/`Unknown` nunca são renomeados. Novo `IDispatcharrChannelClient.UpdateNameAsync` (PATCH parcial no mesmo recurso já usado para `streams`).
  - **Migration aditiva e transaccional** `AddCanonicalCountryAndAffinityKind`: guard FK antes de qualquer mutação, classificação de grupos, backfill `CanonicalChannelId → Key` por JOIN, split controlado de grupos mixed (Channel + Country sem perder membros), preservação de órfãos e índice único filtrado. Reversibilidade garantida por tabela de proveniência `affinity_migration_backup` (não mapeada no EF): o `Down()` remove apenas os artefactos criados pelo Up (por `GeneratedCountryGroupId`), restaura o `CountryCode` original e, se o estado remanescente tiver variantes duplicadas que violem a unicidade global antiga, **aborta sem deduplicar** (rollback transacional com mensagem explícita).
  - **Testes**: `Phase93AffinityCatalogTests` (cardinalidade, unicidade filtrada, resolução/precedência, rename de DisplayName sem quebrar afinidade, país, delimiter, backfill/split/órfãos/idempotência da migration) e `Phase93DispatcharrNamingTests` (criação com DisplayName, rename CrawlerManaged, não-rename Unknown).
  - Documentado em `docs/architecture/channel-catalog-and-ownership.md` §12 e `m3uCrawler/README.md` § "Afinidades e catálogo canónico por país (PHASE 9C.3)".

- **PHASE 9C.2 (2026-09-16): wizard de primeira execução, primeiro administrador e autenticação normal.**
  - **Bootstrap mínimo** sobre o lifecycle da 9C.1: `NOT_CONFIGURED → POST /api/bootstrap/start → CONFIGURING → POST /api/bootstrap/admin → POST /api/bootstrap/complete → READY`, com página mínima `GET /bootstrap`. `GET /` encaminha para o wizard em `NOT_CONFIGURED`.
  - **Primeiro administrador** (`admin_users`): criado no wizard, password explícita (mínimo 12 caracteres, sem regras artificiais de complexidade), sem password default. Criação **transaccional** e **idempotente**; nunca dois primeiros administradores; o bootstrap não substitui o administrador existente nem volta a abrir depois de `READY`.
  - **Hashing**: PBKDF2-HMAC-SHA256 nativo (210 000 iterações, salt 16 B, hash 32 B), formato versionado `pbkdf2-sha256$<iter>$<salt>$<hash>`, comparação em tempo constante e hash *dummy* para utilizador inexistente/inactivo (anti-enumeração). Nenhuma dependência nova.
  - **Sessões persistentes** (`admin_sessions`) em SQLite: cookie `m3u_session` só com id opaco de 256 bits, `HttpOnly`, `SameSite=Strict`, `Secure` apenas sob HTTPS; revogável no logout, expira (12 h, janela deslizante com tecto absoluto), sobrevive a restart e roda o id em cada login (anti session-fixation). CSRF por sessão (`X-CSRF-Token`) em métodos mutantes.
  - **Configuração mínima (L2) para `READY`**: administrador activo + catálogo canónico utilizável + output dir utilizável (probe de escrita real) + Dispatcharr válido **só se activado**. Telegram/sources/ordering/import policies/grupos/source priority/scheduler permanecem advisory e não bloqueiam `READY`.
  - **Gate de autorização único** no `WebDashboardService` com três modos determinísticos: **Bootstrap** (só wizard/sessão/lifecycle; restantes endpoints 403), **UserAuth** (`READY` + administrador: sessão obrigatória, CSRF em mutantes) e **Legacy** (`READY` sem administrador: comportamento actual de `--web-token` preservado; não cria administrador nem migra credenciais). `--web-token` continua válido como credencial de máquina em todos os modos.
  - **Migration aditiva** `AddAdminUsersAndSessions` (apenas cria as tabelas `admin_users`/`admin_sessions` e índices; sem alterações destrutivas) — aplicada pelo fluxo existente com backup pré-migration.
  - **Testes**: `AuthPrimitivesTests`, `AdminSessionStoreTests`, `BootstrapServiceTests`, `BootstrapConfigurationValidatorTests`, `DashboardBootstrapEndpointTests` (fresh install, invariantes, concorrência, retomada, L2, sessão/cookie/CSRF, logout, legacy e `--web-token`).
  - Documentado em `docs/architecture/configuration-lifecycle.md` § "Autenticação e bootstrap (PHASE 9C.2)" e `m3uCrawler/README.md` § "Autenticação e bootstrap (PHASE 9C.2)".

- **PHASE 9C.1 (2026-09-16): ciclo de vida de configuração / first-run bootstrap.** Fundação fail-safe para instalações novas: o processo arranca, o Dashboard fica acessível e, enquanto não estiver configurado, **nenhum discovery automático nem job agendado executa**.
  - Novos estados persistidos `NOT_CONFIGURED` / `CONFIGURING` / `READY` (`m3uCrawler/Services/Configuration/ConfigurationLifecycleState.cs`), reportados como strings estáveis.
  - **Autoridade do estado persistido**: se existir estado válido, é usado sem inferência. Sem estado, corre o bootstrap: evidência objectiva de instalação operacional → adopta `READY` com *legacy adoption*; caso contrário → `NOT_CONFIGURED`.
  - **Legacy adoption** determinada apenas por entidades criadas por operação (sources, channel-sources, ordering lists/items, import policies, grupos, jobs, sync-runs, reviews, matching audits, affinity groups, ownership Dispatcharr, identity rules) e por artefactos de output (`import_history.json`, `playlist.m3u`, `telegram_run_report.json`, `telegram_playlist_*.m3u`). `canonical_channels`/`channel_aliases` (seed/baseline) e `source_priority_policies` (default global) não contam.
  - **Persistência**: `configuration_lifecycle.json` ao lado do `channel-catalog.db`, no volume persistente (produção `/data`), com escrita atómica. Sem tabela nova nem migração de BD.
  - **Gate único** (`IConfigurationGate`): `ScheduledJobRunner` bloqueia todos os jobs automáticos (incluindo `discoverM3u`) fora de `READY`, registando `blocked:not-configured` sem tratar como sucesso nem avançar o próximo tick. O Telegram automático (`--telegram-maintain` e `--loop-hours`) é igualmente bloqueado; a invocação manual de um único ciclo não é afectada.
  - **Dashboard**: endpoint read-only `GET /api/configuration/lifecycle` (respeita `--web-token`), acessível em `NOT_CONFIGURED`, com marca de adopção e avaliação **advisory** dos requisitos §32.18 §5 — que **não** bloqueiam `READY` (compatibilidade com instalações existentes).
  - Observabilidade proporcional: `🧭 Configuration lifecycle: …`, `⛔ automatic discovery blocked: not configured`, `⛔ scheduler blocked: not configured`; sem dados sensíveis.
  - Testes: `ConfigurationLifecycleTests`, `ConfigurationGateSchedulerTests`, `ConfigurationLifecycleEndpointTests` (bootstrap, persistência/reload, transições, adopção legacy com e sem evidência, advisory não-bloqueante, gate do scheduler e do discovery, dashboard acessível em `NOT_CONFIGURED`).
  - Documentado em `docs/architecture/configuration-lifecycle.md` e `m3uCrawler/README.md` § "Ciclo de vida de configuração (PHASE 9C.1)".

### 🛡️ Correcções / Hardening
- **PHASE 9A.3 (2026-09-16): concorrência global por Xtream account no run Telegram.** A Phase 9A.2 integrou `TestStreamsAsync` com o `AccountBoundedWorkerPool`, mas construía **um pool local por chamada**, pelo que dois `CandidatePlaylist` da MESMA account processados por workers diferentes podiam testar streams em paralelo, e `MaxConcurrentAccounts` não era um limite global efectivo (era ditado pelo semáforo de candidates). Correcção:
  - Novo `m3uCrawler/Services/Validation/AccountGateCoordinator.cs`: coordenador com lifetime de **run**, `SemaphoreSlim` global limitado a `MaxConcurrentAccounts`, `ConcurrentDictionary<string, SemaphoreSlim(1,1)>` por `AccountId`, refcount/cleanup e `Dispose` determinístico. Aquisição slot global → gate da account; libertações sempre em `finally`.
  - `TelegramScraperService.SearchAndTestM3UInTelegramAsync` cria **uma** instância do coordinator (ao lado de `validationState`/`tester`/`accountValidator`) e passa-a por `ProcessCandidateAsync` → `TestStreamsAsync`, sendo partilhada por todos os candidate workers; `Dispose` no `finally` do run, após `processingDone`.
  - `TestStreamsAsync` deixa de criar o pool local e passa por `AccountGateCoordinator.RunExclusiveAsync` + `AccountValidator.ValidateAccountAsync` (delegate de validação injectável apenas para testes; em produção é sempre o validador real).
  - **Cancellation**: o token do run controla apenas a admissão (slot global/gate); depois de admitida, a validação corre até ao fim (`CancellationToken.None`), repondo a semântica anterior à 9A.2. Se cancelada antes da admissão, devolve a mesma forma com streams não-funcionais.
  - **Não alterado**: `AccountIdentity`, `AccountBoundedWorkerPool`, `AccountValidator`, `StreamValidationOptions`, `XtreamAccountLockManager`, timeouts HTTP, cache, `HostFailureTracker`, `XtreamPublicationResolver`, descoberta, parser e download. Sem dedup por `AccountId` nesta fase.
  - Testes: `Phase93AccountGateCoordinatorTests` (regressão explícita do bug 9A.2 com dois workers concorrentes + A–H) e `Phase92TelegramAccountPoolIntegrationTests` reorganizado para exercer o caminho real `TestStreamsAsync` sem rede. Documentado em `docs/PHASE-9A-3-global-account-concurrency.md`.

### ✨ Adicionado
- **Fallback URL-only no `XtreamPublicationResolver.ResolveFromHtml`** (introduzido 2026-09-14): quando os caminhos DOM-based e flat-text não produzem nenhuma conta mas o HTML contém URLs `get.php?username=…&password=…`, o resolver extrai credenciais directamente das URLs como último fallback. Cobre o caso do publisher `m3u-sᴄᴀɴ` cuja edição de 14-09-2026 (mensagem Telegram `110751`) deixou de emitir labels visíveis e passou a disponibilizar **apenas as URLs** em `get.php`. A nova lógica:
  - Detecta URLs `<scheme>://<host>[:<port>]/get.php?username=X&password=Y` independentemente dos atributos ou do texto à volta (`href`, texto puro, etc.).
  - Suporta HTML entities (`&amp;`, `&#38;`, `&lt;`, `&gt;`) no query string.
  - Valida que **ambos** `username=` e `password=` estão presentes (URLs sem uma das chaves são silenciosamente ignoradas).
  - Deduplica por `(scheme, host, port, username)` exactamente como exige a regra de identidade lógica do projecto (password nunca participa).
  - Restritivo: hostname passa `LooksLikeValidHost`, scheme é `http`/`https`, username/password ≤ 64 chars.
  - Restaurado o caminho DOM-based como preferido: **só** activamos o fallback quando os dois caminhos anteriores devolvem 0 contas, evitando qualquer regressão na resolução do formato antigo (110705 produz 9 contas pelo DOM, fica inalterado).
  - Validado em reprodução isolada: 110751 (`neorcqds.top:8080`) passa de `XtreamAccountsDiscovered=0` para `=751`; 110705 (mesmo host) continua em 9 contas. Zero duplicação de pipeline, zero mutação do Dispatcharr, zero regressão em testes existentes.
- **Testes** (11 novos, todos passam em `<1s`): `XtreamPublicationResolverUrlOnlyFallbackTests` — formato legacy não-regressão, formato URL-only puro, dedup múltiplas URLs, portas explícitas/implícitas (https → 443), query parameters em ordem diferente, URL inválida/incompleta silenciosamente ignorada, URL sem username ignorada, URL sem password ignorada, HTML sem contas devolve 0, texto tipo `username=foo` mas não em URL real não é capturado, **fixture sanitizada baseada na estrutura real de 110751** com credenciais fictícias que resolve 3 contas distintas.
- **Total agora**: **1466 testes** (anterior: 1455) — 11 novos, 0 removidos, 0 skipped incrementado.

### ✨ Adicionado
- **Processamento incremental dos candidates** (PIPELINE-INC, 2026-09-14): o pipeline Telegram deixou de ser **batch-then-test** (acumulava todos os candidatos num `List` e só depois os testava) para passar a **producer-consumer online**: cada `CandidatePlaylist` descoberto durante a enumeração é emitido imediatamente para um `System.Threading.Channels.Channel<CandidatePlaylist>` e processado por um worker dedicado com `SemaphoreSlim(maxConcurrency)`. Resolve a latência artificial observada na run da mensagem `110751` (detectada ~10:50:16, primeiro teste de playlist só ~11:03:30 — 13 min de espera artificial). Agora a latência entre deteção e teste é (download+teste) / parallelism. Invariantes preservados (R1/R2/9A sem alteração):
  - `CountryChannelValidator.AnalyzePlaylist`/`FilterStreamsByCountry` (R1) — exact same usage.
  - `M3uParserService.Parse` — exact same usage.
  - `StreamValidationTesterFactory.CreateTester` / `StreamValidationOptions` — exact same usage; `OverallTimeoutSeconds = 12` inalterado.
  - `StreamValidationCache` / `HostFailureTracker` — não alterados.
  - `PipelineIngestionService` (catalogo persistente) — exact same usage.
  - `wtelegram.config` — não tocado.
- **Classificador de falhas em `M3uTesterService.DownloadPlaylistContentAsync`** (PIPELINE-INC-DIAG, 2026-09-14): rótulo "timeout ou erro de rede" insuficiente — substituído por log estruturado com `kind` ∈ {`InvalidUrl`, `Http4xx`, `Http429`, `Http5xx`, `Timeout`, `Cancellation`, `Dns`, `TlsOrConnection`, `Network`, `Unknown`}, cada um com `durationMs`, `status` HTTP e URL sanitizada (sem credenciais, sem query string). API pública intacta (`Task<(string?, bool)>`). Apenas logging, não afecta comportamento HTTP nem timeouts.
- **Testes** (10 novos, todos passam em `<1s`): `TelegramPipelineIncrementalTests` — callback `onCandidateProduced` invocado por candidate; `Channel<CandidatePlaylist>` delivery sob concorrência (50 candidates, maxConcurrency=5); consumer termina em `Complete()`; erro no processamento de UM candidate não derruba o pipeline (5/6 processados); producer+consumer correm em paralelo; múltiplos producers não corrompem o canal; callback `null` é no-op; `RunReport.CandidatesFound` incrementa-se por candidato; Cancellation propaga-se ao worker; classificador `InvalidUrl` para URL vazia.
- **Total agora**: **1476 testes** (anterior: 1466) — 10 novos, 0 removidos, 0 regressões.

### 🛡️ Correcções / Hardening
- **PHASE 9A-FIX (2026-09-14): `ConnectTimeout` configurável e classificação inequívoca de `TaskCanceledException` interna**. A PHASE 9A introduziu (em `ed3d6ef`) um `SharedHttpClient` estático com `SocketsHttpHandler.ConnectTimeout = TimeSpan.FromSeconds(DefaultConnectionTimeoutSeconds=5)` e `HttpClient.Timeout = TimeSpan.FromSeconds(DefaultOverallTimeoutSeconds=12)`. Em produção observámos (em `92a5d0d`) um padrão repetitivo `[DownloadPlaylist] kind=Unknown durationMs=~5000 error=TaskCanceledException` que correspondia a timeouts internos do `HttpClient` classificados incorrectamente como `Unknown` e que não respeitavam o valor configurado pela `StreamValidationPolicy`. Correcção aplicada (read-only sobre `M3uTesterService` + extracção de factory testável, sem tocar em `CountryChannelValidator`, `M3uParserService`, `TelegramPublicationResolver`, `XtreamPublicationResolver`, `PipelineIngestionService`, `StreamValidationCache`, `HostFailureTracker`, nem `StreamValidationTesterFactory`):
  - **Factory testável `HttpClientFactory`** (novo ficheiro `m3uCrawler/Services/Validation/HttpClientFactory.cs`): encapsula a criação do `HttpClient` partilhado com cache `ConcurrentDictionary<(int ConnectSeconds, int OverallSeconds), (HttpClient Client, SocketsHttpHandler Handler)>`. Cada combinação distinta de timeouts obtém o seu próprio `HttpClient` uma única vez via `GetOrAdd`; testers com as mesmas options partilham-no; **nunca** há um `HttpClient` por stream. O método público `CreateConfiguredClient(int, int)` devolve o `SocketsHttpHandler` em conjunto com o `HttpClient` para que testes possam inspeccionar `ConnectTimeout` directamente, sem reflection.
  - **Resolução lazy via `_client` por tester**: o construtor do `M3uTesterService` resolve o `HttpClient` correcto a partir das options já sanitizadas via `HttpClientFactory.ResolveClient(...)`. `ProbeOnceAsync` (estático) também resolve internamente para suportar override options em chamadas individuais.
  - **`IsHttpClientInternalTimeout(Exception)`** em `M3uTesterService`: nova heurística que detecta se uma excepção foi causada por um timeout **interno** do `HttpClient`/`SocketsHttpHandler` (i.e., a cadeia de `InnerException`s contém um `TimeoutException`), distinguindo-a de uma cancellation externa do caller. Heurística **conservadora**: baseia-se apenas na presença de `TimeoutException` em qq nível de `InnerException`, conforme documentado pelo .NET 9 (dotnet/runtime #47484, #63706, #78070). Cancelamentos externos não trazem `TimeoutException` na cadeia, logo são inequivocamente distinguíveis.
  - **Classificador de `DownloadPlaylistContentAsync` melhorado**:
    - `kind=HttpConnectTimeout` — `TaskCanceledException` interna + `durationMs < ConnectionTimeout*1000+500` (i.e., `SocketsHttpHandler.ConnectTimeout` disparou).
    - `kind=HttpRequestTimeout` — `TaskCanceledException` interna + duração ≥ `ConnectTimeout+500` (i.e., `HttpClient.Timeout`).
    - `kind=Timeout` — `attemptCts.CancelAfter(OverallTimeout)` cooperativo disparou e não há `TimeoutException` interna.
    - `kind=Cancellation` — `cancellationToken` do caller foi cancelado.
    - `kind=Unknown` — mantido como catch-all genuíno (não mais mascarando timeouts internos do HttpClient).
  - **`Sanitize` já existente** continua a clampar `ConnectionTimeoutSeconds` ao intervalo `[1, 300]` e `OverallTimeoutSeconds` ao intervalo `[1, 600]`. Não foram alterados defaults (`5` connect, `12` overall) para não mascarar o problema externo de providers. O cache `HttpClientFactory` está limitado por estes clamps (máximo teórico de 180 000 chaves; em prática ≤10).
- **Testes** (24 novos, todos determinísticos com `TcpListener` local ou instâncias directas do factory — sem Internet pública): `Phase9AFixTests` —
  - `StreamValidationOptions_ConnectionTimeout_accessor_matches_seconds`: accessor `ConnectionTimeout` devolve `TimeSpan` correcto.
  - `StreamValidationOptions_Sanitize_clamps_ConnectionTimeoutSeconds`: confirma clamp `[1, 300]`.
  - `IsHttpClientInternalTimeout_returns_true_for_TaskCanceledException_with_inner_TimeoutException`: caso básico.
  - `IsHttpClientInternalTimeout_returns_true_for_nested_chain`: cadeia aninhada.
  - `IsHttpClientInternalTimeout_returns_false_for_external_cancellation`: cancellation externa.
  - `IsHttpClientInternalTimeout_returns_false_for_plain_HttpRequestException`: network exception.
  - `IsHttpClientInternalTimeout_returns_false_for_plain_OperationCanceledException`: defensivo.
  - `IsHttpClientInternalTimeout_returns_false_for_null`: defensivo.
  - `IsHttpClientInternalTimeout_traverses_deep_nesting_with_intermediate_non_Timeout_types`: cadeia profunda.
  - `IsHttpClientInternalTimeout_handles_ConnectTimeout_chain_realistic_in_net9`: reproduz a cadeia documentada para `ConnectTimeout` (dotnet/runtime #47484).
  - `IsHttpClientInternalTimeout_handles_HttpClientTimeout_chain_realistic_in_net9`: reproduz a cadeia documentada para `HttpClient.Timeout` (dotnet/runtime #78070).
  - `HttpClientFactory_applies_ConnectionTimeoutSeconds_to_SocketsHttpHandler_ConnectTimeout`: prova formal de que o valor chega ao handler.
  - `HttpClientFactory_applies_OverallTimeoutSeconds_to_HttpClient_Timeout`: prova formal de que o valor chega ao client.
  - `HttpClientFactory_propagates_custom_values_not_just_defaults`: valores custom `(15, 45)` chegam ao handler/client.
  - `HttpClientFactory_resolves_distinct_clients_for_distinct_keys`: chaves distintas → instâncias distintas.
  - `HttpClientFactory_returns_same_client_for_same_key`: mesma chave → mesma instância.
  - `HttpClientFactory_handlers_are_disposable_independently_via_client_disposing`: `disposeHandler: true` funciona.
  - `HttpClient_with_configured_timeouts_resolves_to_distinct_cache_entries`: invariante lógico do cache.
  - `Multiple_testers_with_same_timeouts_share_the_same_HttpClient`: cache estável.
  - `Tester_with_different_timeouts_gets_distinct_HttpClient_cached_separately`: invariante.
  - `OverallTimeout_2s_terminates_within_3s_when_blackhole`: `OverallTimeout=2s` dispara em <3.5s com blackhole local.
  - `ConnectTimeout_is_decoupled_from_OverallTimeout_classification`: blackhole com `OverallTimeout=2s` termina cedo e devolve `(null, false)`.
  - `TestManyBoundedAsync_still_respects_max_concurrency_with_configured_options`: regression guard da 9A — bounded concurrency preservada com partial response + `MaxConcurrency=2`.
- **Total agora**: **1506 testes** (anterior: 1483) — 23 novos, 0 removidos, 0 regressões.
- **Sem alterações funcionais em produção detectáveis**: o `HttpClient` por defeito continua com `ConnectTimeout=5s` e `OverallTimeout=12s` (idêntico ao actual em `92a5d0d`). A diferença visível em logs é a classificação: `Unknown / TaskCanceledException` deixa de aparecer para timeouts HTTP internos (substituído por `HttpConnectTimeout` ou `HttpRequestTimeout`).
- **Limitações documentadas**: (a) o evento físico de TCP `ConnectTimeout` (SYN sem resposta) não é reproduzível de forma totalmente determinística num teste unitário sem manipular routing de pacotes ou usar IP não-roteável (dependente de rede); a invariante é validada por inspecção directa do `SocketsHttpHandler.ConnectTimeout` e por reprodução das cadeias de excepção documentadas. (b) `HttpClientFactory.Cache` não tem mecanismo de eviction; o número de chaves distintas é naturalmente pequeno (1-3 em produção) e os `Sanitize` garantem limites finitos. (c) `IsHttpClientInternalTimeout` é conservadora (só detecta via `TimeoutException` na cadeia); cancelamentos cuja cadeia não traga `TimeoutException` serão classificados como `Cancellation` ou `Timeout` (coop.), nunca falsamente como internal timeout.

### 🛡️ Correcções / Hardening
- **PIPELINE-INC-HARDENING (2026-09-14): thread-safety do `RunReport` sob producer-consumer concorrente**. Risco detectado: o `RunReport` é mutado por até `maxConcurrency` tasks do worker simultaneamente, com escritas em contadores `int` (que não são atómicos em C#) e em `List<>` (que não é thread-safe). Correcção aplicada:
  - **Counters `int` que são escritos por múltiplas tasks**: convertidos para `internal int _Field` com property-wrapper. `Interlocked.Increment(ref rep._X)` e `Interlocked.Add(ref rep._X, n)` substituem `++` e `+=`.
  - **`List<>` (`RejectionReasons`, `DiscoveredPlaylists`)**: protegidos por `lock(rep.SyncRoot)`. `RunReport` expõe novo `internal readonly object SyncRoot`.
  - **Helpers estáticos `AddRejection` / `AddDiscovered`**: encapsulam o `lock(rep.SyncRoot)` para invocação limpa em `ProcessCandidateAsync`.
  - **`M3uTesterService`**: zero alterações (não é concorrente).
  - **`CountryChannelValidator` / `M3uParserService` / `StreamValidationTesterFactory` / `StreamValidationCache` / `HostFailureTracker`**: zero alterações.
- **PIPELINE-INC-CHANNEL-ANALYSIS (2026-09-14): análise do `Channel<CandidatePlaylist>` unbounded**. Pior caso conhecido: 1 mensagem HTML (110751) produz 751 accounts Xtream; o channel cresce linearmente. Cada `CandidatePlaylist` é ~500 bytes → pior caso plausível seria ~50 MB para 100 000 accounts/mensagem. Fan-out é determinístico e bounded (sem recursão, sem loop). Channel unbounded é seguro no desenho actual — não convertido para bounded. Activar bounded seria contraproducente (perda potencial de candidates) sem benefício mensurável.
- **Testes** (7 novos, todos passam em `<16ms`): `TelegramPipelineRunReportThreadSafetyTests` — 1000 increments concorrentes em 4 counters distintos, valor exacto por counter; 200 writes concorrentes em `RejectionReasons` e `DiscoveredPlaylists` sem perda/erro; 5 runs repetidos sem perda; mix counter+list sem race; stress com `SemaphoreSlim(8)`; fan-out múltiplo simultâneo; integridade de `Status` string sob escritas concorrentes.
- **Total agora**: **1483 testes** (anterior: 1476) — 7 novos, 0 removidos, 0 skipped incrementado.

### ✨ Adicionado
- **Resolução de publicações Telegram via t.me/c/ (introduzido 2026-09-09)**: uma mensagem Telegram que contém apenas um deep link para outra publicação do Telegram (e.g. `https://t.me/c/1635952193/110637`) deixa de ser invisível para o crawler. O pipeline agora descobre, resolve e tria estas referências em três camadas distintas:
  - **`TelegramPublicationDiscovery`** (parsing puro, sem I/O): identifica `https://t.me/c/<channel>/<message>` e URLs HTTP públicas adicionais no texto da mensagem.
  - **`TelegramPublicationResolver`** (recebe um `TelegramMessageFetcher` delegate, testável sem WTelegram): resolve a mensagem via `WTelegram.Channels_GetMessages(inputChannel, [InputMessageID])`, classifica o resultado em **`Resolved` / `ResolutionFailed` / `RequiresReview` / `Unsupported`**, aplica recursão com depth-limit (`MaxResolutionDepth = 3`) e seen-set para evitar ciclos, e re-aplica `XtreamPublicationResolver.ResolveFromHtml` quando o attachment é HTML com cards Xtream.
  - **`TelegramScraperService`** (orquestrador): integra as duas camadas no loop principal, converte contas Xtream descobertas em `CandidatePlaylist { DetectedFrom="xtream publication" }` que entram no mesmo pipeline M3U/Xtream existente — **zero duplicação** de pipeline.
- **`RunReport` estendido** com 8 novos contadores para observabilidade da nova capacidade:
  - `PublicationsDiscovered`, `PublicationsResolved`, `PublicationsResolutionFailed`, `PublicationsRequiresReview`, `PublicationsUnsupported`
  - `XtreamAccountsDiscovered`, `XtreamAccountsAfterDedup`, `XtreamAccountsForwarded`
  - Lista `PublicationsTriageLog` de `PublicationTriageEntry` com `Reference`, `ChannelId`, `MessageId`, `State`, `Reason`, `XtreamAccountsFound`. `Reference` é sempre URL público; `Reason` é sanitizado contra credenciais antes de ser persistido. `ToString()` aplica `CredentialSanitizer.SanitizeUrl` ao `Reference` e `SanitizeText` ao `Reason`.
- **`CredentialSanitizer.SanitizeText`** (novo): sanitiza texto arbitrário (caption Telegram, mensagens de revisão) contra credenciais Xtream. Aplica `SanitizeUrl` a cada URL `http(s)://` presente e preserva o resto.
- **`CredentialSanitizer.SanitizeUrl`** estendido para cobrir schemes não-HTTP (`xtream://`, `rtsp://`, etc.) tanto em formato userinfo (`user:pass@`) como path-style (`user/pass@`).
- **Tratamento explícito de FLOOD_WAIT** no `TelegramPublicationResolver`: retry com delay (configurável via `floodWaitRetryCount`, default 2) antes de desistir. Após esgotar, marca a publicação como `ResolutionFailed` em vez de desaparecer.
- **Testes** (40 novos, todos passam): `TelegramPublicationDiscoveryTests` (8), `TelegramPublicationResolverTests` (15), `TelegramReferenceToXtreamAccountsE2ETests` (6 — incluindo o teste end-to-end `t.me/c/1635952193/110637 → HTML → múltiplas contas → múltiplas sources independentes`), `RunReportPublicationCountersTests` (3). Adicionalmente, cobertura de segurança para passwords ausentes em `ToString`, `Reason`, `LogicalIdentity`. Total agora: **1172 testes** (anterior: 1132).
- **Documentação** (`m3uCrawler/README.md`): nova subsecção "Camadas de descoberta e resolução (introduzido 2026-09-09)" com a sequência `Discovery → Resolution → Classification → Ingestion`; referência `t.me/c/<channel>/<message>` deixa de ser uma "limitação conhecida" e passa a descrever o caminho suportado, com casos cobertos e FLOOD_WAIT.
- **Parser adaptativo de publicações Xtream (commits `e963a7a` → `4df856e`)** (esta iteração): substitui o parser rígido anterior por um mecanismo multi-cue que absorve a variabilidade de publishers sem heurística específica. Validado em produção no servidor `192.168.68.142` contra dois publishers reais (HTML msg `110658` do canal `1635952193` e HTML msg `110705` do mesmo canal). Os publishers publicam HTML com formatação fancy (labels em small caps Unicode, divisores `━━━━`, separador `➢`) — o parser lida com isso sem alterar código.
  - **Estratégia por âncoras, não por estrutura HTML**: o resolver encontra a posição de cada label conhecido (`host`, `user`, `pass`, `m3u`, `epg`, `expires`, `port`, ...) no texto limpo (após strip de box-drawing U+2500..U+25FF, símbolos U+2600..U+27BF, math U+2200..U+23FF, pares surrogate U+D800..U+DFFF, ANSI codes `ESC[...]`). Cada âncora é normalizada via transliteração (modifier letters U+1D00..U+1D7F + IPA U+0260..U+029F + Latin-1 supplement U+00C0..U+00FF → ASCII) e classificada por proximidade.
  - **Clustering com boundary explícito em `host`**: cada `host` repetido FECHA o cluster anterior e ABRE um novo. Isto resolve o problema fundamental de fundir cards adjacentes quando estão dentro da janela de proximidade. Janela conservadora de 1000 chars cobre o caso iptvgold onde `Host` fica no topo e `M3U`/`EPG` no fundo (≈700-800 chars de distância).
  - **Tolerância a separadores**: `:`, `=`, `→`, `➢`, `|`, `-` ou **só whitespace** (`Host   http://...`) são separadores válidos. O extractor (`ExtractValueFromLineAfterPosition`) strip adicional de arrow chars residuais no valor.
  - **Validação de host**: `LooksLikeValidHost` rejeita texto livre sem `.` ou TLD — protege contra falsos positivos em texto que mencione `Host:`/`User:`/`Pass:` sem ser uma publicação real.
  - **Identidade lógica preservada**: `scheme://host:port/username` continua a ser a chave de dedup — duas contas no mesmo servidor com usernames diferentes são **fontes independentes** (já validado contra iptvgold com 70 contas distintas).
  - **Fallback explícito**: se o caminho DOM-based (`<table>`, `<tr>`, `class=card`, `<hr>`) não produz contas mas o `InnerText` tem ≥ 50 chars, o caminho `ResolveFromFlatText` é invocado. Sem este fallback, qualquer HTML sem `<table>`/`<hr>` produzia zero contas.
  - **Validado em produção** (single-cycle no servidor, `--history-hours 24`): o `telegram_run_report.json` contém 10 entradas `discoveredPlaylists[].source = "xtream publication:"` (URL com credenciais sanitizadas como `***/***`), cada uma correspondendo a uma conta Xtream distinta do HTML da msg `110705`. Total: **9 utilizadores únicos** do `neorcqds.top:8080` testados e integrados na `playlist.m3u`. O HTML da msg `110658` (iptvgold) também é processado correctamente: extrai 70 contas distintas num único `ResolveFromHtml` (validado unitariamente com fixture `m3uCrawler.Tests/Fixtures/iptvgold_07-09-2026.html`, MD5 `b8af67a21c7810fcd96868d7ae142a5d`).
  - **Testes** (15 novos, todos passam): `XtreamPublicationResolverRealWorldFixtureTests` (5 — fixture iptvgold com asserts honestos sobre contagem ≥2, sem leak de password em `ToString`, MEDIA LIST filtrada), `XtreamPublicationResolverAdaptiveParsingTests` (7 — separador pipe, labels Unicode PT/ES, label-on-one-line-value-on-next, host inválido rejeitado, cluster split por host boundary, etc). Total agora: **1187 testes**.
  - **Limitações documentadas nos próprios testes**: (a) cards adjacentes que partilham labels (`CHANNELS`/`MOVIES`/`SERIES` no MEDIA LIST) podem fundir-se em cluster único — a heurística `host`-boundary mitiga mas não elimina o caso onde os anchors de media list se misturam com labels do card; (b) `Label -> Value` (seta colada sem espaço) não suportado, separador requer pelo menos 1 espaço; (c) variantes FR/ES (`Serveur`/`Mot de passe`) não no vocabulário actual.
  - **Sem mudança de comportamento do pipeline a jusante**: contas descobertas continuam a ser promovidas via `TelegramScraperService.PromoteXtreamAccount` (já existente) a `CandidatePlaylist { DetectedFrom = "xtream publication" }`. O `AnalyzePlaylist` → `M3uParserService.Parse` → `ValidateStreams` → `TestStreamsAsync` continua a ser o único caminho de teste e merge. **Zero duplicação de pipeline.**
  - **Fixture nova em testes**: `m3uCrawler.Tests/Fixtures/iptvgold_07-09-2026.html` (532 KB, 70 contas Xtream, gitignored — contém credenciais reais obtidas em runtime de publicação autorizada). Auto-copiada para `bin/Fixtures/` via `<None Update="Fixtures\*"/>` no `m3uCrawler.Tests.csproj`. MD5 verificado em download: `b8af67a21c7810fcd96868d7ae142a5d`.

### 🛠 Validação end-to-end do scheduler (2026-09-11)
- **Novo ficheiro de testes** `m3uCrawler.Tests/SchedulerEndToEndTests.cs` (8 testes) que demonstram o encadeamento real das 4 actions do scheduler + composição com catálogo populado + idempotência de dispose.
- **Validação confirmou**: `discoverM3u`, `validatePlaylist`, `generatePlaylist`, `syncDispatcharr` são resolvidas via DI, executam serviços reais, persistem `LastRunAtUtc`/`LastResult`/`NextRunAtUtc`, respeitam cancellation, capturam erros sem matar o runner, e são encadeáveis num único tick sequencial.
- **Gap documentado (suportado por código)**: o pipeline real (`M3uCrawlerService.SearchM3u8Files` ou `TelegramScraperService.SearchAndTestM3UInTelegramAsync`) **NÃO** chama `EnsureSourceAsync`/`RecordChannelSourceAsync` — confirmado por grep. Após runs de produção, o catálogo canónico fica apenas com canais seedados pelo JSON baseline; nenhum `SourceEntity`/`ChannelSourceEntity` novo é acrescentado pelo pipeline. O teste `Real_pipeline_does_not_upsert_sources_or_channel_sources` regista este comportamento explicitamente. **(Fechado em `PHASE-Bridge` abaixo.)**
- **Total agora**: 1335 testes em Release (anterior: 1327), 0 falhas, 0 warnings novos.

### 🛠 PHASE-Bridge / R3-hardening — normalizer case-insensitive (2026-09-11)
- **Bug crítico fechado**: `ChannelNormalizer.CountryToken` regex exigia capitalização. `"cnn portugal"` (já lowercase) **não** era reduzido para `"cnn"` — quebrando a coerência entre o título raw e a forma normalizada que o matcher consulta. Causava `CNN Portugal` → `UnknownReviewRequired` apesar do catálogo ter `cnnportugal` com `CreateEligible`.
- **Correcção**: adicionada `RegexOptions.IgnoreCase` ao `CountryToken` em `ChannelNormalizer`. Agora `"CNN Portugal"` e `"cnn portugal"` normalizam para `"cnn"` consistentemente.
- **`CatalogBaselineImporter` melhorado**: persiste também as versões normalizadas de cada alias (sem tokens de país/qualidade). Camada defensiva que protege contra futuros canais cujo DisplayName contenha tokens não-removíveis.
- **Testes adicionados** (`ChannelNormalizerTests`, 8 testes novos): `Normalize_strips_country_tokens_case_insensitively` cobre `CNN Portugal → cnn`, `cnn portugal → cnn`, `SIC notícias → sic noticias`, `Porto Canal → porto canal`, etc.
- **Resultado**: 1370 → **1378 testes** em Release, 0 falhas, 3x runs estáveis. `Permutation_produces_deterministic_result` agora passa (CNN Portugal resolve para `cnnportugal`).
- **Diagnóstico 27 streams pós-R3**: 19 matched (vs 18 pré-R3), 2 auto-created (vs 3), 6 REJECTED (estável). Confidence média 0.95 (vs 0.85 pré-R3).
- **Não cria nova PHASE**: é hardening final do PHASE-Bridge. Documentado como `PHASE-Bridge / R3-hardening` em `docs/IMPLEMENTATION_ROADMAP.md` secção 32.15.

### 🛠 PHASE 9A — Autópsia do bloqueio HTTP (2026-09-11)
- **Gap de integração identificado**: a primeira execução real no servidor expôs que `TelegramScraperService.DownloadPlaylistContentAsync` usava `new HttpClient { Timeout = 30s }` em vez do `M3uTesterService.SharedHttpClient` da PHASE 9A. Em produção, candidatos como `strscr1912.xyz:2095` conseguiam bloquear a iteração durante minutos.
- **Correcção mínima**: adicionado `M3uTesterService.DownloadPlaylistContentAsync(url, ct)` que reusa o `SharedHttpClient` e o `OverallTimeout` (default 12s) da 9A. `TelegramScraperService` foi refactorizado para chamar este método, passando o `M3uTesterService` como parâmetro.
- **Sem segundo framework**: a correcção **integra-se** na infra-estrutura 9A existente, sem duplicar lógica de timeout/retry.
- **Testes adicionados** (`HttpTimeoutAutopsyTests.cs`, 6 testes, 5 pass + 1 skipped documentacional): reproduzem blackholes TCP, partial response com stall, 404 rápido, e batch com blackhole no meio. Confirmam que o download termina dentro de `OverallTimeout=12s` + tolerância e que `CancellationToken` do caller é respeitado.
- **Resultado**: 1378 → **1384 testes** em Release (5 novos + 1 skipped + 1 documentacional), 0 falhas, 2x runs estáveis. Build: 0 errors, 0 warnings novos.
- **Não foi feito**: novo retry-limit/circuit-breaker, `--telegram-once`, alterações a R1/R2/R3, push ao remoto, substituição da imagem em produção.
- Documentado como `PHASE 9A — Autópsia do bloqueio HTTP` em `docs/IMPLEMENTATION_ROADMAP.md` secção 32.16.

### 🛠 Redução da janela Telegram de ~300h para 24h (2026-09-11)
- **Motivação**: evidência operacional mostrou que resultados relevantes continuam a aparecer dentro de 24h (ex: `m3u@204.52.191.254-HITS_DI_@RATTENPAPST.html`, encontrada no mesmo dia). Janela de ~480h (20 dias) no `docker-compose.yml` de produção era desnecessariamente grande.
- **Alterações (cirúrgicas, apenas no `historyHours`)**:
  - `Program.cs:165` — default de `telegramHistoryHours` de `48` para `24`.
  - `Program.cs:642` — exemplo no `--help` actualizado para `--history-hours 24`.
  - `Program.cs:627` — mensagem de `--help` indica "(padrão: 24)".
  - `TelegramScraperService.cs` (4 overloads) — defaults de `historyHours` de `48` para `24`.
  - `docker-compose.yml:14` — `--history-hours "480"` → `"24"`.
  - `docs/architecture/run-observability-and-manual-trigger.md` — snippets com `48` → `24`.
- **Não alterado**: filtros, canais, descoberta, validação, ingestão, bridge para catálogo, R1/R2/R3, PHASE 9A, timeouts, M3uTesterService, Dispatcharr, scheduler, catálogo canónico, matching, ordering, legacy paths.
- **Testes adicionados** (`TelegramHistoryWindowTests.cs`, 4 testes): `All_SearchM3UInTelegram_overloads_default_to_24_hours`, `Program_cs_uses_24h_as_default_for_telegramHistoryHours`, `Docker_compose_passes_history_hours_24_not_480`, `CutoffDate_for_default_historyHours_is_24h_ago_plus_margin`.
- **Resultado**: 1384 → **1388 testes** em Release (4 novos), 0 falhas, 2x runs estáveis. Build: 0 errors, 0 warnings novos.

### 🛠 PHASE-Bridge / R2 — saneamento do catálogo canónico PT (2026-09-11)
- **Gap fechado (suportado por código)**: a validação end-to-end identificou divergências entre o `CatalogSeed` programático e o JSON baseline canónico (`docs/catalog/m3ucrawler_pt_canonical_catalog.json`). O `CatalogSeed` criava duplicações para o mesmo canal lógico (`rtp1`/`rtp-1`, `cnn`/`cnnportugal`, `benfica-tv`/`btv`, etc.).
- **`CatalogSeed.Channels` saneado**: removidos canais duplicados pela baseline JSON (RTP 1-3, SIC, TVI, SIC Notícias, RTP Notícias, CNN, CMTV, News Now, Canal 11, V+ TVI, RTP Memória, Porto Canal, Sport TV 1, Eurosport 1, SIC K, Panda, Cartoon Network, Hollywood, Cinemundo, AXN, Discovery, NatGeo, Globo). Mantidos apenas canais não cobertos pela baseline: Sport TV 2-7, Sport TV NBA, TVI 24, TVI Internacional, SIC Mulher, SIC Radical, Baby TV, Canal Panda (key legada), Odisseia, BTV (key alinhada).
- **`CatalogBaselineImporter`**: o alias principal (idêntico ao `DisplayName` normalizado) é agora **persistido automaticamente**. Antes era pulado como "redundante", o que impedia `ResolveAsync("rtp 1")` e `ResolveAsync("cnn portugal")` de funcionarem quando o JSON não os declarava explicitamente.
- **BTV**: key renomeada de `benfica-tv` para `btv` (alinhado com `pt.btv` da baseline). DisplayName actualizado para `BTV` (do JSON). Todos os aliases legados (`benficatv`, `benfica tv`, `btv hevc pt`) preservados.
- **`runtime-data/countries/pt.json` mantido inalterado**: é a configuração operacional do `CountryChannelValidator`, não uma duplicação do catálogo. A R2 não toca neste ficheiro.
- **Testes actualizados**: `ChannelCatalogIntegrationTests` ajustado para usar `Key = "btv"` e `DisplayName = "BTV"`. `CatalogBaselineImporterTests` ajustado para incluir o alias principal automático.
- **Testes adicionados** (`CatalogR2ConsistencyTests.cs`, 13 testes): A–J + bootstrap idempotente + cobertura da baseline + aliases resolvidos.
- **Resultado**: 1357 → **1370 testes** em Release, 0 falhas, 3x runs estáveis. Pré-R2: 56 canais com duplicações. Pós-R2: 46 canais únicos.
- **Não cria nova PHASE**: é saneamento das PHASES 1–10. Documentado como `PHASE-Bridge / R2` em `docs/IMPLEMENTATION_ROADMAP.md` secção 32.14.

### 🛠 PHASE-Bridge / R1 — country gate dentro do ingestion (2026-09-11)
- **Bug crítico fechado**: o `PipelineIngestionService.IngestAsync` aceitava `countryCode` mas não aplicava a política de país. Streams estrangeiros (`ES La 1`, `BR Globo News`, `Sky News`) e streams sem token PT (`CANAL FANTASTICO`) eram persistidos no catálogo canónico, contaminando a playlist final.
- **Nova API**: o construtor de `PipelineIngestionService` agora **exige** um `CountryChannelValidator` (fail-fast no construtor via `ArgumentNullException`).
- **`IngestAsync` chama `CountryChannelValidator.ValidateStreams` internamente** antes de qualquer persistência. Streams REJECTED são silenciosamente descartados.
- **REJECT ≠ UNKNOWN**: streams rejeitados pelo country policy **nunca** são convertidos em `CanonicalChannel.CreateEligible` (boundary arquitectural explícito).
- **`IngestionResult.RejectedByCountryCount`**: novo campo para diagnóstico.
- **`SourceEntity`** é criada antes do gate (audit trail); `ChannelSourceEntity` só é criado se o stream passar o gate.
- **`Program.cs`**: partilha o `CountryChannelValidator` já construído (linha 186) com o `PipelineIngestionService` (linha 198).
- **Testes** (`PipelineIngestionCountryGateTests.cs`, 12 testes): A–J + REJECT≠UNKNOWN. Cobrem estrangeiros, desconhecidos, batch misto, idempotência, fail-fast, e boundary.
- **Regression guard**: `RealPipelineDiagnosticTests` foi actualizado com `Assert.Equal(0, foreignIngested)` — se algum stream REJECTED voltar a aparecer no catálogo, a teste falha.
- **Resultado**: 1345 → **1357 testes** em Release, 0 falhas, 3x runs estáveis. Pré-R1: 27/27 ingested, 8 auto-created. Pós-R1: 21/27 ingested, 3 auto-created, 6 REJECTED.
- **Não cria nova PHASE**: é uma correcção do PHASE-Bridge (R1). Documentado em `docs/IMPLEMENTATION_ROADMAP.md` secção 32.13.

### 🛠 PHASE-Bridge — Pipeline real → Catálogo (2026-09-11)
- **Gap fechado (suportado por código)**: a validação end-to-end anterior tinha identificado que `TelegramScraperService.SearchAndTestM3UInTelegramAsync` e o modo M3U8-search em `Program.cs` **não** escreviam no catálogo persistente. Esta entrega fecha o gap.
- **`PipelineIngestionService`** (`m3uCrawler/Services/Catalog/PipelineIngestionService.cs`): bridge entre `IReadOnlyList<M3uStream>` já testados e o catálogo persistente. Para cada stream: `EnsureSourceAsync` (idempotente por Key) → `ResolveAsync` (mecanismo existente, sem segundo algoritmo de matching) → se Unknown: `EnsureCanonicalChannelAsync` com `CreateEligible` (canal permanece disponível para revisão via Dashboard, nunca eliminado) → `RecordChannelSourceAsync` (idempotente por `(channelId, sourceId, streamUrl)`) → `RecordMatchingAuditAsync`. Proveniência preservada em `SourceEntity.Origin` e `ChannelSourceEntity.MatchMethod`/`MatchConfidence`.
- **`CatalogResolver.EnsureCanonicalChannelAsync`** (novo): upsert idempotente de `CanonicalChannelEntity` por Key. Cria canal com `CreateEligible` se não existir; devolve existente caso contrário. Adiciona alias normalizado se não colidir com outro canal.
- **`TelegramScraperService.SearchAndTestM3UInTelegramAsync`** — dois parâmetros opcionais novos: `pipelineIngestor` (PipelineIngestionService?) e `pipelineSourceKey` (string?). Se fornecidos, o catálogo é alimentado antes do return; falha na ingestão é apanhada e logada, não é fatal (a playlist M3U continua a ser produzida). Construtor alternativo `TelegramScraperService(WTelegram.Client?)` permite testes sem credenciais reais.
- **`Program.cs`**: ao entrar no bloco `--telegram` ou `--telegram-maintain`, inicializa `PipelineIngestionService` (se o catálogo estiver acessível) e passa-o nas duas chamadas `SearchAndTestM3UInTelegramAsync` e em `RunTelegramMaintenanceCycle`. Source key por defeito: `telegram-<slug do termo>`.
- **Compatibilidade**: callers que não passam `pipelineIngestor` (testes legacy, modos não-Telegram) continuam a funcionar — o parâmetro é opcional. `IngestIntoCatalogAsync` é um novo método público separado.
- **Testes TDD** (`m3uCrawler.Tests/PipelineIngestionBridgeTests.cs`, 9 testes, todos passam em Release):
  - **A** — primeira descoberta cria Source + ChannelSource e usa canonical existente;
  - **B** — segunda passagem idêntica não cria duplicados (idempotência);
  - **C** — passagem actualiza Availability quando o stream muda de estado;
  - **D** — canal desconhecido fica com `CreateEligible` e é visível no catálogo;
  - **E** — matching via `CatalogResolver.ResolveAsync` (variantes batem no mesmo canal);
  - **F** — proveniência preservada (`SourceEntity.Origin`, `ChannelSourceEntity.MatchMethod`/`MatchConfidence`);
  - **G** — o caminho real de Telegram chama a bridge (via `TelegramScraperService.IngestIntoCatalogAsync`);
  - **H** — dados ingeridos chegam ao `PlaylistComposerService`;
  - **I** — `RecordChannelSourceObservationAsync` (PHASE 9b) continua a funcionar depois da bridge.
- **Total agora**: 1335 → **1344 testes** em Release (anterior: 1335), 0 falhas, 0 warnings novos. Build em Release: 0 errors, 0 warnings novos.
- **Não criação de PHASE 13**: o trabalho enquadra-se como subfase técnica de fecho das PHASES 1–10, sem introduzir novo conceito arquitectural. Documentado como `PHASE-Bridge` em `docs/IMPLEMENTATION_ROADMAP.md` secção 32.12.

### 🛠 PHASE 12 — fecho da Automation / Scheduler (2026-09-11)
- **Acções concretas `IScheduledAction`** (4 novas, todas reutilizam serviços existentes sem duplicar pipeline):
  - `ScheduledM3uDiscoveryAction` (`discoverM3u`) — `M3uCrawlerService.SearchM3u8Files` + `M3uTesterService.TestMultipleStreams` → `output/playlist.m3u`.
  - `ScheduledValidationAction` (`validatePlaylist`) — re-testa streams em `output/playlist.m3u` via `M3uTesterService.TestM3u8Stream`; remove as que falham.
  - `ScheduledPlaylistGenerationAction` (`generatePlaylist`) — `PlaylistComposerService.ComposeAsync` sobre a primeira `OrderingListEntity` disponível → `PlaylistManagerService.WriteComposedAsync`.
  - `ScheduledDispatcharrSyncAction` (`syncDispatcharr`) — `DispatcharrSyncService.RunAsync`; termina em `dispatcharr-disabled` quando `dispatcharr_enabled=false` (no-op silencioso sem HTTP nem ficheiros extra).
- **`ScheduledAutomationHost`** (`Services/Automation/`): monta um `ServiceProvider` mínimo (DI já existente) que regista as 4 actions e devolve um `ScheduledJobRunner` pronto a arrancar. Expõe `RegisteredActions` para o Dashboard.
- **Arranque em produção**: `Program.cs` constrói o `ScheduledAutomationHost` dentro do bloco `--web`, chama `Start()` e regista `Console.CancelKeyPress` para shutdown limpo. Quando `--web` não é passado, o scheduler não corre (não há segundo mecanismo de scheduling).
- **Dashboard**: novo endpoint `GET /api/scheduled-actions` devolve a lista de actions registadas; o formulário `Scheduled Jobs` troca o input livre por `<select>` com essas opções quando o host está activo. Retro-compatível: sem host, o input livre continua.
- **Testes** (12 novos, todos passam em Release): `ScheduledActionsTests` cobre resolução via DI, idempotência do `Start`, nomes estáveis/distintos, no-op quando Dispatcharr está disabled, no-op sem playlist, propagação de `CancellationToken`, tick do runner para jobs disabled / desconhecidos / existentes / não vencidos. Total agora: **1327 testes** (anterior: 1315).
- **Documentação**: `docs/IMPLEMENTATION_ROADMAP.md` — PHASE 12 passa de `[parcial]` para `[concluído]` (secções 30, 32.1, 32.10 e apêndice actualizadas; nova secção 32.11 regista o fecho de gaps sem apagar o snapshot histórico de 32.10).

### 🔧 Alterado
- **`TelegramScraperService.SearchM3UInTelegramInternal`**: depois de iterar os diálogos e mensagens, invoca o `TelegramPublicationResolver.ResolveAsync` para todas as `TelegramPublicationRef` capturadas. Usa o cache de `chatsDict` (já existente, construído a partir de `_client.Messages_GetAllDialogs()`) para obter `access_hash` por `channel_id`. As contas Xtream promovidas passam a integrar `candidates` no mesmo loop do pipeline M3U/Xtream existente. **Sem nova pipeline, sem duplicação.**
- **`M3uCandidateDetector`**: inalterado. `M3uCandidateDetector` continua a tratar `.html`/`.htm` attachments e URLs genéricas como até aqui.
- **`XtreamPublicationResolver`**: inalterado. Continua a receber HTML bruto e devolver 0..N `XtreamAccountInfo`.

- **Anexos HTML como publicações Xtream (segundo mecanismo, complementar à URL pública)**: além do caminho "URL HTTP pública → HTML publication", o sistema agora reconhece anexos Telegram com filename `.html` ou `.htm` (case-insensitive, e.g. `m3u@host.example_07-09-2026.html`) como candidatos a inspeção. O `M3uCandidateDetector` ganhou `IsHtmlFilename(...)` e um novo ramo em `DetectFromMessage` que emite `CandidatePlaylist { Kind=Attachment, DetectedFrom="html attachment", RequiresContentVerification=true, Content=null }`. O download continua a ser feito por `ProcessAttachmentCandidatesAsync` / `DownloadTelegramDocumentTextAsync` (mesmo mecanismo dos anexos `.m3u`/`.m3u8`). Quando o conteúdo descarregado parece HTML (`<!DOCTYPE` ou `<html`), o **mesmo** `XtreamPublicationResolver.ResolveFromHtml` é invocado — não há um parser novo para anexos vs. URLs. As contas descobertas entram no mesmo pipeline M3U/Xtream existente. Regras de identidade, deduplicação, sanitização e `MEDIA LIST`-não-canal são exactamente as do primeiro mecanismo. **Não altera** `M3uCandidateDetector.DetectFromMessage` para outros paths (`.m3u`, `.m3u8`, `#EXTM3U content`, URLs Xtream).
- **Limitação documentada**: deep links do Telegram do tipo `https://t.me/c/<channel_id>/<message_id>` **não são resolvidos nesta iteração**. A investigação técnica confirmou que a WTelegram API (`Messages_GetMessages` com `InputMessage{Id, Peer=channel_peer(id)}`) suporta esta funcionalidade, mas a implementação foi explicitamente diferida para uma iteração dedicada para evitar misturar dois caminhos muito diferentes (URL pública vs. resolução de mensagem autenticada) e para preservar a sanidade do limite de profundidade. Se uma mensagem contiver um link `t.me/c/...` ele é tratado como URL HTTP genérica (sem cards Xtream → "no xtream cards found").
- **Testes** (25 novos, todos passam): detector (8) + integração do pipeline de attachment (7) + regressão (10). Total agora: **1132 testes**.
- **Documentação** (`m3uCrawler/README.md`): nova subsecção "Anexos HTML (`m3u@host.html`) — segundo mecanismo" dentro de "Publicações HTML com cards Xtream"; nova subsecção "Limitação conhecida: links `t.me/c/<channel>/<message>`"; diagrama de pipeline actualizado para mostrar os dois pontos de entrada para o mesmo resolver.
- **Descoberta de contas Xtream a partir de publicações HTML (Telegram → HTML → cards Xtream)**: quando uma mensagem Telegram contém um URL `http(s)` genérico (sem pista `playlist|m3u|iptv|list|xtream|channel|canal|live|getplaylist` no path/query) que aponta para uma página HTML com uma ou várias "cards" Xtream (`Host`/`User`/`Pass`/`M3U`/`EPG`/`Expires`/...), o sistema:
  - **NÃO** altera `M3uCandidateDetector` (permanece como está: sem heurística para URLs genéricas, evita downloads indiscriminados).
  - Em `TelegramScraperService.ExtractRemainingHttpUrls`, extrai URLs HTTP/HTTPS do texto que **não** foram capturadas pelo detector nem são URLs Xtream servidor indirectamente consumidas, e adiciona-as como `CandidatePlaylist { DetectedFrom = "xtream publication url", RequiresContentVerification = true }`.
  - No `for` principal do `SearchAndTestM3UInTelegramAsync`, quando o conteúdo HTTP descarregado não começa por `#EXTM3U` mas parece HTML (`<!DOCTYPE` ou `<html`), invoca o novo `XtreamPublicationResolver.ResolveFromHtml(html, sourceUrl)`.
  - O resolver (`Services/XtreamPublicationResolver.cs`) é puro (sem I/O), usa `HtmlAgilityPack` (já dependência), e devolve 0..N `XtreamAccountInfo`. Estratégia de segmentação por ordem de preferência: `<hr>` no body → `<div|section|article|li class='card'>` → `<tr>` de `<table>` (>=2 linhas) → body como fallback. Extrai pares `label:value` tolerantes a capitalização (`Host`/`HOST`/`host`), espaços (`Max Connections`), HTML entities (`&amp;`, `&lt;`), `<a href>` (captura o `href` em vez do texto do link) e labels alternativos (`Server`, `Username`, `Password`).
  - Apenas produz contas com **Host + User + Pass** presentes; cards incompletas são descartadas silenciosamente.
  - **Identidade lógica = `scheme://host:port/username`** (normalizado, case-insensitive em scheme/host; **password nunca participa**). Múltiplas contas no mesmo servidor com usernames diferentes permanecem como **fontes independentes**; contas repetidas colapsam para uma única (deduplicação determinística por `LogicalIdentity`).
  - Cada conta é promovida a `CandidatePlaylist { Url = acc.M3uUrl ?? BuildXtreamPlaylistUrl(acc), DetectedFrom = "xtream publication", RequiresContentVerification = true }` e re-entra no **mesmo loop** do pipeline M3U/Xtream existente (`AnalyzePlaylist` → `M3uParserService.Parse` → `FilterStreamsByCountry`/`ValidateStreams` → `TestStreamsAsync` → `RunReport`). **Não há uma segunda pipeline paralela**.
  - `BuildXtreamPlaylistUrl` **não** cria uma segunda implementação de `get.php`: sintetiza uma URL de servidor `/live/USER/PASS/0.ts` e delega em `M3uCandidateDetector.ResolveXtreamPlaylistUrl` (única forma canónica).
  - Páginas HTML sem nenhuma card Xtream válida geram um único registo sanitizado em `RunReport.RejectionReasons` mas **não** incrementam `PlaylistsInvalid` (a página existe; simplesmente não é uma publicação Xtream).
  - **Limitação conhecida**: a "MEDIA LIST" presente em muitas publicações (`MEDIA LIST\nPORTUGAL\nSIC\nTVI\nRTP\n...`) nunca é tratada como fonte de canais. O resolver só aceita labels semânticos. A lista real de canais/vod/séries continua a vir da ingestão real (`get.php`/`m3u_plus`) através do pipeline M3U/Xtream.
- **`XtreamAccountInfo`** (`Models/XtreamAccountInfo.cs`): DTO intermediário, `internal sealed`, transporta `Host/Port/Scheme/Username/Password` + `M3uUrl/EpgUrl/ExpiresAt/MaxConnections/...` + `SourcePublicationUrl/SourceTelegramMessageId/SourceTelegramChannel` entre o resolver e o ponto de promoção a `CandidatePlaylist`. Password existe como propriedade mas o tipo nunca é serializado (`ToString()` apenas com `endpoint` e `user`; nenhuma chamada a `JsonSerializer.Serialize` o recebe). Não há persistência nesta fase.
- **Testes** (39 novos, todos passam): `XtreamPublicationResolverTests` (26) + `XtreamPublicationResolverSanitizationTests` (4) + `TelegramPublicationFanOutTests` (9). Cobrem: parsing tolerante, M3U/EPG com `<a href>`, deduplicação determinística (múltiplas contas no mesmo servidor independentes, repetições colapsadas, password nunca na identidade), `MEDIA LIST` ignorada, Host/User/Pass faltantes, HTML inválido/vazio, sanitização (password nunca em consola/ToString/LogicalIdentity, tipo interno), fan-out Telegram (extração de URLs restantes, promoção de conta com `DetectedFrom = "xtream publication"`).
- **Documentação** (`m3uCrawler/README.md`): nova subsecção "Publicações HTML com cards Xtream" sob "Descoberta no Telegram (independente de keyword)" com a estratégia completa; diagrama de pipeline actualizado com a ramificação HTML → `XtreamPublicationResolver`; nova entrada na árvore `Models/` e `Services/`.

- **Pending Country Approvals** (esta iteração): nova funcionalidade de aprovação manual de canais que geraram dúvida no country-level targeting. Quando um stream tem indicadores de país (e.g. "PT" no título) mas não corresponde a um canal canónico conhecido, é adicionado a uma lista de pendentes. O utilizador pode:
  - **Aprovar**: cria uma `IdentityRule` com `ReviewOnly` que permite fuzzy matching futuro.
  - **Reprovar**: cria uma `IdentityRule` com `Excluded` que bloqueia o canal permanentemente.
  - A tabela `pending_country_approvals` no SQLite (`/data/channel-catalog.db`) regista: `NormalizedIdentity`, `OriginalTitle`, `CountryCode`, `StreamUrl` (sanitizada), `SourceGroup`, `ReasonSignature`, `State` (Open/Approved/Rejected).
  - Endpoint `GET /api/catalog/pending-country-approvals` lista todos; `POST /api/catalog/pending-country-approvals/{id}/approve` aprova; `POST /api/catalog/pending-country-approvals/{id}/reject` reprova.
  - Dashboard: novo separador "Pending" no catálogo com badges de contagem e operações de Approve/Reject.
- **Catálogo persistente SQLite (EF Core + migrations)** (esta iteração): substitui o `ChannelCategoryLookup.Contains()` como autorização para criar canais. A BD vive em `/data/channel-catalog.db` em produção (mesmo directório de `wtelegram.config` / `session.dat`, montado como bind-mount do container). Migrations aplicam-se idempotentemente no arranque. Backup automático antes de migrations destrutivas. Seed versionado em `Services/Catalog/CatalogSeed.cs`. Sem nova dependência externa (sem Redis, sem EF fora deste catálogo, sem nova BD). Seed inclui Benfica TV (com aliases `btv`, `btv hevc pt`, `benficatv`, `benfica tv`, …) e **`Sport TV NBA` como canal canónico autónomo** (`sport-tv-nba`, `CreateEligible`, aliases `sport tv nba`, `pt sport tv nba`, `sport tv nba hevc pt`).

### 🔧 Alterado
- **`ContentClassifier` deixa de decidir NewChannel**: o `ChannelCategoryLookup` (em memória, 126 entradas) continua a existir apenas para decidir `EditorialCategory` (compatibilidade). A autorização para criar canais (`NewChannel`) é agora lida exclusivamente do catálogo persistente via `CatalogResolver.ResolveAsync(normalized)`. Quando o catálogo não está activo, o matcher cai no modo legado (`ChannelCategoryLookup.Contains`).
- **`ChannelMatcher.BuildPlanAsync` é agora async**: o método `BuildPlan(...)` sync foi preservado como shim que delega em `BuildPlanAsync(...)` via `GetAwaiter().GetResult()`. Testes existentes e callers sync não mudam. Quando o catálogo está activo, o tier da bucket é decidido pelo `CatalogResolution` (`Canonical+CreateEligible → Curated`, `Canonical+MergeOnly/ReviewOnly → Unknown`, `Rule ReviewOnly → review-required`, `Rule Excluded → excluded`, `Unknown → Unknown tier`). O bucket identity usa o `CanonicalKey` do catálogo (ex.: `btv hevc pt` resolve para `benfica-tv`).
- **Política tri-state de `FindUnknownMatch`** preservada: 0 candidatos → `no-exact-or-alias-match`, 1 candidato → `unknownMatchedToExisting`, 2+ candidatos → `ambiguous-exact-or-alias-match`. O catálogo não muda esta regra; apenas acrescenta o caminho `IdentityRule ReviewOnly` que também produz `no-exact-or-alias-match`.
- **Removed `ChannelKind.Group` e `ChannelKind.Category`**: nunca emitidos; declarações órfãs removidas do enum.
- **`SPORT TV NBA` agora é canal canónico autónomo**: a entrada deixou de ser `IdentityRule ReviewOnly` (que a impedia de criar canal) e passou a `CanonicalChannel` com `PublicationPolicy = CreateEligible`. Aliases cobrem as 4 variantes do brief: `SPORT TV NBA`, `PT: SPORT TV NBA`, `PT SPORT TV NBA`, `SPORT TV NBA HEVC PT`. Em `FindCuratedMatch`, o score contra "Sport TV 1..7" fica ≈67 (token-set ratio, abaixo do threshold 80), portanto **nunca** faz fuzzy-match com outros canais Sport TV. Se já existir um canal "Sport TV NBA" externo/desconhecido em Dispatcharr, a stream do crawler entra em merge-only (anexada como `NewStream`); streams externas nesse canal são protegidas pelo filtro de ownership (ver §6 abaixo) e nunca são removidas.
- **Filtro de ownership no `ChannelMatcher.BuildExistingDecision`** (defesa em duas camadas — camada 1): o `ChannelMatcher` consulta agora o `StreamOwnership` (em batch, uma única query `_catalog.GetStreamOwnershipMapAsync`) antes de emitir `SyncOutcome.Removed`. Streams com `Ownership = External` ou `Unknown` (ou sem registo, com catalog activo) são reclassificadas como `SyncOutcome.ExistingUnchanged` com `OrderReason = "protected-by-ownership"`. O counter `SyncReportCounts.ProtectedExternalStreams` regista o total. Sem catalog (legacy mode), todas as streams caem no fallback `CrawlerManaged` (comportamento histórico preservado para não regredir testes pré-catalog).
- **Filtro de ownership no `DispatcharrSyncService.ApplyAsync`** (defesa em duas camadas — camada 2, **salvaguarda redundante**): mesmo que um plano inválido contenha `SyncOutcome.Removed` para uma stream protegida, a fase de aplicação re-consulta o ownership antes de emitir DELETE. Streams sem registo na BD são tratadas como `Unknown` (default seguro) → nunca DELETE. Sem catalog, fallback `CrawlerManaged` (legacy). Console regista `🛡️ Ownership guard: stream {id} ({ownership}) mantida ...` quando拦截.

### 🐛 Corrigido
- **`BTV HEVC PT` criava canal `BTV HEVC PT`** em vez de anexar como `Benfica TV`. Com o catálogo, `btv hevc pt` resolve para `benfica-tv` (canonical key) e o bucket identity passa a ser `benfica-tv`. Se já existir um canal `Benfica TV` em Dispatcharr (ownership = External ou CrawlerManaged), a stream entra em modo merge-only sem remover nem renomear.
- **`PT: SPORT TV NBA` criava canal** com nome `pt sport tv nba` (com prefixo PT cru). Agora é canal canónico autónomo `Sport TV NBA` (`sport-tv-nba`, DisplayName limpo), distinto de Sport TV 1..7.
- **Sincronização removia 61 streams externas**. As streams `Ownership = External` ou `Ownership = Unknown` nunca geram `SyncOutcome.Removed`. Apenas streams com `CrawlerManaged` (criadas por um sync anterior) podem ser removidas em sincronizações subsequentes. Defesa em duas camadas (matcher + ApplyAsync) para garantir que **nenhum DELETE HTTP** é emitido para streams protegidas, mesmo em planos inválidos.

### 📦 Deployment
- **Produção não alterada nesta iteração**: a nova correcção fica em branch dedicado para revisão. Aplicar a produção é uma iteração separada.

### ✨ Adicionado
- **Segurança de Unknown → matching (commits `c3e0e4f` → `b3f2a1e`)** (esta iteração): uma entrada `Unknown` nunca pode usar fuzzy matching para anexar streams a um canal existente. A política de 3 níveis foi subdividida em dois **tiers** (Curated / Unknown) com bucket storage separado:
  - **Curated** (`Channel`): fuzzy + alias + threshold (80). Pode produzir `NewChannel`.
  - **Unknown**: apenas match por **igualdade normalizada** ou **alias explícito**. Pode anexar streams a um canal existente (`ExistingReassigned`/`ExistingUnchanged`); nunca produz `NewChannel`. Se não houver match exacto/alias → `UnknownReviewRequired`.
  - O bucket storage é keyed por `(BucketTier, Identity)`: streams da mesma identidade normalizada em tiers diferentes (e.g. SIC curado + SIC XYZ Unknown) ficam em buckets separados e produzem decisões independentes. A ordem de chegada das streams não pode fazer com que uma stream `Unknown` seja promovida a `NewChannel` ou que uma stream curada seja anexada por fuzzy a um canal diferente.
- **Testes de regressão** (7 novos): `UnknownExactMatchOnlyTests` cobre (a) `Fox Sportz` (typo) nunca anexa a `Fox Sports` via fuzzy; (b) `Meo TV` (igualdade exacta) anexa; (c) alias explícito `MEO → Meo TV` anexa; (d) ausência de alias não fuzzy-matcha; (e) permutação de ordem entre streams curated e Unknown da mesma identidade produz NewChannel único para a curada; (f) `RTP NOTICIAS` (curado) pode fuzzy-matchar `RTP 1` mas `RTP N` (Unknown) nunca.
- **Determinismo de exact/alias match para Unknown** (commit `b3f2a1e`): `FindUnknownMatch` recolhe **todos** os candidatos exactos/aliases antes de decidir. A política é estritamente:
  - 0 candidatos → `UnknownReviewRequired` com `no-exact-or-alias-match`.
  - 1 candidato → `unknownMatchedToExisting` (anexa).
  - 2+ candidatos → `UnknownReviewRequired` com `ambiguous-exact-or-alias-match`.
  - Nunca escolhe `First()` ou equivalente para `Unknown`. O novo tipo `UnknownMatch` (tri-state: `NoMatch` / `Unique` / `Ambiguous`) e o helper `RecordUnknownAmbiguous` formalizam este contrato.
- **Teste de tier collision com alias partilhado** (commit `b3f2a1e`): com `aliasMap["SIC XYZ"] = "SIC"`, ambas as streams `SIC` (curated) e `SIC XYZ` (Unknown) resolvem para o mesmo identity `sic` em buckets de tier distintos. Executa-se as permutações `[SIC, SIC XYZ]` e `[SIC XYZ, SIC]`. Sem canais existentes: NewChannel único (curated) + UnknownReviewRequired (SIC XYZ), contagens e decisões idênticas. Com canal existente SIC: ambos anexam via alias exact-identity, reconciliação produz uma única decisão para `existingChannelId=700`.
- **Política documentada de `Foreign`**: `ChannelKind.Foreign` é emitido **apenas** quando o source group é estrangeiro em `GroupTaxonomy` E o título não é uma identidade curada. Títulos curados em grupos estrangeiros são `ChannelKind.Channel` e o carácter estrangeiro é expresso como `OutputGroupKind.Foreign` no `ResolutionPolicy`.

### 🔧 Alterado
- **Removidos `ChannelKind.Group` e `ChannelKind.Category` do enum** (esta iteração): nenhum dos dois era emitido por regra alguma — eram declarações órfãs que confundiam a taxonomia pública. As 9 classificações activas são agora: `Channel`, `Bundle`, `Vod`, `LiveCam`, `Foreign`, `Placeholder`, `Unknown`. `ChannelClassification.Group` static field também removido.
- **XML-doc alinhado com o comportamento real** em `ContentClassifier.Classify`: precedência revista, semântica de `Foreign` clarificada, regra de Unknown reforçada para exigir match exacto ou alias (nunca fuzzy). Removidas referências a "Group" e "Category" no doc.

### 🐛 Corrigido
- **Política de 3 níveis (exclude / match-existing / create-new)** (commits `3a4af81` → `3f4af81`, branch `fix/docker-buildinfo-metadata`, PR #2): `ContentClassifier.Classify` corre antes do bucket de canais, separando as decisões "ser não-canal" e "criar NewChannel". A política de 3 níveis (exclude / match-existing / create-new) garante que `Bundle / Vod / LiveCam / Placeholder` nunca chegam a bucket, que entradas `Unknown` sem identidade curada são marcadas como `UnknownReviewRequired` e nunca geram `NewChannel` (a entrada `PT - NO EVENT` na fixture de produção tem 8 ocorrências confirmadas neste caminho), e que canais reais legítimos fora do dicionário curado continuam a receber streams novas via `ExistingReassigned` quando existem em Dispatcharr. O `SourceGroupCategoryLookup` é agora consumido pelo classificador para detectar source-groups editoriais (Live/Entretenimento/etc.). Endpoint `GET /api/classification-summary` no dashboard expõe as contagens por disposição (`excluded`, `unknownMatchedToExisting`, `unknownReviewRequired`, `newChannelsFromCuratedIdentity`) e a amostra sanitizada.
- **Fronteira explícita Classification → Matching** (commits B1.2-FIX2 → esta iteração): `ContentClassifier.Classify(title, sourceGroup)` corre **antes** do bucket de canais, e devolve um `ChannelKind` explícito. Apenas `ChannelKind.Channel` produz uma `ChannelDecision`. As outras kinds (`Group`, `Bundle`, `Vod`, `LiveCam`, `Category`, `Foreign`, `Placeholder`, `Unknown`) são contabilizadas como `ClassifiedExclusion` (com `title`, `group`, `kind`, `reason`; **sem** URL nem credenciais) e nunca chegam a `NewChannel`. A precedência é determinística e documentada no xmldoc do classificador:
  1. Título vazio → `Unknown`.
  2. Colour-placeholder (`#f#...`) → `Placeholder`.
  3. Identidade conhecida em `ChannelCategoryLookup.Contains` → `Channel` (a identidade tem prioridade sobre sinais de source-group).
  4. `ContentTypeDetector` VOD ou PPV → `Vod`.
  5. Source-group 24-7 (`canais 24-7`) ou título com `24/7`/`24-7` → `Bundle`.
  6. `PACK`/`BUNDLE` no título → `Bundle`.
  7. Source-group com prefixo `VOD |` → `Vod`.
  8. Source-group com `PPV`/`BETCLIC`/`LIGA PORTUGAL` (cobre `PT - NO EVENT`) → `Vod`.
  9. `LiveCam` no título → `LiveCam`.
  10. Source-group estrangeiro em `GroupTaxonomy` → `Foreign` (apenas para títulos não-classificados como canal).
  11. Caso contrário → `Unknown`.
- **Contadores por `ChannelKind`** (`SyncReportCounts.Classification: Dictionary<string,int>`): chave = nome do enum (`Channel`, `Bundle`, `Vod`, `LiveCam`, `Foreign`, `Unknown`, `Placeholder`), valor = contagem. Serializado como `classification` no `MatchPlan` / `dispatcharr_plan_*.json` / `dispatcharr_report_*.json`.
- **`MatchPlan.ClassifiedExclusions`**: lista sanitizada de entradas rejeitadas pela classificação (sem URL nem credenciais). Serializado como `classifiedExclusions`.
- **`ChannelCategoryLookup.Contains`** (novo método público): distingue "identidade conhecida" do fallback `Category.Live` retornado por `Lookup`. É a fonte primária de verdade do classificador — é a única via para promover uma entrada a `Channel` com `NewChannelEligibility=true`. Identidades desconhecidas podem ainda ser comparadas contra canais existentes em Dispatcharr (com `ExistingMatchEligibility=true`), mas nunca geram um `NewChannel` automaticamente.
- **Dashboard**: novo endpoint `GET /api/classification-summary` lê o `dispatcharr_plan_*.json` mais recente e devolve `classification`, `excludedCount`, e uma amostra (até 50) de `ClassifiedExclusion` (sem credenciais). Reutiliza `MatchPlanSerializer`.
- **Testes de classificação** (38 novos): `ContentClassifierTests` cobre SIC/RTP/TVI/CMTV/Sport TV/CNN/SIC NOTICIAS/RTP NOTICIAS como `Channel`; `Filmes`, `Combates`, `SPORT TV PACK`, `PACK`, `MEGA BUNDLE` como `Bundle`; `PT - <título> - <ano>`, `PT - NO EVENT` (regressão real), `VOD | PORTUGAL`, `PPV/BETCLIC` como `Vod`; `LiveCam` como `LiveCam`; colour placeholders como `Placeholder`; unknown strings como `Unknown`. Inclui teste de integração end-to-end com a fixture de produção `m3ucrawler_playlist_20260831_223914.m3u` (`ChannelClassifierRegressionTests`) que prova que `PT - NO EVENT` e `Filmes 24/7` não aparecem em `plan.Channels` como `NewChannel`.
- **Teste de serialização round-trip** (`WebDashboardServiceTests.MatchPlan_serialization_round_trips_classification_exclusions`): confirma que `Classification` e `ClassifiedExclusions` sobrevivem a `Serialize`/`Deserialize` em camelCase.

### 🐛 Corrigido
- **Discrepância OCI labels ↔ `/api/version`**: o `/api/version` da imagem publicada devolveva `1.0.0 / unknown / 0 / 1970-01-01` enquanto os OCI labels mostravam o `commit` correcto. Causa: o Dockerfile (context `./m3uCrawler`) não incluía `Directory.Build.props`/`Directory.Build.targets` no contexto Docker, pelo que MSBuild aplicava os defaults do SDK NuGet. Resolvido em A1/B1.2.
- **`PT - NO EVENT` (8 ocorrências na fixture real)** era criado como `NewChannel` em produção. Agora é classificado como `Vod` (regra 8: BETCLIC/LIGA PORTUGAL) e excluído do matching.
- **Title="Filmes Batman 24/7"** continuava a ser classificado como canal mesmo após o bundle-guard regex legacy. O classificador cobre tanto o source-group 24-7 (`canais 24-7` regex) como o título com `24/7`/`24-7`.
- **`docker build` em branch push** (commit `d42310a`): `refs/heads/<branch>` era convertido para `<branch>` (não SemVer válido) e abortava o `dotnet restore` com `'<branch>' is not a valid version string`. Substituído o `sed` por um `case` que normaliza `refs/heads/*` para `0.0.0-dev-<short-sha>` (SemVer válido e único).
- **Step "Resolve published digest" do workflow GHCR** (commit `0797b40`): usava `curl` contra `ghcr.io` com `GITHUB_TOKEN`, que tem `packages:write` mas não `read:packages`, devolvendo 401 silenciosamente. Substituído por `docker buildx imagetools inspect --raw | jq '.manifests[] | select(.platform.architecture=="amd64" and .platform.os=="linux") | .digest'` (autenticado via `docker/login-action@v3`); fallback single-arch via `sed` no header `Name:`. Validado end-to-end: digest reportado pelo step bate certo com `docker buildx imagetools inspect` no servidor.
- **`--web --web-port 5001` standalone** (B1.2-FIX2, pré-existente): o parsing de `--web`/`--web-port`/`--web-token` estava gated por `if (args.Contains("--telegram"))`, fazendo `--web` sozinho cair no M3U8-search onde `5001` era interpretado como search term. Hoist do bloco para o top-level de `Main`; quando `--web` standalone está activo, o processo aguarda `webTask` em vez de cair no M3U8-search.

### 📦 Deployment
- **Produção live em `sha256:a29bbfe1cf84d2db3411d5713986215d9bf8c062d71b859b4844caf483c1d4a6`** (commit `e0f62e2`, build 53, `commit`/`buildDate` coerentes com OCI labels `revision`/`created`).
- **PR #1** merged: `fix/docker-buildinfo-metadata` → `main`, 4 commits (`3a338d8`, `d42310a`, `e0f62e2`, `0797b40`), todos os checks verdes.

### 📊 Estado
- Build: `dotnet build m3uCrawler.sln --configuration Release` → **0 warnings, 0 errors**.
- Testes: `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo` → **1211 testes passados, 0 falhados, 0 skipped**.
- actionlint: **0 errors, 0 warnings**.
- Imagem rollback preservada: `sha256:27b0b18dd81e9c01416bad674cfbe86515a9994be4565bb44dda49fb2e50b97e`.
- **Validação em produção** (servidor `192.168.68.142`, container `m3ucrawler:sha-4df856e`, commit `4df856e`): single-cycle com `--history-hours 24` descobriu 10 contas Xtream distintas do HTML da msg `110705` (canal `1635952193`), testou streams funcionais em `neorcqds.top:8080`, e integrou canais portugueses na `playlist.m3u` final. `telegram_run_report.json` contém `discoveredPlaylists` com URLs sanitizadas (`username=***&password=***`).

### ✨ Adicionado (PHASE 1 — Canonical Catalogue)

- **Catálogo canónico baseline**: `docs/catalog/m3ucrawler_pt_canonical_catalog.json` adicionado ao repositório. `catalog_id=pt-canonical-tv`, `version=1.0`, `country=PT`. Contém `numbering` (RTP1=1, RTP2=2, SIC=3, TVI=4, …), `groups` (16, incluindo 8 grupos de TV/Rádio e 5 de VOD com `range=null`), `dashboard_options` (`import_vod=true`, `prefer_hd=true`, `deduplicate_channels=true`, `normalize_groups=true`, `preserve_source_numbers=false`, `keep_vod=true`) e `matching.examples` (≥20 canais PT: `rtp1`, `rtp2`, `sic`, `tvi`, `sic-noticias`, `cnn-portugal`, `sporttv1`, `eurosport1`, `btv`, `hollywood`, `natgeo`, `globo`, etc.). É um **artefacto versionado** — não substitui a persistência SQLite.
- **`CatalogBaselineImporter`** (`m3uCrawler/Services/Catalog/CatalogBaselineImporter.cs`): importa o JSON para `CanonicalChannelEntity` + `ChannelAliasEntity` de forma **idempotente** (sem duplicações), **aditiva** (não destrói canais pré-existentes) e **auditável** (`CatalogBaselineImportReport` com `ChannelsCreated/Updated`, `AliasesAdded/Skipped`, `Warnings`). Converte `canonical_id` (`pt.rtp1`) em `Key` interno (`rtp1`), removendo prefixo país ISO-like. Resolve `EditorialGroup` e `EditorialCategory` heurísticamente. Filtra aliases idênticas ao `DisplayName`.
- **Integração no `ChannelCatalogBootstrapper`** (`TryImportBaselineAsync`): chamada automaticamente após `SeedAsync`. Procura o baseline em três localizações canónicas (env var `M3U_BASELINE_PATH`, `CWD/docs/catalog/`, `AppContext.BaseDirectory` com subida relativa). Falha na importação é registada em log mas **não aborta** o arranque.
- **Testes** (`m3uCrawler.Tests/CatalogBaselineImporterTests.cs`): 19 testes cobrindo conversão de `canonical_id`, resolução de grupo/categoria, idempotência, preservação de canais pré-existentes, actualização de `DisplayName`, sanitização do report, round-trip JSON, integração com o ficheiro baseline real.
- **Operação esperada em produção**: primeiro arranque com baseline disponível adiciona ~70 canais e ~150 aliases ao seed programático (`CatalogSeed` = 36 canais). Arranques seguintes são zero-operação (idempotência).

## [0.1.0] - 2026-09-02

### ✨ Adicionado
- **Pipeline de classificação editorial OutputGroupKind**: 9 valores (PortugalLive, PortugalVOD, PortugalFilmes24-7, PortugalEntretenimento, PortugalDesporto, PortugalInfantil, PortugalDocumentarios, PortugalPPV, Foreign) expostos como metadata não-actuante em `ChannelDecision.OutputGroup`. Componentes determinísticos em `Services/Matching/` (`GroupTaxonomy`, `GroupNormalizer`, `GroupResolver`, `ChannelCategoryLookup`, `ContentTypeDetector`, `SourceGroupCategoryLookup`, `ResolutionPolicy`).
- **`CountryChannelValidator.IsTargetCountry`** (default true) em `CountryStreamMatch` para distinguir PT-flagged streams dos demais sem ambiguidade.
- **`SyncReportCounts.OutputGroups`** (`IReadOnlyDictionary<string,int>`) agrega a contagem por OutputGroupKind para visibilidade editorial em `dispatcharr_report_*.json` (chave `outputGroups`).
- **`MatchPlanSerializer.SanitizeForSerialization`** preserva `OutputGroup` no JSON sanitizado.
- **Política de versionamento SemVer (0.x em desenvolvimento)**: `Directory.Build.props` como fonte canónica de versão + `Directory.Build.targets` para resolver metadata de build (`git rev-parse --short=12 HEAD`) e re-compor `InformationalVersion` no formato `<semver>+sha.<commit>+build.<n>+date.<iso>`. Tolerância a ausência de git (fallback `unknown`) e override via `dotnet build -p:M3uCrawlerVersion=...`.
- **`m3uCrawler.Build.BuildInfo`** expõe em runtime `Application`, `Version`, `Commit`, `BuildNumber`, `BuildDate`; `OverrideForTesting` e `ResetForTesting` para a suite. Sem fontes manuais duplicadas em runtime.
- **`m3uCrawler --version`** (alias `-V`) imprime `m3uCrawler <Version> (<Commit>, build <Build>, <BuildDate UTC>)` e termina com código 0, fora do fluxo principal.
- **Endpoint `/api/version`** no dashboard devolve `{ application, version, commit, build, buildDate }` em camelCase.
- **CI baseline** (`chore(ci)`): workflow `ci.yml` em pushes/PRs a `main` — checkout, setup .NET 9.0.x, cache de pacotes NuGet, restore, build Release e test Release. Sem tags, sem release, sem publish nesta fase.

### 🐛 Corrigido
- **SourceRevision em `InformationalVersion`**: o .NET 8+ SDK concatenava automaticamente o SHA completo a `InformationalVersion`. Com `IncludeSourceRevisionInInformationalVersion=false` e re-composição explícita no nosso target, o valor torna-se determinístico e bem-formado.

## [0.1.1] - 2026-09-03
### ✨ Adicionado
- **Pipeline Telegram completo**: descoberta de candidatos → aquisição de conteúdo → detecção M3U → parsing → validação por país → extracção de streams → teste de streams → relatório.
- **`M3uCandidateDetector`**: descoberta independente de keyword (URLs `.m3u`/`.m3u8`, anexos, conteúdo `#EXTM3U`, URLs plausíveis sem extensão).
- **`CandidatePlaylist`**: modelo de candidato com `RequiresContentVerification` para URLs sem extensão sujeitos a verificação de conteúdo.
- **`M3uParserService`**: parser M3U centralizado que preserva `OriginalExtInf`, `Title`, `Group`, `Logo` e distingue playlists de canais de master HLS.
- **`AnalyzePlaylist` (CountryChannelValidator)**: validação por país baseada nos **títulos `#EXTINF`**, com **threshold 3**, famílias canónicas, variantes colapsadas (`RTP1` ≡ `RTP 1`) e protecção contra falsos positivos de aliases curtos.
- **`RunReport`**: relatório detalhado da execução (`MessagesAnalyzed`, `CandidatesFound`, `PlaylistsDownloaded`, `PlaylistsInvalid`, `CountryMatches`, `PlaylistsRejected`, `ChannelsRecognized`, `StreamsExtracted`, `StreamsTested`, `StreamsWorking`, `StreamsFailed`, `RejectionReasons`, `DiscoveredPlaylists`).
- **`telegram_run_report.json`**: persistido em `output/` em ambos `--telegram` e `--telegram-maintain`.
- **Dashboard**: novos endpoints `/api/run-report` e `/api/discovered-playlists`; secções de diagnóstico da última execução e de últimas playlists descobertas.
- **Alinhamento do `/api/country/validate`** com o critério rigoroso do pipeline (`AnalyzePlaylist`, threshold 3, `recognizedChannelCount`, `threshold`).
- **Suporte a Xtream Codes**: `M3uCandidateDetector` reconhece URLs Xtream de servidor (`/live/USER/PASS/...`) — resolvendo-as para `get.php?username=...&password=...&type=m3u_plus` — e URLs de playlist Xtream (`get.php?type=m3u_plus`). Ambos entram no mesmo pipeline (`RequiresContentVerification` + gate `#EXTM3U`), sem segunda pipeline paralela e sem depender de keyword.
- **`CredentialSanitizer`**: sanitiza URLs com credenciais (Xtream `user:password@`, `username/password/token` em query, segmentos `user/pass` em `/live/`/`/movie/`/`/series/`) para `***`. Garantia de que passwords **nunca** aparecem em logs, `RunReport` (`DiscoveredPlaylists.Name`, `RejectionReasons`) ou dashboard.

### 🔧 Melhorado
- **`--telegram-maintain`** preserva `playlist.m3u` quando não existem novos candidatos (`MergeStreams` com `freshStreams` vazio devolve `stillWorkingMain`).
- Descoberta de playlists com anexo cujo filename é `.m3u`/`.m3u8` ou cujo conteúdo começa por `#EXTM3U` (mesmo sem extensão).
- URLs HTTP plausíveis sem extensão (`/getplaylist?...`) são inspeccionados e só avançam se o conteúdo HTTP for `#EXTM3U`.
- `ImportHistoryEntry` inclui métricas de discovery (mensagens, candidatos, playlists, streams).
- Documentação (`m3uCrawler/README.md`, `CHANGELOG.md`, `README.md` raiz) actualizada para reflectir o estado real.

### 🐛 Corrigido
- Falso positivo de aliases curtos: `SIC`/`TVI` já não correspondem a `basics`/`atvinew` (matching por tokens, não por `Contains` sobre o conteúdo bruto).
- Duplicação de parsing M3U (centralizado em `M3uParserService`; removido `ExtractM3u8FromTelegramDocumentAsync`).
- `--telegram-maintain` sem novos candidatos não apaga streams existentes.
- **Credenciais Xtream em logs**: a implementação anterior imprimia servidor, username e password no terminal; agora toda a representação destinada a logs/relatórios/dashboard é sanitizada (`CredentialSanitizer`) e o pipeline integra Xtream de forma segura.
- **Credenciais em consola (tester e listagem de streams)**: `M3uTesterService` e `Program.cs` (listagem de streams funcionais e templates de scan-domain) usam `CredentialSanitizer.SanitizeUrl` em todas as impressões, de modo que passwords Xtream nunca aparecem no terminal.
- **Credenciais em JSONs de relatório/diagnóstico**: `PlaylistManagerService.SaveToJsonReport` sanitiza o `Url` de cada stream antes de serializar; a playlist M3U funcional (`SaveToM3uPlaylist`) preserva as URLs reais para reprodução.
- **Credenciais no dashboard**: a pré-visualização HTML usa `GET /api/playlist/preview` e `GET /api/playlist_temp/preview` (sanitizados via `CredentialSanitizer.SanitizeM3uContent`); os endpoints funcionais `/api/playlist` e `/api/playlist_temp` mantêm-se para download explícito.
- **Distinção explícita artefacto funcional vs diagnóstico**: a playlist M3U funcional contém URLs reais (necessárias para Xtream); logs, JSONs de relatório, `RunReport` e pré-visualização do dashboard são sempre sanitizados.
- **Acesso ao dashboard**: por defeito o `HttpListener` escuta em todas as interfaces sem autenticação — qualquer pessoa na rede podia obter a playlist Xtream funcional via `GET /api/playlist`. Adicionada protecção opcional por token partilhado (`--web-token`): quando configurado, todos os endpoints (incluindo `/api/playlist*`) exigem `Authorization: Bearer <token>` ou `?token=<token>` (comparação em tempo constante, `401` caso contrário). Sem token configurado, o comportamento mantém-se aberto para compatibilidade com uso local.
- **Grupos Dispatcharr com nome duplicado (case-insensitive)**: o `ChannelMatcher` (`Services/Matching/ChannelMatcher.cs:68`) lançava `ArgumentException: An item with the same key has already been added. Key: portugal` quando o Dispatcharr devolvia ≥2 grupos cujo `Name` colapsava para a mesma chave normalizada (`Trim().ToLowerInvariant()`). Substituído o `ToDictionary(..., OrdinalIgnoreCase)` por um `GroupNameIndex` que distingue chaves únicas de chaves ambíguas; para chaves ambíguas, **nenhum** `Id` é seleccionado e a entrada é propagada em `MatchPlan.AmbiguousGroups` / `SyncReport.AmbiguousGroups` (campo `AmbiguousGroups` também adicionado a `SyncReportCounts`). A mesma política é aplicada em `DispatcharrSyncService.ApplyAsync` (substituição do `g.First().Id` por uma construção que exclui nomes ambíguos do dicionário de aplicação), garantindo que `dry_run=false` também não selecciona arbitrariamente.

### 📦 Deployment
- **Migração para Docker Compose**: `docker-compose.yml` passa a ser a fonte de verdade do deployment. Imagem `ghcr.io/ginjeira/m3ucrawler:latest` (pull-only, sem `build:` no servidor), `container_name: m3ucrawler`, `restart: unless-stopped`, comando completo com `--history-hours 360`, bind mounts absolutos para `/opt/m3ucrawler/runtime-data`. Documentação consolidada em `DEPLOYMENT.md`, `OPERATIONS.md`, `ROADMAP.md`, `AGENTS.md`.
- **`m3uCrawler/runtime-data/` no repositório** permanece como placeholder commitado (apenas `channel-indicators.json`, `countries/pt.json`, `.gitkeep`). Em produção é substituído por bind mount para `/opt/m3ucrawler/runtime-data` no host.
- Cobertura: detector (URLs/anexos/conteúdo/inspecção, **Xtream server + get.php**), parser (EXTINF, master HLS), validação por país (threshold, famílias, variantes, falsos positivos, normalização), merge de manutenção, **sanitização de credenciais** (userinfo, query, path Xtream, combinações, `SanitizeM3uContent`, `SaveToJsonReport` não persiste passwords, `SaveToM3uPlaylist` preserva URLs funcionais), **autenticação do dashboard** (`IsAuthorized`: sem token, header Bearer, query `?token=`, rejeição de token errado/parcial, credenciais Xtream não aceites), carregamento de listas por país, baseline legacy.

## [v2.1.0] - 2025-11-02

### ✨ Adicionado
- **Argumentos de linha de comando**: Suporte completo para parâmetros CLI
- **--max-streams N**: Definir limite de streams para testar (1-1000)
- **--fast / --high-performance**: Modo alta performance (20 conexões paralelas)
- **--help / -h**: Sistema de ajuda integrado
- **Configuração flexível**: Limite padrão aumentado para 500 streams
- **Parsing inteligente**: Separação correcta entre termo de pesquisa e opções
- **Scripts auxiliares**: test_simple.ps1 e run_advanced.bat
- **Detecção automática**: Console interactivo vs linha de comando

### 🔧 Melhorado
- **UX drasticamente melhorada**: Interface muito mais amigável
- **Performance configurável**: 10-20 conexões paralelas conforme modo
- **Validação robusta**: Tratamento de argumentos malformados
- **Documentação expandida**: Exemplos práticos e casos de uso
- **Flexibilidade total**: De 5 streams (teste) até 1000 (produção)

### 🔧 Corrigido
- **Console.ReadKey**: Não bloqueia quando entrada é redireccionada
- **Argumentos CLI**: Parsing correcto de termos vs opções
- **Timeout melhorado**: Redução de falsos negativos

### 📖 Exemplos de Uso
```bash
# Teste rápido
dotnet run -- "demo test" --max-streams 5

# Uso normal
dotnet run -- "iptv portugal" --max-streams 200

# Alta performance
dotnet run -- "worldwide streams" --fast --max-streams 1000

# Ajuda
dotnet run -- --help
```

## [v1.1.0] - 2025-11-02

### ✨ Adicionado
- **Múltiplas fontes de pesquisa**: 15+ fontes diferentes implementadas
- **Repositórios GitHub**: IPTV-ORG, Free-TV, M3U Filter Samples
- **APIs públicas**: StreamWeasels, IPTV Cat, Pluto TV
- **Motores alternativos**: DuckDuckGo, SearX, StartPage
- **Fontes regionais**: Portugal, Brasil, Chile
- **Extração HTML avançada**: Links de páginas web especializadas
- **Logging detalhado**: Rastreamento por fonte durante pesquisa
- **Regex melhorada**: Captura mais precisa de URLs M3U8

### 🔧 Melhorado
- **Performance**: Agora encontra 22.000+ URLs vs 100 da versão anterior
- **Eficiência**: Tempo médio reduzido de 961ms para 753ms
- **Resiliência**: Falha de uma fonte não afecta as outras
- **Headers HTTP**: Mais realistas para evitar bloqueios
- **Diversificação**: Não depende mais apenas do iptv-org.github.io

### 🐛 Corrigido
- **Problema principal**: "Nenhuma URL M3U8 encontrada" resolvido
- **Anti-bot**: Contornado com fontes alternativas
- **Timeout**: Melhor tratamento de erros de conexão
- **Regex**: Captura URLs com parâmetros de query

### 📊 Estatísticas v1.1.0
- **URLs encontradas**: 22.033 (vs 0-100 anterior)
- **URLs únicas**: 11.039 após filtragem
- **Taxa de sucesso**: 59% (59/100 streams funcionais)
- **Fontes activas**: 15+ implementadas
- **Performance**: 23x mais URLs descobertos

## [v1.0.0] - 2025-11-01

### Adicionado
- 🔍 Pesquisa automática de streams M3U8 usando múltiplos motores de busca
- 🧪 Sistema de teste paralelo de conectividade dos streams
- 💾 Geração automática de playlists M3U com apenas streams funcionais
- 📊 Relatórios detalhados em formato JSON
- 🎨 Interface colorida no console com emojis e feedback visual
- ⚙️ Sistema de configuração flexível via arquivos JSON
- 📝 Sistema de logging detalhado
- 🚀 Scripts de execução para Windows (PowerShell e CMD)
- 📚 Documentação completa com exemplos de uso
- 🧹 Script de limpeza de arquivos temporários

### Recursos Técnicos
- Processamento assíncrono e paralelo para máxima eficiência
- Tratamento robusto de erros e timeouts
- Suporte a múltiplos formatos de saída
- Arquitectura modular com separação de responsabilidades
- Compatibilidade com .NET 9.0

### Dependências
- HtmlAgilityPack para web scraping
- System.Text.Json para serialização
- .NET 9.0 como framework base

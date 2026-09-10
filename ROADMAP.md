# ROADMAP.md — Estado e direcção do m3uCrawler

> Este documento representa **o que está concluído, em curso, próximo e conhecido como problemático** no projecto. Não é uma lista especulativa de features, nem um guia técnico. Para procedimentos, ver `DEPLOYMENT.md` e `OPERATIONS.md`. Para a arquitectura da aplicação, ver `m3uCrawler/README.md`.

---

## Estado actual

- **Aplicação funcional** com pipeline Telegram completo: descoberta independente de keyword → detecção M3U → parser → validação por país (threshold 3, matching por tokens) → extracção e teste de streams → relatório detalhado.
- **Descoberta de contas Xtream via publicações HTML**: quando uma mensagem Telegram contém um URL `http(s)` genérico para uma página HTML com uma ou várias "cards" Xtream (`Host`/`User`/`Pass`/`M3U`/`EPG`/...), um **anexo `.html`/`.htm`** (e.g. `m3u@host.example_07-09-2026.html`), ou um **deep link `t.me/c/<channel>/<message>`**, cada conta é extraída pelo `XtreamPublicationResolver` (via `TelegramPublicationDiscovery` para parsing e `TelegramPublicationResolver` para resolução via WTelegram `Channels_GetMessages`) e promovida a `CandidatePlaylist` que re-entra no **mesmo pipeline** M3U/Xtream existente. Múltiplas contas no mesmo servidor permanecem como fontes independentes (identidade lógica = `endpoint+username`; password nunca participa). `M3uCandidateDetector` reconhece o anexo via `IsHtmlFilename(...)` e emite `DetectedFrom="html attachment"`. `MEDIA LIST` nunca é tratada como canais. Sem persistência nova; sem nova pipeline. Telegram publication URLs (`https://t.me/<username>/<message>`) sem channel id explícito **não** são suportadas (limitação documentada). Sem impacto em Dispatcharr/ownership/Channel Catalogue. Detalhes em `m3uCrawler/README.md` ("Publicações HTML com cards Xtream" + "Camadas de descoberta e resolução") e `CHANGELOG.md [Unreleased]`.
- **Parser adaptativo de publicações Xtream** (commits `e963a7a` → `4df856e`, branch `review/three-level-classification-v2`): o `XtreamPublicationResolver` foi refactorizado de um parser rígido para um mecanismo multi-cue que absorve a variabilidade de publishers sem heurística específica. Estratégia: (1) caminho DOM preservado (`<table>`/`<tr>`/`class=card`/`<hr>`); (2) fallback flat-text com anchor-detection — strip de ruído cosmético (box-drawing U+2500..U+25FF, símbolos, math, surrogate pairs), transliteração de glyphs fancy para ASCII (modifier letters U+1D00..U+1D7F, IPA U+0260..U+029F, Latin-1 supplement U+00C0..U+00FF, mathematical double-struck digits), clustering por proximidade com **boundary explícito em `host`** (cada `host` repetido FECHA o cluster anterior), validação de host (precisa de `.` ou TLD), e janela conservadora de 1000 chars. Validado em produção no servidor `192.168.68.142` (container `m3ucrawler:sha-4df856e`, single-cycle `--history-hours 24`) contra dois publishers reais: msg `110705` (canal `1635952193`, `neorcqds.top:8080`) descobriu 10 contas Xtream distintas testadas e integradas na `playlist.m3u`; unitariamente, o fixture `m3uCrawler.Tests/Fixtures/iptvgold_07-09-2026.html` (msg `110658`, 70 contas Xtream em `iptvgold.online:8880`) é parseada correctamente num único `ResolveFromHtml` call. Limitações conhecidas: cards adjacentes que partilham labels com metadata externa (CHANNELS/MOVIES/SERIES numa MEDIA LIST) podem fundir-se; `Label -> Value` (seta colada sem espaço) não suportado; variantes FR/ES não estão no vocabulário actual.
- **Dashboard web** (`--web`) com gestão de listas de canais por país, diagnóstico da última execução, últimas playlists descobertas e pré-visualização sanitizada das playlists. Protecção opcional por token (`--web-token`). `--web` standalone funciona sem `--telegram`.
- **Modo manutenção** (`--telegram-maintain`) preserva `playlist.m3u` quando não há novos candidatos.
- **Sincronização Dispatcharr** (opt-in via `dispatcharr_enabled=true` em `wtelegram.config`): pós-playlist, o pipeline pode agora gerar um `MatchPlan` (matching puro, determinístico, com normalização + aliases + fuzzy + numeric-sibling guard + source ordering por provider/qualidade/reliability) e aplicá-lo ao Dispatcharr. Default `dispatcharr_dry_run=true` — primeiro rollout escreve apenas `output/dispatcharr_plan_<ts>.json` e `output/dispatcharr_report_<ts>.json`. Ver "Limitações" abaixo.
- **Deployment** migrado para **Docker Compose** com imagem publicada em `ghcr.io/ginjeira/m3ucrawler` (pinning por digest imutável em produção) e bind mounts absolutos para `/opt/m3ucrawler/runtime-data`. Ver `DEPLOYMENT.md`.
- **Coerência OCI ↔ BuildInfo ↔ `/api/version`** (commits `3a338d8` → `0797b40`): a imagem publicada embute o mesmo `AssemblyInformationalVersion` que os OCI labels anunciam. Ver `CHANGELOG.md` `[Unreleased]`.
- **Cobertura de testes**: 1187 testes unitários (15 novos para o parser adaptativo — `XtreamPublicationResolverRealWorldFixtureTests` com fixture iptvgold real e `XtreamPublicationResolverAdaptiveParsingTests` cobrindo separadores/Unicode/host-boundary), 0 warnings em `dotnet build m3uCrawler.sln --configuration Release` (warning CS1998 e CS8619 em ficheiros de teste pré-existentes são fora do escopo desta iteração).
- **Sanitização de credenciais** em logs, relatórios JSON, `RunReport`, preview do dashboard e mensagens de erro. A playlist M3U funcional preserva URLs Xtream reais, como esperado. O `MatchPlanSerializer` re-aplica `CredentialSanitizer.SanitizeUrl` ao campo `streamUrl` antes de escrever.

---

## Concluído (releases recentes)

- `v1.0.0` — pipeline base de descoberta + teste (visão pré-pipeline).
- `v1.1.0` — múltiplas fontes de pesquisa (IPTV-ORG, Free-TV, DuckDuckGo, SearX, etc.).
- `v2.1.0` — limites configuráveis de streams (`--max-streams`, `--fast`), CLI completo, scripts auxiliares.
- **Pipeline Telegram** (commits `39e4216` → `d083d84`): descoberta independente de keyword, parser M3U centralizado, validação por país com matching por tokens e threshold 3, suporte Xtream Codes (URLs de servidor e de playlist), sanitização de credenciais, dashboard alinhado com o pipeline, modo manutenção sem perda de streams, `RunReport` detalhado, `--telegram-maintain` corrigido para preservar `playlist.m3u` quando não há novos candidatos.
- **Filtro per-stream por país** (`c07da69`, `d083d84`): unificação do `CountryChannelValidator` (matching por tokens, famílias canónicas, variantes colapsadas, fallback por `group-title` apenas para categorias explícitas, protecção contra falsos positivos de aliases curtos), integração no pipeline Telegram, testes dedicados.
- **Migração para Docker Compose** (lado repositório concluído): `docker-compose.yml` reescrito com imagem, comando, porta e bind mounts alinhados com a instalação real. Cutover no servidor `/opt/m3ucrawler` é estado operacional externo (ver "Estados externos ao repositório").
- **Coerência OCI ↔ BuildInfo ↔ `/api/version`** (commits `3a338d8` → `0797b40`, branch `fix/docker-buildinfo-metadata`, PR #1): Dockerfile expandido para `./` (inclui `Directory.Build.props`/`Directory.Build.targets`), `.dockerignore` na raiz com exclusões reforçadas, build-args do CI propagados a restore+publish, header CLI passa a derivar de `BuildInfo` (substitui o literal `Versão 2.1 - Novembro 2025`), `--web` standalone funciona sem `--telegram`, `DockerBuildInfoContractTests` cobre o contrato Dockerfile↔BuildInfo, e o step "Resolve published digest" do workflow GHCR usa `docker buildx imagetools inspect` (reutiliza `docker/login-action@v3` em vez do `GITHUB_TOKEN` que não tem `read:packages`). Producao live em digest imutável `sha256:a29bbfe1cf84d2db3411d5713986215d9bf8c062d71b859b4844caf483c1d4a6` (commit `e0f62e2`, build 53, OCI labels e BuildInfo coerentes).
- **Fronteira Classification → Matching** (commits `3a4af81` → `3f4af81` → `c3e0e4f`, branch `fix/docker-buildinfo-metadata`, PR #2 + PR #3): `ContentClassifier.Classify` corre antes do bucket de canais. A política de 3 níveis (exclude / match-existing / create-new) é subdividida em dois **tiers** (Curated / Unknown) com bucket storage separado `(tier, identity)`: streams da mesma identidade em tiers diferentes ficam em buckets distintos, eliminando a dependência da ordem de chegada. Tier Curated usa fuzzy + alias + threshold (80) e pode produzir `NewChannel` quando não há match; tier Unknown apenas por **igualdade normalizada** ou **alias explícito** (nunca fuzzy), pode anexar streams a canais existentes mas nunca gera `NewChannel` — entradas sem match exacto/alias vão para `UnknownReviewRequired`. `Bundle / Vod / LiveCam / Placeholder` nunca chegam a bucket (preserva a correcção da PR #2 contra `PT - NO EVENT`, `Filmes 24/7`). `SourceGroupCategoryLookup` é consumido pelo classificador para detectar source-groups editoriais. `ChannelKind.Group` e `ChannelKind.Category` foram removidos do enum (não eram emitidos por regra alguma). Endpoint `GET /api/classification-summary` no dashboard expõe as contagens por disposição (`excluded`, `unknownMatchedToExisting`, `unknownReviewRequired`, `newChannelsFromCuratedIdentity`) e a amostra sanitizada.
- **Catálogo persistente (SQLite + EF Core) + ownership seguro** (esta iteração): substitui `ChannelCategoryLookup.Contains()` como autorização para criar canais. A BD vive em `/data/channel-catalog.db` (mesmo directório de `wtelegram.config`/`session.dat`). Migrations aplicam-se idempotentemente no arranque; backup automático antes de migrations destrutivas. Seed versionado em `Services/Catalog/CatalogSeed.cs` (Benfica TV com aliases `btv` / `btv hevc pt` / `benficatv` / `benfica tv`; **`Sport TV NBA` é canal canónico autónomo `sport-tv-nba`** com `CreateEligible` e aliases `sport tv nba` / `pt sport tv nba` / `sport tv nba hevc pt`, distinto de Sport TV 1..7 via token-set ratio 67 &lt; threshold 80). Três decisões separadas: `Kind` (estrutural), `ExistingMatchEligibility` (anexa a existente), `NewChannelEligibility` (cria novo). Política explícita por canal canónico (`CreateEligible` / `MergeOnly` / `ReviewOnly` / `Excluded`). `Unknown` nunca faz fuzzy: apenas equality ou alias exacto. **Defesa em duas camadas para protecção de streams externas/desconhecidas**: (1) `ChannelMatcher.BuildExistingDecision` reclassifica `Removed` → `ExistingUnchanged("protected-by-ownership")` para streams com `Ownership != CrawlerManaged`, com counter `ProtectedExternalStreams` no relatório; (2) `DispatcharrSyncService.ApplyAsync` Phase 4 re-consulta o ownership antes de emitir DELETE (streams sem registo = `Unknown` = nunca DELETE). Streams com `Ownership = External` ou `Unknown` nunca são removidas, renomeadas ou reagrupadas — só `CrawlerManaged` pode sair. Legacy mode (sem catalog injectado) preserva comportamento histórico. Detalhes em `docs/architecture/channel-catalog-and-ownership.md`.
- **Parser adaptativo de publicações Xtream** (commits `e963a7a` → `4df856e`, branch `review/three-level-classification-v2`): o `XtreamPublicationResolver` foi refactorizado para absorver variabilidade de publishers sem heurística específica. Validado em produção contra dois publishers reais. Ver entrada em "Estado actual" acima. Detalhes em `m3uCrawler/README.md` ("Publicações HTML com cards Xtream" → "Parser adaptativo") e `CHANGELOG.md [Unreleased]`.

---

## Em curso

- **Migração para Docker Compose** (lado repositório concluído): `docker-compose.yml` reescrito com imagem (`ghcr.io/ginjeira/m3ucrawler:latest`), `container_name: m3ucrawler`, `restart: unless-stopped`, comando completo incluindo `--history-hours 360`, bind mounts absolutos para `/opt/m3ucrawler/runtime-data`. A passagem efectiva do container manual para o container Compose no servidor `/opt/m3ucrawler` é estado operacional externo e será validada durante a fase de deployment.
- **Robustez da publicação da imagem**: a tag `:latest` é mutável e re-apontada em cada push a `main`. Não existe ainda um mecanismo de pin imutável por deploy (ex.: `stable`, tag por commit, digest). O plano de migração identifica isto como follow-up.
- **Validação operacional do dashboard com `--web-token`**: o mecanismo existe e está testado, mas a configuração efectiva em produção ainda não está decidida (ver `ROADMAP.md` § Próximas evoluções).

---

## Próximas evoluções conhecidas

> Itens abaixo são intenções declaradas ou pedidos em aberto. Não há data nem承诺 de entrega — é aqui que se regista "queremos isto" sem virar em feature especulativa.

- **Tornar o pin da imagem mais robusto no `docker-compose.yml`**. Opções em estudo:
  - Tag estável (`stable`) publicada em paralelo com `latest` no workflow `docker-ghcr.yml`;
  - Pin por digest (`@sha256:...`) capturado após `docker pull` numa release;
  - Workflow `docker-ghcr.yml` com `concurrency:` para evitar race de pushes simultâneos.
- **Proteger o dashboard em produção** activando `--web-token` no comando Compose. Implicação: passar a exigir `Authorization: Bearer <token>` para `/api/playlist*`. (Hoje a porta `5000` está exposta sem autenticação.)
- **Limpar e remover documentação obsoleta** (`EXEMPLOS.md`, `STREAM_LIMIT_GUIDE.md`) que descreve o comportamento pré-pipeline. Substituir por apontadores para `m3uCrawler/README.md`.
- **Documentar formalmente a API do `RunReport`** e os endpoints do dashboard num único sítio (provavelmente uma secção adicional em `m3uCrawler/README.md` ou um `API.md` — decisão pendente).
- **Dispatcharr sync**: iterações seguintes (não no primeiro incremento) — visibilidade de canais em `ChannelProfile`, gestão de `auto_channel_sync` na conta M3U hospedada, fallback para o fluxo auto-sync quando o utilizador preferir refresh assíncrono, EPG matching.

---

## Problemas técnicos conhecidos

- **`EXEMPLOS.md` e `STREAM_LIMIT_GUIDE.md` descrevem o comportamento v2.1 (pré-pipeline Telegram)**. Os exemplos CLI ainda mostram apenas `dotnet run -- "iptv portugal"` e fluxos de pesquisa web. Marcados como **DEPRECATED** (Wave 0A 2026-09-10); mantidos para referência histórica, não devem ser usados como referência operacional. Ver `m3uCrawler/README.md` para o comportamento actual.
- **`README.md` (root)** — estado datado a 2026-09-03 (snapshot de 977 testes) foi substituído por formulação neutra que não congela contagens. Verificar sempre via `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo` para referência operacional.
- **Sem testes de integração de rede** (Telegram, HTTP de streams). Os testes são unitários e não exercitam o pipeline ponta-a-ponta. Aceitável para o estado actual; pode tornar-se limitação à medida que o pipeline crescer.

### Dívida documental confirmada

- **`STREAM_LIMIT_GUIDE.md` e `m3uCrawler/EXEMPLOS.md`** descrevem o comportamento v2.1 (pré-pipeline Telegram). Os exemplos CLI ainda mostram apenas `dotnet run -- "iptv portugal"` e fluxos de pesquisa web. **Estado decidido (Wave 0A, 2026-09-10)**: marcados como **DEPRECATED** com nota no topo de cada ficheiro. Mantidos para referência histórica; não devem ser usados como referência operacional. Ver `m3uCrawler/README.md` para o comportamento actual.
- **Wave 0B (2026-09-10)** — remediação de segurança:
  - BUG-SEC-001 — corrigido: `m3uCrawler/Services/M3uCrawlerService.cs` linhas 108 e 118 (impressão de URL Xtream com credenciais reais) — substituído por `CredentialSanitizer.SanitizeUrl(m3uUrl)` antes de `Console.WriteLine`. Adicionados 4 testes em `M3uCrawlerServiceSanitizationTests.cs` para garantir que credenciais em query (`username=`/`password=`/`token=`) e password em userinfo são sempre ocultadas. Dívida técnica residual: `SanitizeUrl` **preserva** o username em userinfo (`http://alice:***@...`); isto é suficiente para BUG-SEC-001 (password nunca exposto) mas é candidato a fix futuro — registado em `CapabilityCoverageMatrix-2026-09-10.md`.
  - BUG-SEC-002 — corrigido: `m3uCrawler/Program.cs` linha 265 continha token Telegram Bot literal hardcoded — **confirmado**: era um bot token real, revogado externamente se ainda em uso (decisão operacional do administrador). Token removido do código; o caminho `--bot` agora requer `--bot-token <token>` ou variável de ambiente `M3U_BOT_TOKEN`. Adicionado `--bot-token` ao `skipWithValue` do M3U8-search legacy e ao help text. Adicionado teste em `TelegramBotServiceConfigurationTests.cs` para garantir que o token é configurável externamente (placeholder `0000000000:PLACEHOLDER_TOKEN_FOR_TESTING_ONLY__`). Confirmação de não-regressão no deployment actual: container `m3ucrawler:sha-b452651` no servidor NÃO usa `--bot`, pelo que a correcção é compatível com deployment corrente.
- **`ARCHITECTURE.md`** consolidado como fonte normativa agregadora da arquitectura existente: **adiado** para uma wave posterior. Razão: o projecto ainda tem dívida arquitectural em áreas em evolução (radio, VOD, EPG, source priority, ordering configurável, tvg-id matching), pelo que cristalizar o estado actual como "arquitectura definitiva" não é prudente.

Estas ondas estão sujeitas a priorização pelo dono do projecto.

---

## Estados externos ao repositório

Os pontos abaixo **não podem ser determinados a partir do repositório** — são estado operacional do servidor `/opt/m3ucrawler` que será validado apenas durante a fase de deployment:

- **Histórico de produção conhecido**:
  - B1.3 (2026-09-02): commit `e0f62e2`, digest imutável `sha256:a29bbfe1cf84d2db3411d5713986215d9bf8c062d71b859b4844caf483c1d4a6`. Esta foi a versão de produção até à Wave 0 (2026-09-10).
  - Wave 0 de remediação documental (2026-09-10): `main` está em `b452651ad49dcfe11bda2598b6d71d62f875a736`. O PR #1 (commits `3a338d8`, `d42310a`, `e0f62e2`, `0797b40`) já foi merged em `main` (ver `CHANGELOG.md [Unreleased]`).
- **Estado actual do container no servidor**: verificado via deploy end-to-end em 2026-09-10 (ver commit `b452651` no servidor `/opt/m3uCrawler-source`, container `m3ucrawler:sha-b452651` em loop). Para o estado mais recente, comparar `git rev-parse HEAD` local com o `version` exposto pelo dashboard `/api/version`.
- **Decisão operacional sobre `--web-token` em produção**: depende do administrador.
- **Permissões exactas de `/opt/m3ucrawler/runtime-data`**: depende da política local do host.

Estes pontos serão alvo de validação durante a fase de deployment (server-side runbook em `DEPLOYMENT.md` e verificação operacional em `OPERATIONS.md`).

---

## Como ler este documento

- **Quero saber o que existe** → secção "Concluído".
- **Quero saber o que está activo** → secção "Em curso".
- **Quero saber o que vai ser feito a seguir** → secção "Próximas evoluções".
- **Quero saber o que ainda está mal** → secção "Problemas técnicos conhecidos".

Para instruções passo-a-passo de deployment ou operação, ir respectivamente a `DEPLOYMENT.md` e `OPERATIONS.md`.

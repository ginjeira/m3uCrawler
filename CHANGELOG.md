# 📈 Changelog - m3uCrawler

Todas as mudanças notáveis neste projeto serão documentadas neste arquivo.

O formato é baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.0.0/),
e este projeto adere ao [Semantic Versioning](https://semver.org/lang/pt-BR/).

## [Unreleased]

### ✨ Adicionado
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
- Testes: `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo` → **1039 testes passados, 0 falhados**.
- actionlint: **0 errors, 0 warnings**.
- Imagem rollback preservada: `sha256:27b0b18dd81e9c01416bad674cfbe86515a9994be4565bb44dda49fb2e50b97e`.

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

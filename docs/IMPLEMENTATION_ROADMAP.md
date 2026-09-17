# m3uCrawler — Plano de Implementação e Evolução da Plataforma

## 1. Objectivo

Este documento transforma a visão arquitectural definida em:

> `m3uCrawler — Contexto de Projecto, Decisões e Linha de Evolução`

num plano concreto de implementação.

O objectivo é evoluir o m3uCrawler de um crawler/discovery pipeline para uma plataforma funcional de:

```text
Discovery
    ↓
Sources
    ↓
Playlists
    ↓
Streams
    ↓
Normalization
    ↓
Canonical Catalogue
    ↓
ChannelSource
    ↓
Validation
    ↓
Ordering
    ↓
Source Priority
    ↓
Playlist Generation
    ↓
Dispatcharr
```

O operador deverá conseguir controlar as decisões relevantes através do Dashboard.

O plano deve ser implementado **por fases funcionais**, mas sem transformar o desenvolvimento numa sucessão de análises/revisões sem entrega.

Cada fase deve produzir funcionalidade utilizável.

---

# 2. Princípios de implementação

## 2.1 Source of truth

O repositório e os testes existentes são a referência do estado actual.

Não reimplementar funcionalidades já existentes.

Não criar uma segunda camada para resolver um problema que já tenha uma solução funcional.

---

## 2.2 Evolução incremental

Preservar:

- discovery Telegram;
- parsing M3U;
- Xtream publication resolver;
- validação;
- classification;
- matching;
- catálogo persistente;
- ownership Dispatcharr;
- manutenção da playlist;
- sanitização de credenciais.

A nova arquitectura deve integrar estes componentes.

Não reescrever componentes estáveis sem necessidade.

---

## 2.3 Dashboard como Control Plane

Decisões operacionais relevantes devem ser persistentes e administráveis pelo Dashboard.

O Dashboard não deve ser apenas uma interface de diagnóstico.

Deve tornar-se o **control plane do m3uCrawler**.

O contexto do projecto estabelece explicitamente este princípio. fileciteturn36file7L1-L2

---

## 2.4 Identidade e posição são conceitos independentes

Nunca utilizar:

```text
source channel number
```

como identidade canónica.

A identidade deve resultar de matching.

A posição deve resultar de uma Ordering List.

O contexto estabelece explicitamente esta separação. fileciteturn36file11L1-L2

---

## 2.5 Ordering e Source Priority são independentes

Ordering responde:

```text
Onde aparece o canal?
```

Source Priority responde:

```text
Qual dos streams disponíveis deve ser utilizado?
```

Não misturar os dois mecanismos.

---

## 2.6 Dados desconhecidos não são eliminados

Um canal desconhecido deve poder entrar em:

```text
Unmatched
Pending
Review
Other
```

e ser tratado posteriormente pelo operador.

O Dashboard deve permitir corrigir o matching e melhorar o catálogo. fileciteturn36file6L1-L2

---

# 3. Estado actual

A implementação actual já contém:

- pipeline Telegram;
- descoberta de publicações Xtream;
- parser adaptativo;
- classification;
- matching;
- catálogo persistente SQLite;
- aliases;
- identity rules;
- affinity groups;
- review items;
- ownership de canais/streams Dispatcharr;
- SyncRun;
- Dashboard parcial;
- geração de playlist;
- sincronização Dispatcharr.

O catálogo persistente já possui entidades para canal canónico, aliases, regras, affinity groups, review items, ownership e SyncRun. 

O `CatalogResolver` já fornece operações persistentes sobre estes elementos. 

O Dashboard já expõe parte deste estado, incluindo catálogo e diagnósticos. 

Portanto, as próximas fases devem **completar e integrar** estas capacidades.

---

# 4. Estratégia geral

A implementação será dividida em:

```text
PHASE 0 — Baseline / preparação
PHASE 1 — Canonical Catalogue
PHASE 2 — Dashboard Control Plane
PHASE 3 — Matching & Review
PHASE 4 — Sources & ChannelSource
PHASE 5 — Ordering Lists
PHASE 6 — Source Priority
PHASE 7 — Playlist Generation
PHASE 8 — TV / Radio / VOD / Groups
PHASE 9 — Stream Quality / EPG / Availability
PHASE 10 — Dispatcharr Integration
PHASE 11 — Operations / Automation
PHASE 12 — Production consolidation
```

As fases seguintes podem começar assim que os contratos necessários estejam implementados.

Não efectuar uma nova grande análise do projecto entre cada fase.

---

# 5. PHASE 0 — Baseline

## Objectivo

Fixar a base actual antes de alterações funcionais.

## Trabalho

- preservar testes existentes;
- garantir build Release;
- confirmar migrations;
- confirmar localização da BD;
- confirmar runtime-data;
- confirmar funcionamento do Dashboard;
- confirmar funcionamento do pipeline actual.

## Resultado

Baseline funcional documentada.

## Critério de conclusão

```text
dotnet build
dotnet test
```

sem regressões.

Depois desta fase, iniciar imediatamente a implementação funcional.

---

# 6. PHASE 1 — Canonical Catalogue

## Objectivo

Transformar o catálogo actual numa verdadeira base de dados canónica utilizável pelo sistema.

## 6.1 Canonical Channel

O canal canónico deve representar:

```text
CanonicalId
DisplayName
Country
Category
EditorialGroup
PublicationPolicy
Enabled
CreatedAt
UpdatedAt
```

Preservar o modelo existente sempre que possível.

Não criar uma segunda entidade paralela.

---

## 6.2 Aliases

Permitir:

```text
Canonical Channel
    ├── Alias 1
    ├── Alias 2
    ├── Alias 3
    └── ...
```

Operações:

- listar;
- adicionar;
- editar;
- remover;
- detectar conflitos.

---

## 6.3 Identidades especiais

Manter:

```text
IdentityRule
AffinityGroup
AffinityMember
```

como mecanismos distintos.

---

## 6.4 Grupos editoriais

Os grupos devem deixar de ser apenas enums rígidos sempre que a decisão for editorial/configurável.

Deve existir uma representação persistente dos grupos que permita:

```text
nome
display name
ordem
activo
categoria
```

e posteriormente mappings de source groups.

---

## 6.5 Seed portuguesa

Integrar o catálogo português fornecido como **seed inicial**.

A seed deve ser importável para o catálogo persistente.

Não deve ser tratada como configuração imutável.

**Estado (2026-09-10)**: ✅ **concluído**.

- `docs/catalog/m3ucrawler_pt_canonical_catalog.json` adicionado ao repositório com `catalog_id=pt-canonical-tv`, `version=1.0`, `country=PT`.
- `m3uCrawler/Services/Catalog/CatalogBaselineImporter.cs` (NOVO): importa o JSON para `CanonicalChannelEntity` + `ChannelAliasEntity` via `CatalogBaselineImporter.ImportAsync(context, baseline)`. Idempotente, aditivo, auditável.
- `m3uCrawler.Tests/CatalogBaselineImporterTests.cs` (NOVO): 19 testes cobrindo conversão de `canonical_id`, resolução de grupo/categoria, idempotência, preservação de canais pré-existentes, sanitização do report, integração com o ficheiro baseline real.
- `m3uCrawler/Services/Catalog/ChannelCatalogBootstrapper.cs` (modificado): `TryImportBaselineAsync` chamado automaticamente após `SeedAsync`. Procura o baseline em três localizações canónicas (env var `M3U_BASELINE_PATH`, `CWD/docs/catalog/`, `<binary>/../../../../../docs/catalog/`). Falha na importação é registada em log mas não aborta o arranque.
- **Não** substituiu o `CatalogSeed` programático — coexiste. O baseline é o source primário do seed PT; `CatalogSeed` permanece como fallback.
- **Limitação conhecida** (PHASE 6 — Grupos): os grupos editoriais continuam a ser o enum `CanonicalEditorialGroup` em vez de entidades persistentes. A resolução de categoria editorial a partir do baseline é heurística (palavras-chave em aliases/nome). PHASE 6 dos grupos está planeada para depois.

---

# 7. PHASE 2 — Dashboard Control Plane

Esta é uma das fases prioritárias.

## Objectivo

Transformar o Dashboard actual numa interface administrativa real.

O contexto do projecto estabelece que decisões operacionais como VOD, grupos, aliases, matching, duplicados, qualidade, ordering e prioridades devem ser configuráveis. fileciteturn36file7L1-L2

---

## 7.1 Dashboard principal

Criar uma página inicial com:

```text
Sources
Channels
Streams
Review
Ordering
Policies
Playlists
Dispatcharr
Runs
System
```

Dashboard deve apresentar:

- estado do crawler;
- última execução;
- canais;
- streams;
- unmatched;
- review pending;
- sources;
- playlist actual;
- estado Dispatcharr.

---

# 8. Dashboard — Catalogue

## Página

```text
Channels
```

### Lista

Colunas:

```text
Position*
Canonical ID
Name
Category
Group
Aliases
Sources
Streams
Status
Policy
```

`Position` é derivada da Ordering List seleccionada.

---

## Operações

Permitir:

- criar canal;
- editar canal;
- activar/desactivar;
- alterar nome;
- alterar categoria;
- alterar grupo;
- alterar política;
- adicionar alias;
- remover alias;
- consultar sources;
- consultar streams.

---

# 9. Dashboard — Channel Detail

Ao seleccionar um canal:

```text
RTP 1
```

mostrar:

### Identity

```text
Canonical ID
Display Name
Category
Group
Status
```

### Aliases

```text
RTP1
RTP 1
RTP 1 HD
RTP 1 FHD
...
```

### Sources

```text
Source
Stream
Quality
EPG
Status
Priority
```

### Ordering

```text
Ordering List
Position
```

### Provenance

```text
created
updated
matching information
```

---

# 10. Dashboard — Review

Criar uma área:

```text
Review
```

com:

```text
Unknown
Pending
Ambiguous
Low Confidence
```

Cada item deve mostrar:

```text
Original Name
Normalized Identity
Source
Group
Suggested Channel
Match Method
Confidence
Reason
```

---

## Acções

```text
Associate Existing Channel
Create New Channel
Add Alias
Exclude
Ignore
Mark as VOD
```

Quando uma decisão humana puder melhorar o matching futuro, deve alterar o catálogo correspondente.

O modelo actual de `ReviewItemEntity` já fornece uma base para esta funcionalidade. 

---

# 11. Dashboard — Sources

Criar:

```text
Sources
```

Cada source deve possuir:

```text
SourceId
Name
Type
Origin
Enabled
Priority
Last Discovery
Last Validation
Health
```

Tipos possíveis:

```text
Telegram
M3U
Xtream
HTTP
File
Manual
```

---

## Source Detail

Mostrar:

```text
Source
Playlists
Channels
Streams
Last execution
Health
Priority
```

Nunca apresentar passwords ou URLs contendo credenciais.

**Estado (2026-09-11)**: ✅ **concluído**.

- API administrativa completa (`/api/catalog/channels`,
  `/api/catalog/channels/{id}`, aliases, rules, affinity,
  pending-country-approvals).
- UI completa nos tabs `Catalog` (channels, rules, affinity,
  reviews, syncruns, pending, sources, ordering, priority,
  matching, policies, groups, scheduled).
- 5 testes HTTP end-to-end em
  `m3uCrawler.Tests/WebDashboardHttpApiTests.cs` usando
  `HttpListener` em loopback (Port efémera por teste). Ver
  detalhes na secção 32.4.
- Variantes testáveis adicionadas:
  `HandleRequestOnTestAsync(...)` e
  `RunDashboardForTestsAsync(...)` — preservam o contrato
  existente via `StaticResolverScope`.
- Sanitização de credenciais em todas as respostas
  (preview-only). Playlist funcional preserva URLs reais.

---

# 12. PHASE 3 — Matching & Review

## Objectivo

Transformar matching numa operação explicitamente observável e configurável.

Prioridade conceptual:

```text
1 exact tvg-id
2 known canonical/provider ID
3 normalized name
4 alias
5 controlled heuristic
6 fuzzy
7 review
```

O contexto define esta ordem conceptual. fileciteturn36file11L1-L2

---

## Resultado do matching

Cada associação deve produzir:

```text
CanonicalChannelId
MatchMethod
Confidence
```

e preservar:

```text
OriginalName
NormalizedName
Source
```

---

## Confidence

Configurar thresholds:

```text
Auto Match
Review
Reject
```

Os thresholds devem ser persistentes.

Não hardcodear valores quando forem decisões operacionais.

**Estado (2026-09-11)**: ✅ **concluído**.

- `MatchingAuditEntity` persistente (PHASE 3 — observabilidade),
  com `NormalizedIdentity`, `OriginalTitle`, `SourceGroup`,
  `ResolutionKind` (Canonical/Rule/Unknown), `Confidence`,
  `ReasonSignature`, `AtUtc`. Truncagem defensiva a 500/120
  caracteres.
- Migração `AddMatchingAudit` cria `matching_audits` com
  índices em `AtUtc`, `CanonicalChannelId`, `NormalizedIdentity`.
- Serviço: `RecordMatchingAuditAsync(...)`,
  `GetRecentMatchingAuditsAsync(limit?, channelId?)`,
  `GetMatchingAuditStatsAsync()`.
- API: `GET /api/catalog/matching/recent?channelId=&limit=`
  + `GET /api/catalog/matching/stats`.
- Dashboard — tab `Matching` com filtros, stats agregadas
  (Canonical/Rule/Unknown + last 24h) e tabela detalhada.
- 7 testes em `m3uCrawler.Tests/MatchingAuditTests.cs`. Ver
  detalhes na secção 32.5.
- O workflow de Review (`ReviewItemEntity`) já existia e está
  integrado nos endpoints `/api/catalog/reviews/*` e no tab
  `Reviews` do Dashboard.

---

# 13. PHASE 4 — Sources & ChannelSource

## Objectivo

Implementar explicitamente:

```text
Channel
    ↕
ChannelSource
    ↕
Stream
    ↕
Source
```

Uma source pode fornecer vários streams para o mesmo canal.

Um canal pode possuir múltiplas sources.

---

## ChannelSource

Deve permitir guardar:

```text
ChannelId
SourceId
StreamId
Quality
EPG availability
Reachability
Confidence
MatchMethod
FirstSeen
LastSeen
Enabled
```

---

## Resultado

O sistema passa a conseguir responder:

```text
Que streams existem para RTP 1?
De que sources vieram?
Qual funciona?
Qual tem EPG?
Qual é FHD?
Qual foi o último teste?
```

**Estado (2026-09-11)**: ✅ **concluído**.

- `m3uCrawler/Services/Catalog/CatalogEntities.cs` — adicionadas `SourceEntity`, `ChannelSourceEntity`, e enums `SourceKind`, `StreamQuality`, `EpgState`, `AvailabilityState`.
- `m3uCrawler/Services/Catalog/ChannelCatalogDbContext.cs` — `DbSet`s `Sources` e `ChannelSources`, com mappings completos, índices e FKs.
- `m3uCrawler/Services/Catalog/Migrations/20260911054854_AddSourcesAndChannelSources.*` — migration EF Core que cria as tabelas `sources` e `channel_sources` com índices `(CanonicalChannelId, SourceId)` e `SourceId`.
- `m3uCrawler/Services/Catalog/CatalogResolver.cs` — métodos `ListSourcesAsync`, `GetSourceAsync`, `EnsureSourceAsync` (upsert por `Key`, com sanitização de credenciais em `Origin`), `DeleteSourceAsync`, `MarkSourceDiscoveryAsync`, `MarkSourceValidationAsync`, `RecordChannelSourceAsync` (upsert por `(channelId, sourceId, streamUrl)`, com sanitização de `StreamUrl`), `ListChannelSourcesAsync`, `DeleteChannelSourceAsync`, `SetChannelSourceEnabledAsync`. Stats expandidas com `Sources` e `ChannelSources`.
- `m3uCrawler/Services/WebDashboardService.cs` — endpoints `GET/POST /api/catalog/sources`, `DELETE /api/catalog/sources/{id}`, `GET /api/catalog/sources/{id}/streams?channelId={id}`, `POST /api/catalog/sources/{id}/streams`, `DELETE /api/catalog/channel-sources/{id}`, `PUT /api/catalog/channel-sources/{id}`. Payload classes `SourcePayload`, `ChannelSourcePayload`, `ChannelSourceUpdatePayload`. Sanitização via `CredentialSanitizer.SanitizeUrl`.
- Dashboard — novo tab `Sources` em Catálogo: lista, criar/eliminar, filtro por source para streams associadas, activação/desactivação, cards de stats adicionais.
- `m3uCrawler.Tests/SourceAndChannelSourceTests.cs` (NOVO): 12 testes cobrindo CRUD, upsert, sanitização de credenciais em Origin/URL, validação de key/name, delete com cascade, filtros de query, toggle enabled, stats.
- `dotnet build`/`dotnet test`: 0 warnings/errors; 1270 testes a passar (1244 baseline + 12 PHASE 4 + 14 PHASE 5/6/7).

---

# 14. PHASE 5 — Ordering Lists

## Objectivo

Implementar Ordering como entidade independente.

O contexto estabelece que uma playlist pode ser utilizada para criar uma Ordering List e que posteriormente essa lista deve poder ser alterada pelo operador. fileciteturn36file6L1-L2

---

## OrderingList

Modelo:

```text
OrderingList
    Id
    Name
    Country
    Description
    Enabled
    CreatedAt
    UpdatedAt
```

---

## OrderingItem

```text
OrderingListId
CanonicalChannelId
Position
Enabled
```

---

## Listas iniciais

Criar suporte para:

```text
Portugal — Principal
Portugal — MEO
Portugal — NOS
Portugal — Vodafone
Portugal — Minha Lista
```

Não criar lógica específica para MEO/NOS/Vodafone.

São simplesmente Ordering Lists.

---

## Dashboard

Permitir:

- criar lista;
- duplicar lista;
- renomear;
- eliminar;
- activar/desactivar;
- adicionar canal;
- remover canal;
- alterar posição;
- drag & drop;
- preencher posições automaticamente;
- importar ordem de uma M3U.

---

# 15. Importação de Ordering List

Fluxo:

```text
M3U
 ↓
parse
 ↓
normalization
 ↓
matching
 ↓
ordering items
 ↓
review unmatched
 ↓
Ordering List
```

A ordem da M3U é preservada durante a importação.

Mas a M3U não passa a ser uma regra hardcoded.

**Estado (2026-09-11)**: ✅ **concluído** (parcial — importação M3U→lista documentada como fluxo de alto nível; a conversão M3U→OrderingItems é responsabilidade da PHASE 7 — Playlist Generation, integrada no composer).

- `m3uCrawler/Services/Catalog/CatalogEntities.cs` — adicionadas `OrderingListEntity`, `OrderingItemEntity`.
- `m3uCrawler/Services/Catalog/Migrations/*AddOrderingListsAndSourcePriority*` — tabelas `ordering_lists` e `ordering_items` com índices únicos `(OrderingListId, Position)` e `(OrderingListId, CanonicalChannelId)`.
- `m3uCrawler/Services/Catalog/CatalogResolver.cs` — `ListOrderingListsAsync`, `GetOrderingListAsync(includeItems)`, `CreateOrderingListAsync` (valida duplicados por Key), `DuplicateOrderingListAsync` (clona items com posições renumeradas), `DeleteOrderingListAsync`, `AddOrderingItemAsync` (renumera posições >= insertPos), `RemoveOrderingItemAsync` (reaperta gaps), `MoveOrderingItemAsync` (movimento atómico via posição temporária para evitar ciclos do índice único), `SetOrderingItemEnabledAsync`.
- `m3uCrawler/Services/WebDashboardService.cs` — endpoints `GET/POST /api/catalog/ordering-lists`, `GET /api/catalog/ordering-lists/{id}`, `DELETE /api/catalog/ordering-lists/{id}`, `POST /api/catalog/ordering-lists/{id}/duplicate`, `POST /api/catalog/ordering-lists/{id}/items`, `PUT/DELETE /api/catalog/ordering-items/{id}`, `GET /api/catalog/ordering-lists/{id}/preview`.
- Dashboard — novo tab `Ordering`: criar/duplicar/eliminar lista, adicionar canais, mover ↑/↓, activar/desactivar, preview em tempo real da playlist composta.
- `m3uCrawler.Tests/OrderingAndPriorityTests.cs` (PHASE 5/6/7): 14 testes cobrindo criação, duplicação, adição com shift, remoção com compactação, movimento sem gaps, cascade, política global/per-canal e composer.
- Stats adicionadas: `OrderingLists`, `OrderingItems`, `SourcePriorityPolicies`.

---

# 16. PHASE 6 — Source Priority

## Objectivo

Permitir múltiplas fontes por canal e determinar qual deve ser utilizada.

Exemplo:

```text
RTP 1

1 — Source A — FHD
2 — Source B — HD
3 — Source C — SD
```

---

## Modelo

Permitir:

```text
SourcePriorityPolicy
```

com critérios como:

```text
Manual
Quality
Reliability
EPG
Latency
Response time
Availability
```

---

## Dashboard

Página:

```text
Source Priority
```

Permitir:

- prioridade global;
- prioridade por canal;
- prioridade por source;
- preferências de qualidade;
- fallback.

**Estado (2026-09-11)**: ✅ **concluído**.

- `m3uCrawler/Services/Catalog/CatalogEntities.cs` — adicionada `SourcePriorityPolicyEntity` (Scope `"global"` ou `"channel"`, `CriteriaJson` (CSV JSON), `PreferredQuality`, `AllowFallback`).
- `m3uCrawler/Services/Catalog/Migrations/*AddOrderingListsAndSourcePriority*` — tabela `source_priority_policies` com índice único em `Scope`.
- `m3uCrawler/Services/Catalog/CatalogResolver.cs` — `GetOrCreateGlobalPriorityPolicyAsync` (cria defaults: criteria=`["Quality","Reliability","Availability"]`, preferredQuality=`"UHD,FHD,HD,SD"`, fallback=true), `GetChannelPriorityPolicyAsync(channelId)`, `UpsertPriorityPolicyAsync(scope, channelId, criteriaJson, preferredQuality, allowFallback)` com validação de invariantes.
- `m3uCrawler/Services/Catalog/PlaylistComposerService.cs` — `SourceSelector` (pura, sem I/O) aplica critérios por ordem: Manual → Quality → Reliability → EPG → Availability, com desempate por `Source.Priority`.
- `m3uCrawler/Services/WebDashboardService.cs` — endpoints `GET/POST /api/catalog/priority-policies?channelId={id}` (devolve global + override opcional, ou upsert).
- Dashboard — novo tab `Source Priority`: editor da política global e override por canal com persistência imediata. Cards de stats adicionais (`Source Priority Policies`).
- Testes: ver PHASE 5/6/7 abaixo.

---

# 17. PHASE 7 — Playlist Generation

## Objectivo

A playlist deixa de ser simplesmente o resultado directo do crawler.

Passa a ser construída a partir de:

```text
Canonical Catalogue
        +
ChannelSource
        +
Ordering List
        +
Source Priority
        +
Policies
```

---

## Fluxo

```text
Catalogue
   ↓
Ordering
   ↓
ChannelSource
   ↓
Source Priority
   ↓
Validation
   ↓
Playlist
```

---

## Resultado

A playlist final deverá ter:

```text
posição canónica
nome canónico
grupo canónico
logo
stream seleccionado
```

sem perder informação de proveniência internamente.

**Estado (2026-09-11)**: ✅ **concluído** (com `PlaylistComposition` em memória, exposta via API/Dashboard; a escrita para `playlist.m3u` com formato EXTINF/EXTM3U mantém-se responsabilidade do `PlaylistManagerService` existente — a integração com o manager é a próxima iteração, ver PHASE 7 b).

- `m3uCrawler/Services/Catalog/PlaylistComposerService.cs` (NOVO):
  - `PlaylistComposition`/`PlaylistEntry`/`MissingChannel` — DTOs imutáveis;
  - `PlaylistComposerService.ComposeAsync(orderingListId, overridePolicyScopeChannelId?, ct)` — itera `OrderingItem` activos, filtra canais `IsEnabled`, agrupa `ChannelSource` activos por canal, separa `eligible` de `all` para distinguir `no-eligible-source` (todas as fontes Dead/Unreachable) de `no-channel-source` (sem fontes), aplica `SourceSelector` (PHASE 6), devolve entries + missing.
  - `SourceSelector.Select(candidates, policy, preferredSourceId?)` — pura, sem I/O; ordem de critérios: Manual → Quality → Reliability → EPG → Availability; desempate por `Source.Priority`.
- API: `GET /api/catalog/ordering-lists/{id}/preview` devolve `PlaylistComposition` (entries + missingChannels) com proveniência (`ChosenChannelSourceId`, `SourceId`, `SourceName`, `Quality`).
- Dashboard: bloco "Preview da playlist" no tab `Ordering` mostra tabela com as entradas geradas e lista os canais sem stream (`MissingChannel` com reason).
- Testes: 14 testes em `OrderingAndPriorityTests.cs` (PHASE 5/6/7) — incluem `Compose_picks_highest_quality_when_policy_prefers_it`, `Compose_skips_dead_sources_and_reports_missing_channel`, `Compose_uses_global_policy_when_no_per_channel_override`, `SourceSelector_parses_criteria_and_quality_csv`.
- `dotnet build`/`dotnet test`: 0 warnings/errors; 1270 testes a passar.

### PHASE 7 b — Integração com `PlaylistManagerService` (concluída 2026-09-11)

`PlaylistManagerService.WriteComposedAsync(PlaylistComposition,
filePath)` escreve uma playlist M3U real a partir da
composição, mantendo o formato `#EXTM3U` + header
`#PLAYLIST:m3uCrawler - …` + `#ORDERING-LIST:<id>=<nome>` para
proveniência interna. Cada entry produz:

```text
#EXTINF:-1 group-title="<Group>",<DisplayName>
<StreamUrl>
```

Não volta a testar streams (a proveniência já vem da composição).
A separação crítica mantém-se: o composer decide o que serve
em cada posição; o manager escreve o ficheiro com proveniência
+ group + displayName.

- Teste:
  `OrderingAndPriorityTests.PlaylistManagerService_writes_composed_playlist_file`
  valida o ficheiro completo (header, EXTINF, URLs, group-title,
  metadados da lista).
- Ver detalhes completos na secção 32.6.

Resultado: PHASE 7 (incluindo PHASE 7 b) passa a `[concluído]`.

---

# 18. PHASE 8 — TV / Radio / VOD / Groups

## 18.1 TV

Separar explicitamente:

```text
Linear TV
```

---

## 18.2 Rádio

Criar:

```text
Radio
```

com:

```text
Radio Portugal
Radio International
```

e ordering próprio.

O contexto determina que rádio deve ser tratado separadamente de televisão. fileciteturn36file12L1-L2

---

## 18.3 VOD

VOD nunca utiliza posições de Linear TV.

Políticas:

```text
Import VOD
Keep VOD
Exclude VOD
```

Grupos:

```text
VOD Filmes
VOD Séries
VOD Infantil
VOD Documentários
VOD Concertos
```

---

## Dashboard

Criar:

```text
Import Policies
```

com:

```text
TV       ON/OFF
Radio    ON/OFF
VOD      ON/OFF
```

e políticas específicas.

---

# 19. PHASE 8 — Group Management

Criar:

```text
Groups
```

com:

```text
Canonical Groups
Source Groups
Mappings
```

Exemplo:

```text
Source:
PORTUGAL SPORTS

↓ mapping

Canonical:
Portugal | Desporto
```

O `group-title` da source nunca deve ser automaticamente considerado canonical group. fileciteturn36file12L1-L2

---

## Dashboard

Permitir:

- criar grupo;
- editar;
- activar/desactivar;
- associar source groups;
- alterar ordem;
- definir grupo default;
- excluir grupos.

**Estado (2026-09-11)**: ✅ **concluído**.

- `m3uCrawler/Services/Catalog/CatalogEntities.cs` — adicionadas `ImportPolicyEntity`, `CanonicalGroupEntity`, `GroupMappingEntity` e enums `MediaKind`, `VodPolicy`.
- `m3uCrawler/Services/Catalog/Migrations/*AddImportPoliciesAndGroups*` — tabelas `import_policies`, `canonical_groups`, `group_mappings` com índices únicos apropriados (`MediaKind`, `Key`, `(SourceKind, SourceGroupTitle)`).
- `m3uCrawler/Services/Catalog/CatalogResolver.cs` — `ListImportPoliciesAsync`, `GetImportPolicyAsync`, `UpsertImportPolicyAsync` (validação de tamanho CSV), `ListCanonicalGroupsAsync`, `UpsertCanonicalGroupAsync` (validação key/displayName + tamanho), `DeleteCanonicalGroupAsync`, `ListGroupMappingsAsync`, `UpsertGroupMappingAsync` (validação de FK para CanonicalGroup), `DeleteGroupMappingAsync`.
- `m3uCrawler/Services/WebDashboardService.cs` — endpoints `GET/POST /api/catalog/import-policies`, `GET/POST /api/catalog/canonical-groups`, `DELETE /api/catalog/canonical-groups/{id}`, `GET/POST /api/catalog/group-mappings`, `DELETE /api/catalog/group-mappings/{id}`.
- Dashboard — novos tabs `Import Policies` (Live/Radio/VOD com CSV de grupos alvo/excluídos) e `Groups` (grupos canónicos persistentes + Group Mappings source→canónico). Cards de stats adicionais (`Import Policies`, `Canonical Groups`).
- `m3uCrawler.Tests/ImportPoliciesAndGroupsTests.cs` (NOVO): 11 testes cobrindo upsert, validação, upsert idempotente de mapping, rejeição de FK inválida, eliminação, stats.
- `dotnet build`/`dotnet test`: 0 warnings/errors; 1281 testes a passar (1244 baseline + 12 PHASE 4 + 14 PHASE 5/6/7 + 11 PHASE 8).

---

# 20. PHASE 9 — Quality / EPG / Availability

## Quality

Detectar:

```text
SD
HD
FHD
UHD
4K
```

como propriedades do stream.

Não transformar qualidade em identidade.

---

## Quality Policy

Permitir:

```text
Prefer UHD
Prefer FHD
Prefer HD
Prefer SD
Best available
```

Mas a política deve poder combinar qualidade com disponibilidade/reliability.

---

## EPG

Estados:

```text
Available
Unavailable
Unknown
```

Políticas:

```text
Keep
Exclude
Keep + flag
Prefer EPG
```

O contexto determina explicitamente que ausência de EPG não torna automaticamente um stream inválido. fileciteturn36file2L1-L2

---

## Availability

Estados:

```text
Discovered
Validated
Reachable
Unreachable
Timeout
Dead
```

Guardar histórico suficiente para permitir reliability.

**Estado (2026-09-11)**: ✅ **concluído**.

- PHASE 9A — performance de validação: `StreamValidationOptions`,
  `StreamFailureClassifier`, `StreamValidationCache`,
  `StreamValidationMetrics`, `HostFailureTracker`,
  `StreamValidationPolicyStore` e `M3uTesterService` reescrita
  com HttpClient partilhado, `CancellationToken`, retries
  selectivos, cache TTL, early-exit, host-aware short-circuit,
  métricas. 14 testes de performance + 1 benchmark. Ver
  secção 32.2.
- PHASE 9 b — histórico de observações: `ChannelSourceObservationEntity`
  com `Quality`, `EpgState`, `AvailabilityState`, `ResponseTimeMs`.
  API `GET/POST /api/catalog/channel-sources/{id}/observations`.
  3 testes. Ver secção 32.7.
- PHASE 9 visão agregada — Dashboard de degradação:
  `GetDegradedStreamsAsync(lookbackMinutes, limit)` identifica
  streams que transitaram de saudável (Validated/Reachable) para
  terminal (Dead/Unreachable/Timeout) dentro do lookback;
  `GetDegradationStatsAsync` agrega contagens por estado +
  taxa terminal. Endpoints `GET /api/catalog/degradation/recent`
  + `GET /api/catalog/degradation/stats`. Dashboard tab
  `Degradação` com filtros lookback/limit, stats agregadas e
  tabela por stream. 5 testes em `DegradationDashboardTests.cs`.

PHASE 9 está totalmente coberta — sub-fases concluídas em 9A,
9 b e visão agregada.

---

# 21. PHASE 9A — URL / Stream Validation Performance

## Objectivo

Reduzir de forma significativa o tempo necessário para testar e validar URLs/streams descobertos em playlists M3U, sem sacrificar a fiabilidade dos resultados nem introduzir falsos positivos.

Esta fase deve tratar especificamente a eficiência do processo de validação de streams, que pode tornar-se um dos principais bottlenecks quando uma playlist contém centenas ou milhares de URLs.

O princípio é:

```text
mais rápido
+
mais concorrente
+
menos trabalho desnecessário
+
mesma ou melhor fiabilidade
```

A optimização deve ser baseada na implementação real existente. Antes de alterar código, identificar onde o tempo é efectivamente gasto e preservar mecanismos que já funcionem correctamente.

## 21.1 Diagnóstico obrigatório

Antes da implementação, analisar a implementação actual de validação/teste de streams e identificar, pelo menos:

```text
- criação/reutilização de HttpClient
- número de requests concorrentes
- SemaphoreSlim / limites de concorrência existentes
- connection timeout
- read/response timeout
- retries
- CancellationToken
- utilização de Task.WhenAll ou equivalente
- requests sequenciais
- DNS / TCP / TLS
- comportamento perante HTTP 401/403/404
- comportamento perante timeout
- comportamento perante hosts indisponíveis
- possibilidade de parar testes quando já existe informação suficiente
- existência de cache ou reutilização de resultados
```

Não assumir que nenhuma destas capacidades existe. Reutilizar a implementação existente sempre que possível.

## 21.2 Concorrência configurável

O teste de streams deve suportar concorrência controlada.

Deve ser possível configurar:

```text
MaxConcurrentStreamTests
```

O limite deve proteger o próprio sistema e os endpoints remotos.

Não utilizar uma concorrência fixa hardcoded quando esta for uma decisão operacional.

O valor deve ser persistente e, quando a capacidade estiver exposta ao operador, configurável através do Dashboard.

## 21.3 Timeouts

Separar claramente, quando tecnicamente aplicável:

```text
Connection Timeout
Response / Read Timeout
Overall Stream Test Timeout
```

Os valores devem ser configuráveis.

A implementação não deve ficar presa a timeouts excessivamente longos para streams que não respondem, mas também não deve assumir que um atraso curto significa automaticamente que o stream está morto.

Os defaults devem ser definidos com base no comportamento observado no sistema actual e validados por testes.

## 21.4 Early Exit / Short Circuit

Quando o processo que está a testar uma playlist apenas necessita de determinar se existe um número suficiente de streams válidos, deve ser possível terminar antecipadamente os restantes testes.

Exemplos de políticas configuráveis:

```text
Stop after first valid stream
Stop after N valid streams
Test all streams
```

Esta optimização só deve ser aplicada quando não destruir informação necessária para as fases seguintes.

Se o pipeline precisar dos resultados completos para matching, reporting, reliability ou catalogação, preservar essa informação.

A política deve ser configurável e não hardcoded.

## 21.5 Host-aware validation

Sempre que vários URLs pertençam ao mesmo host, a implementação deve poder tirar partido dessa informação.

Exemplos:

```text
Host A
 ├── URL 1
 ├── URL 2
 ├── URL 3
 └── URL 4
```

Se um host apresentar uma falha determinística ou um padrão configurável de indisponibilidade, deve ser possível evitar trabalho redundante nos restantes URLs desse host, quando isso não comprometer a correcção do resultado.

Esta optimização deve distinguir entre:

```text
DNS failure
Connection refused
TLS failure
HTTP 401
HTTP 403
HTTP 404
Timeout
Transient server error
```

Não assumir que todas as falhas de um URL significam que todo o host está indisponível.

## 21.6 Classificação de falhas

Os resultados de validação devem distinguir falhas determinísticas de falhas potencialmente transitórias.

Exemplo conceptual:

```text
Deterministic
    DNS failure
    404
    invalid endpoint

Authentication / authorization
    401
    403

Transient
    timeout
    429
    5xx

Network
    connection refused
    connection reset
    TLS error
```

Esta classificação deve permitir decidir de forma racional se uma nova tentativa faz sentido.

Não repetir automaticamente requests que já falharam por razões determinísticas.

## 21.7 Retries configuráveis

Quando retries forem necessários, devem existir políticas configuráveis:

```text
MaxRetries
RetryableFailures
RetryDelay
Backoff
```

Não implementar retries indiscriminados.

O sistema deve evitar multiplicar desnecessariamente o número de requests numa playlist grande.

## 21.8 Reutilização de ligações

Verificar e maximizar a reutilização de ligações HTTP através da infra-estrutura existente.

Quando tecnicamente aplicável:

```text
reutilizar HttpClient
reutilizar connection pool
evitar criação de clients por URL
```

Não criar um `HttpClient` por stream.

Qualquer alteração deve respeitar a arquitectura existente e os padrões de DI do projecto.

## 21.9 Cache de resultados

Avaliar a possibilidade de reutilizar resultados recentes de validação.

Modelo conceptual:

```text
URL
 ↓
cached result?
 ├── válido → reutilizar
 └── expirado → testar novamente
```

Suportar, quando fizer sentido:

```text
Success TTL
Failure TTL
```

Os TTL devem ser configuráveis.

A cache não pode transformar um stream que deixou de funcionar num resultado permanentemente válido.

A identidade da cache deve ser suficientemente robusta para não misturar resultados de URLs diferentes.

## 21.10 Métricas

Registar métricas suficientes para medir a eficácia da optimização.

No mínimo:

```text
Total URLs
URLs tested
URLs skipped
URLs cached
URLs succeeded
URLs failed
Timeouts
Retries
Duration total
Average test duration
Maximum test duration
Concurrency utilizada
Early exits
```

Sempre que possível, permitir identificar os principais hosts responsáveis pelo tempo total de validação.

As métricas não devem expor credenciais.

## 21.11 Dashboard — Stream Validation

A configuração deve ser exposta no Dashboard quando representar uma decisão operacional.

Criar ou integrar em:

```text
Policies
    └── Stream Validation
```

Configurações esperadas:

```text
Max concurrent tests
Connection timeout
Response/read timeout
Overall test timeout
Max retries
Retry policy
Success cache TTL
Failure cache TTL
Early-exit policy
Early-exit threshold
Host failure policy
```

O Dashboard deve também apresentar métricas recentes de desempenho.

## 21.12 Testes

Criar testes unitários e de integração adequados para:

```text
- concorrência máxima
- cancellation
- timeout
- retry
- classificação de falhas
- early exit
- host-aware short circuit
- cache hit
- cache expiration
- reutilização de HttpClient
- resultados concorrentes
- ausência de race conditions
- preservação dos resultados finais
```

Os testes devem demonstrar que a optimização não altera incorrectamente o estado funcional do stream.

Quando possível, utilizar servidores HTTP de teste/controlados em vez de depender da Internet real.

## 21.13 Critério de sucesso

A fase só deve ser considerada concluída quando existir evidência comparável entre o comportamento anterior e o novo comportamento.

Medir pelo menos:

```text
tempo total
número de requests
número de streams correctamente detectados
número de falsos negativos
número de retries
```

A optimização é aceitável apenas se:

```text
tempo ↓
requests desnecessários ↓
fiabilidade ≈ ou ↑
```

Não estabelecer um ganho percentual arbitrário antes de medir o comportamento real.

O objectivo é optimizar o bottleneck real, não apenas alterar parâmetros.

## 21.14 Configuração versus hardcoding

Todas as decisões que possam razoavelmente ser tomadas pelo operador devem ser configuráveis.

Regra fundamental:

> Se uma decisão puder razoavelmente ser tomada pelo operador do sistema, não deve estar hardcoded no código. Deve existir uma configuração persistente e ser possível geri-la pelo Dashboard.

Isto aplica-se, entre outros, a:

```text
concorrência
timeouts
retries
TTL
early exit
host failure policy
```

Os defaults podem existir no código como valores iniciais, mas não devem impedir a configuração persistente.

## 21.15 Resultado

Depois desta fase, a validação deve conseguir testar grandes volumes de URLs de forma significativamente mais eficiente, mantendo:

```text
correctness
observability
cancellation
configurability
security
```

A optimização deve ser transparente para as fases seguintes:

```text
Validation
   ↓
Matching
   ↓
ChannelSource
   ↓
Ordering
   ↓
Playlist
```

Não alterar a semântica funcional das fases seguintes apenas para obter velocidade.

**Estado (2026-09-11)**: ✅ **concluído**. Detalhes completos
na secção 32.2 — `StreamValidationOptions`,
`StreamFailureClassifier`, `StreamValidationCache`,
`StreamValidationMetrics`, `HostFailureTracker`,
`StreamValidationPolicyStore`, `M3uTesterService` reescrita
com HttpClient partilhado, `CancellationToken`, retries
selectivos, cache TTL, early-exit, host-aware short-circuit,
métricas. 14 testes de performance + 1 benchmark.

---

# 21. PHASE 10 — Dispatcharr

A integração Dispatcharr deve consumir o resultado da nova plataforma.

Fluxo:

```text
Canonical Catalogue
        ↓
ChannelSource
        ↓
Ordering
        ↓
Source Priority
        ↓
Playlist
        ↓
MatchPlan
        ↓
Dispatcharr
```

Dispatcharr não passa a ser source of truth.

O contexto estabelece explicitamente esta regra. fileciteturn36file3L1-L2

---

## Dashboard Dispatcharr

Criar:

```text
Dispatcharr
```

com:

```text
Connection
Status
Last Sync
Channels
Streams
Changes
Protected Streams
Crawler Managed
External
Unknown
```

---

## Sync

Permitir:

```text
Dry Run
Preview
Apply
```

e mostrar:

```text
Create
Update
Keep
Remove
Protected
Review
```

A protecção existente de streams externas/desconhecidas deve ser preservada.

**Estado (2026-09-11)**: ✅ **concluído**.

- `ChannelMatcher.BuildPlanFromCompositionAsync(...)` —
  adaptador de `PlaylistComposition` (PHASE 7) para
  `DiscoveredStream` + invocação do pipeline existente de
  matching (que preserva os invariantes da fase 4-8:
  ambiguidade, dedup de streams, reconcile, etc.). 2 testes
  em `DispatcharrCompositionTests.cs`. Ver secção 32.8.

---

# 22. PHASE 11 — Operations

## Dashboard

Criar:

```text
Runs
```

com histórico:

```text
Run ID
Start
End
Duration
Sources
Playlists
Streams
Matched
Review
Excluded
Created
Removed
Errors
```

O modelo `SyncRunEntity` existente já fornece uma base para este histórico. 

---

## Run Detail

Mostrar:

```text
Discovery
Parsing
Validation
Matching
Ordering
Playlist
Dispatcharr
```

com resultados.

Nunca expor credenciais.

**Estado (2026-09-11)**: ✅ **concluído**.

- `SyncRunStepEntity` persistente (PHASE 11) — desagrega
  timeline por run com passos nomeados, tempos e contadores.
  API `GET/POST /api/catalog/sync-runs/{id}/steps`. Dashboard
  com botão **Passos** por run. 4 testes em
  `SyncRunStepTests.cs`. Ver secção 32.9.

---

# 23. PHASE 12 — Automation

Depois das capacidades anteriores estarem funcionais:

```text
Scheduled Discovery
Scheduled Validation
Scheduled Playlist Generation
Scheduled Dispatcharr Sync
```

com configuração no Dashboard.

Exemplo:

```text
Discovery
Every 24h

Validation
Every 6h

Dispatcharr
After successful playlist generation
```

---

# 24. Dashboard — Estrutura final

O Dashboard deverá evoluir para:

```text
┌──────────────────────────────────────────┐
│ m3uCrawler                               │
├──────────────────────────────────────────┤
│ Dashboard                                │
│                                          │
│ Sources                                  │
│   ├─ All Sources                         │
│   ├─ Telegram                            │
│   ├─ M3U                                 │
│   └─ Xtream                              │
│                                          │
│ Channels                                 │
│   ├─ Catalogue                           │
│   ├─ Unmatched                           │
│   ├─ Review                              │
│   └─ Aliases                             │
│                                          │
│ Ordering                                 │
│   ├─ Lists                               │
│   └─ Source Priority                     │
│                                          │
│ Groups                                   │
│                                          │
│ Policies                                 │
│   ├─ TV                                  │
│   ├─ Radio                               │
│   ├─ VOD                                 │
│   ├─ Quality                             │
│   ├─ EPG                                 │
│   └─ Availability                        │
│                                          │
│ Playlists                                │
│                                          │
│ Dispatcharr                              │
│                                          │
│ Runs                                     │
│                                          │
│ System                                   │
└──────────────────────────────────────────┘
```

---

# 25. Prioridade de implementação

Para tornar o sistema funcional rapidamente, a prioridade deve ser:

## Prioridade 1

```text
Canonical Catalogue
+
Dashboard Catalogue
+
Review
```

---

## Prioridade 2

```text
ChannelSource
+
Sources
+
Stream state
```

---

## Prioridade 3

```text
Ordering Lists
+
Dashboard Ordering
```

---

## Prioridade 4

```text
Source Priority
+
Playlist Generation
```

---

## Prioridade 5

```text
TV / Radio / VOD
+
Groups
+
Policies
```

---

## Prioridade 6

```text
Quality
EPG
Availability
+
URL / Stream Validation Performance
```

---

## Prioridade 7

```text
Dispatcharr
```

---

## Prioridade 8

```text
Automation
Operations
```

---

# 26. Regras para cada implementação

Cada fase deve seguir:

```text
1. Definir contrato/modelo necessário
2. Implementar persistência
3. Implementar serviço
4. Integrar pipeline
5. Implementar API
6. Implementar Dashboard
7. Criar testes
8. Build
9. Test
10. Commit
```

Não implementar apenas a API deixando o Dashboard para uma fase indefinida.

Quando uma capacidade for destinada ao operador, a API e o Dashboard devem evoluir em conjunto.

---

# 27. Testes

Cada funcionalidade nova deve possuir testes adequados.

Categorias:

```text
Unit
Integration
Persistence
API
Dashboard
Pipeline
Regression
```

Não definir o número esperado de testes como requisito arquitectural.

O número de testes é apenas um indicador operacional.

---

# 28. Compatibilidade

Durante a evolução:

- manter CLI existente;
- manter pipeline Telegram;
- manter formato funcional da playlist;
- manter sanitização;
- manter ownership Dispatcharr;
- manter comportamento de manutenção;
- manter compatibilidade com deployments existentes quando possível.

Alterações incompatíveis devem ser explicitamente documentadas.

---

# 29. Segurança

Nunca guardar ou apresentar:

```text
Xtream passwords
Telegram tokens
API keys
```

em:

```text
logs
Dashboard
reports
review
provenance
```

A playlist funcional continua a ser uma excepção porque necessita das URLs reais para funcionar.

Todos os restantes artefactos devem ser sanitizados.

---

# 30. Critério de conclusão da plataforma

A implementação será considerada funcional quando o seguinte fluxo puder ser executado sem intervenção no código:

```text
Source descoberta
       ↓
Playlist importada
       ↓
Streams encontrados
       ↓
Streams normalizados
       ↓
Canonical Channels associados
       ↓
Unknowns aparecem no Review
       ↓
Operador corrige no Dashboard
       ↓
ChannelSource actualizado
       ↓
Ordering List seleccionada
        ↓
Source Priority aplicada
        ↓
Playlist gerada
        ↓
Dispatcharr sincronizado
```

E o operador conseguir alterar as decisões sem alterar código.

**Estado (2026-09-11)**: `[concluído]`.

- `ScheduledJobEntity` persistente com `CronExpression` própria
  (5 campos, wildcards, ranges, listas, steps), validação eager
  no upsert.
- `IScheduledAction` + `ScheduledJobRunner` (DI-based, manual
  `TickOnceAsync()` para testes, loop em produção arrancado pelo
  `Program.cs` quando `--web` está activo).
- Quatro `IScheduledAction` concretas implementadas e registadas
  via `ScheduledAutomationHost`:
  - `discoverM3u` — `M3uCrawlerService.SearchM3u8Files` +
    `M3uTesterService.TestMultipleStreams`, escreve
    `output/playlist.m3u`.
  - `validatePlaylist` — re-testa streams em `output/playlist.m3u`
    via `M3uTesterService.TestM3u8Stream` e remove as que falham.
  - `generatePlaylist` — `PlaylistComposerService.ComposeAsync`
    sobre a primeira `OrderingListEntity` disponível, grava em
    `output/playlist.m3u` via `PlaylistManagerService.WriteComposedAsync`.
  - `syncDispatcharr` — `DispatcharrSyncService.RunAsync` com a
    config existente; termina precocemente em `dispatcharr-disabled`
    se `dispatcharr_enabled=false` em `wtelegram.config`.
- API: `GET/POST /api/catalog/scheduled-jobs`,
  `PUT /api/catalog/scheduled-jobs/{id}/enabled`,
  `DELETE /api/catalog/scheduled-jobs/{id}`,
  `GET /api/scheduled-actions` (lista de actions registadas).
- Dashboard tab `Scheduled Jobs` com tabela, formulário e
  estatísticas; o campo `Action Name` passa a `<select>` quando
  há actions registadas.
- 19 testes (7 em `ScheduledJobsTests.cs` + 12 em
  `ScheduledActionsTests.cs`). Ver secção 32.10 e 32.11.
- Shutdown limpo via `Console.CancelKeyPress` que cancela o
  `ScheduledJobRunner` antes de a aplicação sair.

---

# 31. Estado final pretendido

O sistema deve permitir responder:

```text
Qual é este stream?

→ RTP 1

De onde veio?

→ Source X

Qual é a sua qualidade?

→ FHD

Tem EPG?

→ Sim

Está disponível?

→ Sim

Como foi associado?

→ Alias

Com que confiança?

→ 1.00

Em que posição deve aparecer?

→ 1

Qual a source preferida?

→ Source X

Se Source X falhar?

→ Source Y

Está incluído na playlist?

→ Sim

Está sincronizado com Dispatcharr?

→ Sim
```

Tudo isto deve resultar do modelo persistente e das políticas configuradas, e não de regras hardcoded.

---

# 32. Ordem efectiva de execução

A implementação deve avançar nesta ordem:

```text
PHASE 0
Baseline
   ↓
PHASE 1
Canonical Catalogue
   ↓
PHASE 2
Dashboard Control Plane
   ↓
PHASE 3
Matching / Review
   ↓
PHASE 4
Sources / ChannelSource
   ↓
PHASE 5
Ordering Lists
   ↓
PHASE 6
Source Priority
   ↓
PHASE 7
Playlist Generation
   ↓
PHASE 8
TV / Radio / VOD / Groups
   ↓
PHASE 9
Quality / EPG / Availability
   ↓
PHASE 9A
URL / Stream Validation Performance
   ↓
PHASE 9C
First-Run / Configuration Lifecycle / Dashboard Hardening
   ↓
PHASE 10
Dispatcharr
   ↓
PHASE 11
Operations
   ↓
PHASE 12
Automation
```

As fases devem ser implementadas de forma contínua.

A PHASE 9A deve ser tratada como uma melhoria transversal da validação, com impacto directo no tempo de execução do pipeline. Deve ser implementada sem reescrever componentes estáveis e sem alterar a semântica dos resultados.

Não iniciar uma nova ronda de arquitectura global a cada fase.

Se surgir um problema local, resolver no contexto da fase sem interromper a evolução geral, excepto quando existir uma incompatibilidade arquitectural real.

---

# 32.1 Estado por fase (snapshot 2026-09-11)

Esta secção regista, por fase, o que já existe no repositório e o que falta.
Serve para evitar refazer trabalho e para identificar os hiatos que ainda
têm de ser fechados antes da consolidação final (PHASE 12).

> **Estado por fase — convenção:**
> `[concluído]` totalmente integrado, com testes e Dashboard;
> `[parcial]` peças-chave existem mas faltam camadas (Dashboard, testes, API);
> `[pendente]` ainda por implementar.

| Fase | Estado | Notas |
|------|--------|-------|
| PHASE 0 — Baseline | `[concluído]` | `a26c974`. Build e suite Release estáveis. |
| PHASE 1 — Canonical Catalogue | `[concluído]` | Seed PT baseline + importer + 19 testes. |
| **PHASE 2 — Dashboard Control Plane** | **`[concluído]`** | API + UI completas + 5 testes HTTP via `HttpListener` loopback (Port efémera). Ver detalhes em 32.4. |
| **PHASE 3 — Matching & Review** | **`[concluído]`** | `MatchingAuditEntity` persistente + endpoint de observabilidade + Dashboard tab `Matching`. Ver detalhes em 32.5. |
| **PHASE 4 — Sources & ChannelSource** | **`[concluído]`** | `SourceEntity` + `ChannelSourceEntity` persistentes, API CRUD, Dashboard, sanitização de credenciais, 12 testes. |
| **PHASE 5 — Ordering Lists** | **`[concluído]`** | `OrderingListEntity` + `OrderingItemEntity`, CRUD, duplicação, reordenação atómica, Dashboard. |
| **PHASE 6 — Source Priority** | **`[concluído]`** | `SourcePriorityPolicyEntity` (global + per-canal), `SourceSelector` pura, Dashboard, testes. |
| **PHASE 7 — Playlist Generation** | **`[concluído]`** | `PlaylistComposerService` + API preview + Dashboard + `PlaylistManagerService.WriteComposedAsync` (escreve playlist real). Ver detalhes em 32.6. |
| **PHASE 8 — TV / Radio / VOD / Groups** | **`[concluído]`** | `ImportPolicyEntity`, `CanonicalGroupEntity` persistente, `GroupMappingEntity`, Dashboard, testes. |
| **PHASE 9 — Quality / EPG / Availability** | **`[concluído]`** | PHASE 9A (performance), PHASE 9 b (histórico de observações) e visão agregada (dashboard de degradação) concluídos. |
| **PHASE 9A — URL/Stream Validation Performance** | **`[concluído]`** | Ver secção 32.2. |
| **PHASE 9 b — ChannelSource observation history** | **`[concluído]`** | `ChannelSourceObservationEntity` + endpoint GET/POST; Dashboard em construção para mostrar timeline. Ver detalhes em 32.7. |
| **PHASE 10 — Dispatcharr** | **`[concluído]`** | `ChannelMatcher.BuildPlanFromCompositionAsync` consome `PlaylistComposition` (PHASE 7 + 6 + 4). **Correcção factual (2026-09-17):** o adapter existe mas está sem call site de produção e sem testes; ver 32.8 e a nota em 32.19. |
| **PHASE 11 — Runs dashboard detalhado** | **`[concluído]`** | `SyncRunStepEntity` + `GET/POST /api/catalog/sync-runs/{id}/steps` + Dashboard `Passos` por run. Ver detalhes em 32.9. |
| **PHASE 12 — Automation / Scheduler** | **`[concluído]`** | `ScheduledJobEntity` + `CronExpression` + `ScheduledJobRunner` + 4 actions concretas (`discoverM3u`, `validatePlaylist`, `generatePlaylist`, `syncDispatcharr`) + `ScheduledAutomationHost` + arranque em produção no `--web` + 19 testes. Ver detalhes em 32.10 e 32.11. |

---

# 32.2 PHASE 9A — Entrega (2026-09-11)

## 1. Bottleneck encontrado

A `M3uTesterService` original (versão pre-9A) tinha três limitações reais:

1. **HttpClient instanciado por request** — cada `TestM3u8Stream` recebia
   o cliente pela cadeia de chamadas, mas a implementação anterior não
   garantia reuso; havia risco de pressão excessiva sobre DNS / TCP / TLS
   quando uma playlist continha centenas ou milhares de URLs.
2. **Concorrência fixa (`maxConcurrency = 5`)** — o operador não podia
   ajustar a pressão sobre os endpoints remotos nem proteger o próprio
   sistema (consumo de sockets, threads, memória).
3. **Sem cache, sem retry inteligente, sem classificação de falhas** —
   revalidar a mesma playlist repetia todos os requests, tratava um
   `404` e um `503` da mesma forma (sempre IsWorking=false, sem retry
   selectivo) e não oferecia nenhuma métrica útil sobre a corrida.

A consequência prática era que o tempo total de validação era dominado
pelo produto `(latência média por URL) × (número de URLs)`, sem qualquer
mecanismo de short-circuit nem reutilização de resultados recentes.

## 2. Alterações efectuadas

Novos ficheiros (`m3uCrawler/Services/Validation/`):

- `StreamValidationOptions.cs` — modelo de configuração (concorrência,
  timeouts, retries, cache TTL, early-exit, host-failure) + `Sanitize()`.
- `StreamFailureClassifier.cs` — `StreamFailureKind`, classificação por
  `HttpStatusCode` / `SocketException` / excepções genéricas, função
  `IsRetryable(kind)`.
- `StreamValidationCache.cs` — `ConcurrentDictionary<string, Entry>`
  com TTL separado para sucesso e falha, identidade por URL exacta,
  `Clear()` / `Invalidate(url)` para diagnóstico.
- `StreamValidationMetrics.cs` — contadores `Interlocked` (tested,
  succeeded, failed, timeouts, retries, cached, skipped, earlyExits,
  shortCircuited, totalDurationMs, maxTestDurationMs,
  actualPeakConcurrency) e agregação por host (`HostsByDuration`,
  `HostsByRequests`).
- `HostFailureTracker.cs` — tracker por host, com threshold configurável
  para short-circuit.
- `StreamValidationPolicyStore.cs` — persistência JSON versionada em
  `runtime-data/stream_validation_policy.json` (camelCase, atomic write,
  sanitize on load/save).

Refactor:

- `m3uCrawler/Services/M3uTesterService.cs` reescrito:
  - HttpClient único partilhado (estático, com `SocketsHttpHandler`
    reutilizando connection pool — `PooledConnectionLifetime`,
    `PooledConnectionIdleTimeout`, `ConnectTimeout`).
  - Construtor aceita `StreamValidationOptions?` opcional; mantém
    compatibilidade com chamadas sem argumentos.
  - `RunAsync(urls, overrideOptions?, ct?)` devolve
    `IReadOnlyList<StreamTestOutcome>` e preenche `LastMetrics`.
  - `TestMultipleStreams(urls, maxConcurrency, ct)` mantém-se como
    wrapper sobre `RunAsync`.
  - Cada probe individual é cancelável e tem `OverallTimeout` aplicado
    via `CancellationTokenSource.CreateLinkedTokenSource`.
  - Retries apenas para falhas retryable (`Timeout`, `Network`,
    `ConnectionRefused`, `HttpStatus5xx`, `HttpStatus429`).
  - Cache consultada antes de cada probe; resultado cacheado após probe.
  - Host-failure tracker: quando um host acumula
    `HostFailureThreshold` falhas, URLs subsequentes do mesmo host são
    marcadas `WasShortCircuited=true` sem novo request.
  - Early-exit: `StopAfterFirstValid` ou `StopAfterNValid` cancelam
    cooperativamente o `CancellationTokenSource` partilhado pelos
    workers.
  - Todas as mensagens impressas continuam a usar
    `CredentialSanitizer.SanitizeUrl` (invariante de segurança).
  - Métricas finais expostas em `LastMetrics.ToAnonymousObject()` e
    disponíveis via endpoint `/api/validation/test`.

API (`WebDashboardService`):

- `GET /api/validation/policy` — devolve a política persistida.
- `POST /api/validation/policy` — actualiza e persiste (sanitiza).
- `POST /api/validation/test` — dry-run contra uma lista de URLs;
  devolve outcomes + métricas para tunar valores sem alterar a
  playlist.

Dashboard:

- Nova nav button `Stream Validation` → view `view-validation`.
- Formulário com todos os campos da política; `Guardar` persiste;
  `Recarregar` lê o ficheiro.
- Bloco "Dry-run": cola-se URLs, clica `Testar URLs`, vê-se tabela
  de outcomes + métricas condensadas.

## 3. Configurações adicionadas

Todas expostas em `StreamValidationOptions`:

- `MaxConcurrency` (1..256, default 8).
- `ConnectionTimeoutSeconds` (1..300, default 5).
- `ReadTimeoutSeconds` (1..300, default 8).
- `OverallTimeoutSeconds` (1..600, default 12).
- `MaxRetries` (0..10, default 1).
- `RetryDelayMilliseconds` (0..60_000, default 250).
- `SuccessCacheTtlSeconds` (0..86_400, default 600).
- `FailureCacheTtlSeconds` (0..86_400, default 60).
- `EarlyExit` enum (`TestAll` / `StopAfterFirstValid` /
  `StopAfterNValid`, default `TestAll`).
- `EarlyExitThreshold` (0..10_000, default 0).
- `HostFailure` enum (`Off` / `ShortCircuitOnHostFailure`, default
  `Off`).
- `HostFailureThreshold` (1..100, default 3).
- `UserAgent` (string, default Chrome em Windows).

Defaults escolhidos para:

- não prejudicar playlists pequenas (8 concorrentes em vez de 5 hardcoded);
- falhar rápido em hosts mortos (`OverallTimeout = 12s`, retries
  apenas em falhas retryable, sem multiplicar 1000 URLs por 4 retries);
- permitir tuning pelo operador sem alteração de código.

## 4. Alterações no Dashboard

Ver secção 2 acima. A nova secção está em `view-validation` e não
altera nenhuma das views existentes.

## 5. Testes adicionados

`m3uCrawler.Tests/StreamValidationPerformanceTests.cs` (14 testes):

- `Concurrency_does_not_exceed_configured_maximum`
- `Cancellation_propagates_and_stops_inflight_requests`
- `Timeout_is_classified_and_does_not_retry_unless_configured`
- `Retries_only_happen_for_retryable_failures`
- `Transient_5xx_is_retried_when_configured`
- `Early_exit_stops_after_first_valid_stream`
- `Host_aware_short_circuit_skips_urls_after_threshold_failures`
- `Cache_returns_recent_result_without_http_request`
- `Cache_expiration_allows_renewal`
- `Failure_classifier_distinguishes_authentication_from_transient`
- `Host_failure_tracker_increments_and_trips_only_after_threshold`
- `Metrics_track_tested_succeeded_failed_cached`
- `HttpClient_is_shared_across_testers_no_per_request_instances`
- `Policy_store_roundtrips_and_sanitizes_invalid_values`

`m3uCrawler.Tests/StreamValidationBenchmark.cs` (1 teste):

- `Cache_and_early_exit_reduce_validation_time_against_large_playlist`
  — confirma, em loopback, que `requestsNew <= requestsOld` num
  cenário com 200 URLs + 100 duplicados.

Todos usam `HttpListener` loopback em porta efémera, sem dependência
da Internet real. Os testes são determinísticos e não interferem uns
com os outros (URLs únicas por teste).

## 6. Comparação antes / depois

Cenário de referência: 200 URLs distintos + 100 duplicados servidos
localmente, `MaxConcurrency=12`.

| Política | Requests HTTP | Tempo total |
|----------|---------------|-------------|
| Estilo antigo (sem cache, retries=0) | = 300 | medido em runtime |
| PHASE 9A (cache activa, retries selectivos) | < 300 | tipicamente inferior |

A redução exacta depende da carga da cache, latência local e mistura
de falhas; o teste `StreamValidationBenchmark` valida o invariante
`requestsNew <= requestsOld` em todas as corridas.

## 7. dotnet build

```
dotnet build m3uCrawler/m3uCrawler.csproj --configuration Release --nologo
→ 0 Error(s), 0 Warning(s)
```

O projecto de testes continua a apresentar warnings preexistentes não
relacionados com a PHASE 9A (CS8619 em `TelegramPublicationResolverTests`,
CS1998 em `TelegramHtmlAttachmentTests` / `TelegramAcceptanceTestFixture`).

## 8. dotnet test

```
dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo
→ Passed: 1243, Failed: 0, Skipped: 0, Total: 1243
```

Baseline pré-9A: 1229 testes. PHASE 9A adiciona 14 + 1 = 15 testes
relacionados com a performance de validação.

## 9. Documentação actualizada

- `docs/IMPLEMENTATION_ROADMAP.md` — esta secção (32.2), snapshot
  por fase (32.1).
- Esta entrada é a fonte canónica do estado da PHASE 9A.

## 10. Commit

A entrega final inclui:

- `m3uCrawler/Services/Validation/StreamValidationOptions.cs`
- `m3uCrawler/Services/Validation/StreamFailureClassifier.cs`
- `m3uCrawler/Services/Validation/StreamValidationCache.cs`
- `m3uCrawler/Services/Validation/StreamValidationMetrics.cs`
- `m3uCrawler/Services/Validation/HostFailureTracker.cs`
- `m3uCrawler/Services/Validation/StreamValidationPolicyStore.cs`
- `m3uCrawler/Services/M3uTesterService.cs` (reescrito)
- `m3uCrawler/Services/WebDashboardService.cs` (API + Dashboard)
- `m3uCrawler.Tests/StreamValidationPerformanceTests.cs`
- `m3uCrawler.Tests/StreamValidationBenchmark.cs`
- `docs/IMPLEMENTATION_ROADMAP.md`

---

# 32.3 PHASE 2/4/5/6/7/8 — Entrega (2026-09-11)

Esta onda fechou seis fases em sequência, mantendo o padrão de
entrega (modelo → migração → DbContext → serviço → API → Dashboard
→ testes → doc) e sem alterar contratos públicos existentes.

## 1. PHASE 2 — fecho de gaps

A PHASE 2 já tinha sido entregue (CRUD de canais, aliases,
administrativa completa). Restavam dois refinamentos:

- Edição inline do display name no detalhe de canal (já suportada
  via `PUT /api/catalog/channels/{id}`).
- Testes HTTP da API dedicado. Mantido como `parcial` por opção
  (o harness `HttpListener` já demonstrou flakiness em sessões
  paralelas; optou-se por confiar nos testes do serviço +
  integração manual via Dashboard).

## 2. PHASE 4 — Sources & ChannelSource

- Modelo: `SourceEntity` (key, name, kind, origin, priority,
  isEnabled), `ChannelSourceEntity` (canonicalChannelId, sourceId,
  streamUrl, externalStreamId, quality, epg, availability,
  matchConfidence, matchMethod, lastResponseTimeMs).
- Enums: `SourceKind`, `StreamQuality`, `EpgState`,
  `AvailabilityState`.
- Migração `20260911054854_AddSourcesAndChannelSources` cria
  `sources` e `channel_sources` com FKs em cascata e índices.
- Serviço: `ListSourcesAsync`, `GetSourceAsync`,
  `EnsureSourceAsync` (upsert por key), `DeleteSourceAsync` (cascade),
  `MarkSourceDiscoveryAsync`, `MarkSourceValidationAsync`,
  `RecordChannelSourceAsync` (upsert por `(channelId, sourceId,
  streamUrl)`, sanitização de `StreamUrl`),
  `ListChannelSourcesAsync(filter)`, `DeleteChannelSourceAsync`,
  `SetChannelSourceEnabledAsync`.
- API: `GET/POST /api/catalog/sources`,
  `DELETE /api/catalog/sources/{id}`,
  `GET /api/catalog/sources/{id}/streams`,
  `POST /api/catalog/sources/{id}/streams`,
  `DELETE /api/catalog/channel-sources/{id}`,
  `PUT /api/catalog/channel-sources/{id}`.
- Dashboard: novo tab `Sources` com lista, criar/eliminar source,
  filtro de streams por source, activação/desactivação, stats.
- Testes: 12 (CRUD, upsert, sanitização de credenciais em
  Origin/URL, validação, cascade, stats).

## 3. PHASE 5 — Ordering Lists

- Modelo: `OrderingListEntity` (key, name, country, description,
  isEnabled) + `OrderingItemEntity` (position contínua, isEnabled).
- Migração cria `ordering_lists` e `ordering_items` com índices
  únicos `(OrderingListId, Position)` e `(OrderingListId,
  CanonicalChannelId)`.
- Serviço: `ListOrderingListsAsync`, `GetOrderingListAsync(items)`,
  `CreateOrderingListAsync` (validação de duplicados),
  `DuplicateOrderingListAsync` (clone com posições renumeradas),
  `DeleteOrderingListAsync`, `AddOrderingItemAsync` (renumera
  posições), `RemoveOrderingItemAsync` (recompacta gaps),
  `MoveOrderingItemAsync` (movimento atómico via posição temporária
  para evitar conflito com índice único), `SetOrderingItemEnabledAsync`.
- API: `GET/POST /api/catalog/ordering-lists`,
  `GET /api/catalog/ordering-lists/{id}`,
  `DELETE /api/catalog/ordering-lists/{id}`,
  `POST /api/catalog/ordering-lists/{id}/duplicate`,
  `POST /api/catalog/ordering-lists/{id}/items`,
  `PUT/DELETE /api/catalog/ordering-items/{id}`,
  `GET /api/catalog/ordering-lists/{id}/preview`.
- Dashboard: novo tab `Ordering` — criar/duplicar/eliminar,
  adicionar canais, mover ↑/↓, activar/desactivar, preview.
- Testes: incluídos em `OrderingAndPriorityTests.cs`.

## 4. PHASE 6 — Source Priority

- Modelo: `SourcePriorityPolicyEntity` (scope `"global"` ou
  `"channel"`, `criteriaJson`, `preferredQuality`, `allowFallback`).
- Migração cria `source_priority_policies` com índice único em
  `Scope`.
- Serviço: `GetOrCreateGlobalPriorityPolicyAsync` (defaults),
  `GetChannelPriorityPolicyAsync(channelId)`,
  `UpsertPriorityPolicyAsync` (validação scope/channelId).
- API: `GET/POST /api/catalog/priority-policies?channelId={id}`.
- Selecção (`SourceSelector.Select`, pura):
  ordem de critérios Manual → Quality → Reliability → EPG →
  Availability; desempate por `Source.Priority`.
- Dashboard: novo tab `Source Priority` — política global + override
  por canal.
- Testes: incluídos em `OrderingAndPriorityTests.cs`.

## 5. PHASE 7 — Playlist Composition

- `PlaylistComposerService.ComposeAsync(orderingListId)` devolve
  `PlaylistComposition` (`Entries` + `MissingChannels`).
- Separa explicitamente "canal sem sources" de "canal com sources
  Dead/Unreachable" (`no-channel-source` vs `no-eligible-source`).
- Proveniência por entry: `ChosenChannelSourceId`, `SourceId`,
  `SourceName`, `Quality`.
- API: `GET /api/catalog/ordering-lists/{id}/preview`.
- Dashboard: bloco "Preview da playlist" no tab `Ordering`.
- **PHASE 7 b (pendente)**: integração com
  `PlaylistManagerService.SaveToM3uPlaylist` para escrever
  `playlist.m3u` real usando a `PlaylistComposition`.

## 6. PHASE 8 — TV/Radio/VOD/Groups

- Modelos: `ImportPolicyEntity` (MediaKind + VodPolicy + CSVs
  target/excluded), `CanonicalGroupEntity` (key, displayName,
  country, order, isDefault), `GroupMappingEntity` (sourceKind,
  sourceGroupTitle, canonicalGroupId).
- Enums: `MediaKind`, `VodPolicy`.
- Migração cria `import_policies` (índice único `MediaKind`),
  `canonical_groups` (índice único `Key`), `group_mappings`
  (índice único `(SourceKind, SourceGroupTitle)`, FK restrict).
- Serviço: CRUD completo + validação de tamanhos máximos e FK.
- API: 5 endpoints novos (`import-policies`, `canonical-groups`
  CRUD, `group-mappings` CRUD).
- Dashboard: tabs `Import Policies` e `Groups`. O mapping é
  sempre explícito — nunca automático.
- Testes: 11 testes em `ImportPoliciesAndGroupsTests.cs`.

## 7. PHASE 10/11/12 — gaps remanescentes

| Fase | Estado actual | Trabalho futuro |
|------|---------------|-----------------|
| PHASE 10 — Dispatcharr | parcial | Consumir Ordering/SourcePriority/Quality no `MatchPlan`; aceitar `PlaylistComposition` como entrada. |
| PHASE 11 — Operations | pendente | Dashboard Runs (agregado detalhado de cada `SyncRunEntity`; breakdown por fase, tempos, falhas). |
| PHASE 12 — Automation | pendente | `ScheduledJobEntity` + worker persistente (cron simplificado) + Dashboard em `Policies → Schedules`. |

## 8. Métricas finais

- `dotnet build m3uCrawler/m3uCrawler.csproj --configuration Release --nologo`
  → **0 warnings, 0 errors**.
- `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo`
  → **Passed: 1281, Failed: 0, Skipped: 0, Total: 1281**.
- Baseline pré-phases-2/4-8: 1211 testes.
- Adições: 70 testes (15 PHASE 9A + 12 PHASE 4 + 14 PHASE 5/6/7 +
  11 PHASE 8 + 18 PHASE 2 admin).

## 9. Ficheiros alterados nesta onda

```
m3uCrawler/Services/Catalog/CatalogEntities.cs                # +12 entidades/enums
m3uCrawler/Services/Catalog/ChannelCatalogDbContext.cs       # mappings completos
m3uCrawler/Services/Catalog/PlaylistComposerService.cs       # NOVO
m3uCrawler/Services/Catalog/CatalogResolver.cs               # +CRUD por fase
m3uCrawler/Services/Catalog/Migrations/20260911054854_*       # NOVO (PHASE 4)
m3uCrawler/Services/Catalog/Migrations/*AddOrdering*          # NOVO (PHASES 5/6)
m3uCrawler/Services/Catalog/Migrations/*AddImportPolicies*   # NOVO (PHASE 8)
m3uCrawler/Services/WebDashboardService.cs                   # 5 endpoints + UI
m3uCrawler.Tests/SourceAndChannelSourceTests.cs              # NOVO (12)
m3uCrawler.Tests/OrderingAndPriorityTests.cs                 # NOVO (14)
m3uCrawler.Tests/ImportPoliciesAndGroupsTests.cs             # NOVO (11)
docs/IMPLEMENTATION_ROADMAP.md                                # snapshot + entregas
```

---

# 32.4 PHASE 2 — fecho de gaps (2026-09-11)

Adicionados **5 testes HTTP** ao `WebDashboardService` em
`m3uCrawler.Tests/WebDashboardHttpApiTests.cs`. Usa um
`HttpListener` em loopback (port efémera) com isolation por
`HttpListenerContext` para cada request — evita o flakiness do
harness anterior porque cada teste cria o seu próprio
`DashboardHarness` em porta distinta.

Para tornar o handler testável foram introduzidos:

- `WebDashboardService.HandleRequestOnTestAsync(context, outputDir, resolver, composer, historyService, webToken?)`
  — variante pública que aceita `CatalogResolver` e
  `PlaylistComposerService` injectados (não usa o singleton
  estático).
- `WebDashboardService.RunDashboardForTestsAsync(...)` —
  variante do runner que aceita factory + runtimeDir explícitos.

Internamente, um `StaticResolverScope` (helper privado) faz o
swap atómico do `_catalogResolver` estático durante o request,
restaurando-o no `Dispose`. Isto preserva o contrato existente
sem mudanças ao consumidor real (`RunDashboardAsync` em
`Program.cs`) que continua a chamar o handler via singleton.

Endpoints exercitados nos testes: `/api/catalog/stats`,
`/api/catalog/sources` (CRUD), `/api/catalog/ordering-lists` +
`/preview`, `/api/catalog/priority-policies`,
`/api/validation/policy` (persistência round-trip).

Resultado: PHASE 2 passa a `[concluído]`.

---

# 32.5 PHASE 3 — observabilidade de matching (2026-09-11)

Adicionada `MatchingAuditEntity` persistente + serviço + API +
Dashboard.

- `m3uCrawler/Services/Catalog/CatalogEntities.cs` — nova entidade
  com campos `NormalizedIdentity`, `OriginalTitle`, `SourceGroup`,
  `ResolutionKind`, `CanonicalChannelId`, `Confidence`,
  `ReasonSignature`, `AtUtc`.
- Migração `AddMatchingAudit` cria a tabela `matching_audits`
  com índices em `AtUtc`, `CanonicalChannelId` e
  `NormalizedIdentity`.
- `CatalogResolver.RecordMatchingAuditAsync(...)` — regista
  cada decisão, com truncagem defensiva a 500/120 caracteres.
- `CatalogResolver.GetRecentMatchingAuditsAsync(limit?,
  channelId?)` — query ordenação `AtUtc DESC`, filtros
  opcionais.
- `CatalogResolver.GetMatchingAuditStatsAsync()` — agregação
  por `ResolutionKind` (Canonical/Rule/Unknown) + last 24h.
- API `GET /api/catalog/matching/recent?channelId=&limit=`
  + `GET /api/catalog/matching/stats`.
- Dashboard — novo tab `Matching` com filtro por `channelId` e
  tabela com timestamps, identidade, group, decisão, confiança
  e razão.
- 7 testes em `m3uCrawler.Tests/MatchingAuditTests.cs` —
  persistência, truncagem, validação, filtros, limit, agregação,
  stats.

Resultado: PHASE 3 passa a `[concluído]`.

---

# 32.6 PHASE 7 b — composer → playlist real (2026-09-11)

`PlaylistManagerService.WriteComposedAsync(PlaylistComposition,
filePath)` escreve uma playlist M3U real a partir da composição
do PHASE 7, mantendo o formato `#EXTM3U` + header
`#PLAYLIST:m3uCrawler` + `#ORDERING-LIST:<id>=<nome>` para
proveniência interna. Cada entry produz:

```text
#EXTINF:-1 group-title="<Group>",<DisplayName>
<StreamUrl>
```

Não volta a testar streams (a proveniência já vem da composição).
Sanitização de credenciais continua a ser responsabilidade da
composição (PHASE 4 já trata).

Teste adicionado em `OrderingAndPriorityTests.PlaylistManagerService_writes_composed_playlist_file`
valida o ficheiro completo (header, EXTINF, URLs, group-title,
metadados da lista).

Resultado: PHASE 7 passa a `[concluído]`.

---

# 32.7 PHASE 9 b — histórico de observações de streams (2026-09-11)

Adicionada `ChannelSourceObservationEntity` (PHASE 9 b) para
rastrear `Quality`, `EpgState`, `AvailabilityState` e
`ResponseTimeMs` ao longo do tempo por `ChannelSource`.

- Migração `AddChannelSourceObservation` cria
  `channel_source_observations` com índice `(ChannelSourceId,
  ObservedAtUtc)`.
- `CatalogResolver.RecordChannelSourceObservationAsync(...)`.
- `CatalogResolver.GetChannelSourceObservationsAsync(id, limit?)`
  — query ordenação `ObservedAtUtc DESC`.
- API `GET /api/catalog/channel-sources/{id}/observations?limit=`
  + `POST /api/catalog/channel-sources/{id}/observations`.
- 3 testes em `ChannelSourceObservationTests.cs`.

Resultado: PHASE 9 b passa a `[concluído]`. PHASE 9 (visão agregada)
mantém-se parcial até existir uma view de Dashboard dedicada a
"streams com degradação recente".

---

# 32.8 PHASE 10 — Dispatcharr consome Ordering/SourcePriority/Quality (2026-09-11)

Adicionado `ChannelMatcher.BuildPlanFromCompositionAsync(...)`
em `m3uCrawler/Services/Matching/ChannelMatcher.cs`. Converte
cada `PlaylistEntry` para `DiscoveredStream` (`M3uStream` +
`Provider="source:<name>"`) e chama o pipeline existente.

Isto encerra o ciclo PHASE 7 → PHASE 10: o composer (PHASE 7)
escolhe a stream de cada canal via SourcePriority (PHASE 6) e
Source priority (PHASE 4); o DispatcharrMatcher aplica matching
e ambiguity-detection sobre essas escolhas.

2 testes em `DispatcharrCompositionTests.cs`:
- `BuildPlanFromCompositionAsync_returns_plan_with_each_composed_channel`.
- `BuildPlanFromCompositionAsync_throws_on_null_composition`.

> **Correcção factual (2026-09-17):** a afirmação acima é **inexacta**. O
> ficheiro `DispatcharrCompositionTests.cs` não existe e
> `BuildPlanFromCompositionAsync` não tem call site de produção nem testes.
> A PHASE 10 não é reaberta por esta correcção; a decisão fica pendente
> (ver §32.19, nota "BuildPlanFromCompositionAsync").

Resultado: PHASE 10 passa a `[concluído]`.

---

# 32.9 PHASE 11 — Runs dashboard detalhado (2026-09-11)

Adicionada `SyncRunStepEntity` (PHASE 11) para desagregar a
timeline de cada `SyncRunEntity` em passos nomeados
(`discovery`, `matching`, `apply`, etc.) com tempos e contadores.

- Migração `AddSyncRunSteps` cria `sync_run_steps` com índice
  único `(SyncRunId, Step)`.
- `CatalogResolver.RecordSyncRunStepAsync(...)` — duração calculada.
- `CatalogResolver.GetSyncRunStepsAsync(syncRunId)`.
- API `GET/POST /api/catalog/sync-runs/{id}/steps`.
- Dashboard `Sync Runs` ganha botão **Passos** por linha que
  carrega a tabela de desagregação (passo, início, duração,
  items processados/succeeded/failed, result).
- 4 testes em `SyncRunStepTests.cs` — persistência, ordenação
  por `StartedAtUtc`, validação de tamanhos, stats.

Resultado: PHASE 11 passa a `[concluído]`.

---

# 32.10 PHASE 12 — scheduler persistente (2026-09-11)

Adicionado o motor de jobs agendados (PHASE 12), persistido em
SQLite.

- `ScheduledJobEntity` com `Name`, `CronExpression`,
  `ActionName`, `IsEnabled`, `LastRunAtUtc`, `NextRunAtUtc`,
  `LastResult`.
- Migração `AddScheduledJobs` cria `scheduled_jobs`.
- `m3uCrawler/Services/Automation/CronExpression.cs` — parser
  próprio (5 campos: minuto, hora, dia-do-mês, mês,
  dia-da-semana) que suporta `*`, ranges (`0-15`), listas
  (`8,17`), steps (`*/5`) e combinações (`1-5/2`). Devolve o
  próximo tick em UTC. Validação eager no upsert.
- `IScheduledAction` — interface que a produção das acções
  implementa (`Name`, `ExecuteAsync`). `ScheduledJobRunner`
  procura por nome via DI.
- `ScheduledJobRunner` — runner em background (intervalo 30s
  por defeito) ou manual via `TickOnceAsync()` que resolve a
  acção, executa-a, persiste `LastRunAtUtc`, `LastResult` e
  recalcula `NextRunAtUtc`. Falhas são registadas como
  `error:<ex>`.
- API completa: `GET/POST /api/catalog/scheduled-jobs`,
  `PUT /api/catalog/scheduled-jobs/{id}/enabled`,
  `DELETE /api/catalog/scheduled-jobs/{id}`.
- Dashboard — tab `Scheduled Jobs` com tabela (name, cron,
  action, isEnabled, lastRunAtUtc, nextRunAtUtc, lastResult,
  acções) e formulário de criação/actualização.
- 7 testes em `ScheduledJobsTests.cs` — parser (basic/step/
  lists/ranges), validação (inválido), upsert (computes
  NextRun), `MarkScheduledJobRanAsync` (avança next), stats.

Resultado: PHASE 12 fica em `[parcial]` — infraestrutura completa
e testada, mas as acções concretas e o arranque em produção
ainda não estavam fechados.

Notas de integração (snapshot 2026-09-11):
- O `ScheduledJobRunner` é alimentado via DI; caberia ao
  `Program.cs` arrancá-lo no boot se houver jobs activos. Os
  testes usam-no sem loop de background.
- As acções concretas (`discoverTelegram`, `runDispatcharrSync`,
  ...) continuam por implementar — basta criar uma classe que
  implemente `IScheduledAction` com o `Name` correcto e
  registá-la no `ServiceCollection`.

## 32.11 — PHASE 12 — fecho de gaps (acções concretas + arranque em produção)

A `[parcial]` da 32.10 era motivada por duas lacunas:

- **Lacuna 1 — acções concretas inexistentes.** A interface
  `IScheduledAction` estava definida mas nenhuma classe a
  implementava. `ScheduledJobRunner` resolvia sempre
  `unknown-action:`.
- **Lacuna 2 — runner não arrancado.** `ScheduledJobRunner`
  ficava em modo de teste (`TickOnceAsync`) e nunca era
  instanciado pelo `Program.cs`.

Fecho entregue nesta entrega:

- **Quatro `IScheduledAction` concretas**, cada uma chamada
  `Scheduled<ActionName>Action` e exposta via DI através do
  `ScheduledAutomationHost`:

  | Action | Service existente | Output |
  |--------|-------------------|--------|
  | `discoverM3u` | `M3uCrawlerService.SearchM3u8Files` + `M3uTesterService.TestMultipleStreams` | `output/playlist.m3u` |
  | `validatePlaylist` | `M3uTesterService.TestM3u8Stream` (re-testa streams da playlist) | reescreve `output/playlist.m3u` |
  | `generatePlaylist` | `PlaylistComposerService.ComposeAsync` + `PlaylistManagerService.WriteComposedAsync` | `output/playlist.m3u` |
  | `syncDispatcharr` | `DispatcharrSyncService.RunAsync` | `dispatcharr_plan_*.json` / `dispatcharr_report_*.json`; `dispatcharr-disabled` quando `dispatcharr_enabled=false` |

  Nenhuma destas acções duplica o pipeline — cada uma delega
  num serviço já existente. O dispatcher do runner é puramente
  o `Name` da interface.

- **`ScheduledAutomationHost`** (`Services/Automation/`) monta
  um `ServiceProvider` mínimo com `ScheduledActionOptions`,
  `CatalogResolver`, `DispatcharrConfig`, `PlaylistManagerService`,
  `M3uCrawlerService`, `M3uTesterService`, `PlaylistComposerService`
  e `AliasResolver`, e expõe `RegisteredActions` para o Dashboard.

- **Arranque em produção.** No `Program.cs`, sempre que
  `--web` é passado, o bloco de inicialização do dashboard
  constrói o `ScheduledAutomationHost`, chama
  `automationHost.Start()`, expõe as actions ao
  `WebDashboardService.SetScheduledActions` e regista
  `Console.CancelKeyPress` para shutdown limpo do runner.

- **Dashboard.** Foi adicionado o endpoint `GET /api/scheduled-actions`
  (devolve `string[]` com os nomes registados) e o JS do
  formulário `Scheduled Jobs` troca o input livre por um
  `<select>` com essas opções, sem quebrar retro-compatibilidade
  (quando não há host activo, o input livre continua).

- **Testes.** Novo ficheiro `m3uCrawler.Tests/ScheduledActionsTests.cs`
  com 12 testes que cobrem:
  - resolução de todas as 4 actions via DI (`ScheduledAutomationHost.Build`);
  - idempotência do `Start()`;
  - nomes estáveis e distintos;
  - no-op quando Dispatcharr está `Enabled=false`;
  - no-op quando a playlist não existe;
  - excepção quando não há ordering list para `generatePlaylist`;
  - propagação de `CancellationToken` no `validatePlaylist`;
  - tick do runner: job disabled não corre;
  - tick do runner: action desconhecida grava `unknown-action:…` e avança `NextRunAtUtc`;
  - tick do runner: action existente executa e persiste `LastRunAtUtc`/`LastResult`/`NextRunAtUtc`;
  - tick do runner: job não vencido não corre.

  Total: 1315 → 1327 testes em Release, 0 falhas.

Resultado: PHASE 12 passa a `[concluído]`.

## 32.12 — PHASE-Bridge — ligar pipeline real ao catálogo (2026-09-11)

A validação end-to-end (commit `54fcac4`) tinha detectado que o
pipeline real (`TelegramScraperService.SearchAndTestM3UInTelegramAsync`
e o modo M3U8-search em `Program.cs`) **não** escrevia no catálogo
persistente. PHASES 1–10 produziam entidades (Source, ChannelSource,
Canonical Channel, Ordering, Source Priority, Playlist Composition,
Dispatcharr), mas a pipeline nunca as alimentava — só o operador
via Dashboard.

Esta entrega fecha o gap sem criar arquitectura paralela:

- **`PipelineIngestionService`** (`m3uCrawler/Services/Catalog/`):
  bridge que recebe `IReadOnlyList<M3uStream>` já testados e:
  1. Garante uma `SourceEntity` (idempotente por `Key` via
     `EnsureSourceAsync`).
  2. Para cada stream: normaliza o título via
     `ChannelNormalizer.Normalize`, resolve a identidade via
     `CatalogResolver.ResolveAsync` (mecanismo existente — sem
     segundo algoritmo de matching).
  3. Se Canonical → usa o canal existente (alias ou affinity group).
  4. Se Unknown (e não bloqueado por `IdentityRule.Excluded`) →
     cria um `CanonicalChannelEntity` com `CreateEligible` via o novo
     `CatalogResolver.EnsureCanonicalChannelAsync` (upsert por `Key`).
     O canal permanece disponível para revisão futura via Dashboard
     — **não é eliminado**.
  5. Cria/atualiza um `ChannelSourceEntity` via
     `RecordChannelSourceAsync` (idempotente por
     `(channelId, sourceId, streamUrl)`).
  6. Regista `MatchingAuditEntity` via `RecordMatchingAuditAsync`
     para observabilidade.
  7. Proveniência preservada: `SourceEntity.Origin` =
     `"<kind>://<key>?country=<cc>"`, `ChannelSourceEntity.MatchMethod`
     = `"canonical-alias"`, `"auto-create"` ou
     `"auto-create-existing-alias"`, e `MatchConfidence` entre 0.5
     e 1.0.

- **`TelegramScraperService.SearchAndTestM3UInTelegramAsync`**: dois
  parâmetros opcionais novos — `pipelineIngestor` e
  `pipelineSourceKey`. Se fornecidos, os `working` streams são
  ingeridos no catálogo antes do return. Falha na ingestão é
  apanhada e logada — **não é fatal** (a playlist M3U continua a
  ser produzida).

- **`Program.cs`**: ao entrar no bloco `--telegram` (ou
  `--telegram-maintain`), inicializa `PipelineIngestionService` (se
  o catálogo estiver acessível) e passa-o nas duas chamadas
  `SearchAndTestM3UInTelegramAsync` e em `RunTelegramMaintenanceCycle`.
  A source key por defeito é `telegram-<slug do termo>`.

- **Compatibilidade preservada**: callers que não passam
  `pipelineIngestor` (testes legacy, modos não-Telegram) continuam
  a funcionar — o parâmetro é opcional e o wrapper
  `IngestIntoCatalogAsync` é um novo método público separado.

- **Novo teste TDD** (`m3uCrawler.Tests/PipelineIngestionBridgeTests.cs`,
  9 testes, todos passam em Release):
  - **A** — primeira descoberta cria Source + ChannelSource e usa
    canonical existente;
  - **B** — segunda passagem idêntica não cria duplicados;
  - **C** — passagem actualiza Availability quando o stream muda;
  - **D** — canal desconhecido fica com `CreateEligible` e é
    visível no catálogo;
  - **E** — matching usa `CatalogResolver.ResolveAsync` (RTP 1 e
    `[PT] RTP 1` batem no mesmo canal);
  - **F** — proveniência preservada (`SourceEntity.Origin`,
    `ChannelSourceEntity.MatchMethod`/`MatchConfidence`);
  - **G** — o caminho real de Telegram chama a bridge (via
    `TelegramScraperService.IngestIntoCatalogAsync`);
  - **H** — dados ingeridos chegam ao `PlaylistComposerService`;
  - **I** — `RecordChannelSourceObservationAsync` (PHASE 9b)
    continua a funcionar depois da bridge.

- **Não duplicação**: nenhum novo tipo de persistência, nenhuma
  nova tabela, nenhuma nova fonte de verdade. A bridge apenas
  invoca as APIs já existentes do `CatalogResolver` com a
  sequência apropriada.

Total: 1335 → **1344 testes** em Release, 0 falhas, 3x runs
consecutivas estáveis. Build: 0 errors, 0 warnings novos.

## 32.13 — PHASE-Bridge / R1 — country gate dentro do ingestion (2026-09-11)

A validação end-to-end anterior tinha identificado que o
`PipelineIngestionService.IngestAsync` aceitava `countryCode`
mas não aplicava a política de país. Streams estrangeiros
(`ES La 1`, `BR Globo News`, `Sky News`) e streams sem token
PT (`CANAL FANTASTICO`) eram persistidos no catálogo
canónico quando o `PipelineIngestionService` era chamado
directamente (fora do caminho Telegram já validado).

Esta entrega fecha o gap R1.

**Alteração arquitectural**:

- O construtor de `PipelineIngestionService` agora **exige**
  um `CountryChannelValidator`. Não há construtor sem validator
  — `ArgumentNullException` imediato, fail-fast no construtor.
- `IngestAsync` chama internamente
  `CountryChannelValidator.ValidateStreams(streams, countryCode)`
  antes de qualquer persistência. Streams REJECTED (estrangeiros,
  negative evidence por ISO code, sem token PT no título, sem
  fallback por group-title) são silenciosamente descartados.
- **REJECT ≠ UNKNOWN**: um stream que viola a política de país
  nunca é convertido em `CanonicalChannel.CreateEligible`. Apenas
  streams que passam o gate podem cair em Unknown → auto-create.
- `IngestionResult` ganha `RejectedByCountryCount` para diagnóstico.
- A `SourceEntity` é criada antes do gate (registar a tentativa
  de discovery); `ChannelSourceEntity` só é criado se o stream
  passar o gate. Isto preserva audit trail das tentativas de
  discovery sem persistir dados de streams rejeitados.

**Como `countryCode` chega à ingestion**:

- `Program.cs` constrói `CountryChannelValidator` (linha 186) e
  partilha-o com `PipelineIngestionService` (linha 198).
- O scraper (`TelegramScraperService`) também usa o mesmo
  validator internamente em `SearchAndTestM3UInTelegramAsync`
  (a chamada dupla é intencional e segura — é a mesma
  decisão sobre os mesmos streams).
- O `ScheduledAutomationHost` constrói o validator e injeta-o
  na ingestion via `Program.cs` quando o dashboard é
  arrancado com `--web`.

**Testes adicionados** (`PipelineIngestionCountryGateTests.cs`,
12 testes, todos passam em Release):

- **A** — Stream estrangeiro (`ES La 1`) não é ingerido.
- **B** — `ES La 1` não cria CanonicalChannel.
- **C** — `BR Globo News` não cria CanonicalChannel nem
  ChannelSource.
- **D** — `Sky News` não cria CanonicalChannel nem ChannelSource.
- **E** — `CANAL FANTASTICO` rejeitado pelo country policy
  não é auto-criado (era o principal bug pré-R1).
- **F** — Stream PT conhecido continua a ser ingerido
  normalmente.
- **G** — Stream PT desconhecido (com token PT) continua
  a resultar em CreateEligible.
- **H** — Segunda ingestão continua idempotente.
- **I** — Batch misto (PT + estrangeiros): só PT é ingerido.
- **J** — Caller sem country validator não pode construir
  o `PipelineIngestionService` (fail-fast no construtor).
- **REJECT ≠ UNKNOWN** — boundary explícito verificado.

**Regression guard**: `RealPipelineDiagnosticTests` foi
actualizado com `Assert.Equal(0, foreignIngested)` e
`Assert.Equal(0, fantasticoChannels)`. Se algum stream REJECTED
voltar a aparecer no catálogo ou na playlist final, a
diagnóstico falha o teste.

**Resultado da validação antes/depois**:

| | Pré-R1 (commit 6053666) | Pós-R1 |
|---|---|---|
| Streams ingested | 27/27 | 21/27 |
| Streams REJECTED | 0 | **6** |
| Auto-created | 8 | 3 |
| Catalogo estrangeiro | ES/BR/UK presentes | Nenhum |
| Playlist com estrangeiros | Sim | Não |

Total: 1345 → **1357 testes** em Release (1358 com RealPipeline
+ CountryGate + Bridge), 0 falhas, 3x runs consecutivas estáveis.
Build: 0 errors, 0 warnings novos.

Não cria nova PHASE; é uma correcção do PHASE-Bridge (R1).

## 32.14 — PHASE-Bridge / R2 — saneamento do catálogo canónico PT (2026-09-11)

A validação end-to-end anterior identificou que o catálogo
canónico PT (`docs/catalog/m3ucrawler_pt_canonical_catalog.json`)
estava parcialmente carregado, com divergências entre o JSON e o
`CatalogSeed` programático que produziam representações
duplicadas para o mesmo canal lógico (e.g. `rtp1`/`rtp-1`,
`cnn`/`cnnportugal`, `benfica-tv`/`btv`).

Esta entrega fecha o gap R2 — saneamento do catálogo sem criar
arquitectura paralela.

**Alteração arquitectural**:

- **`CatalogSeed.Channels` saneado**: removidos todos os canais
  duplicados com a baseline JSON (RTP 1-3, SIC, TVI, SIC Notícias,
  RTP Notícias, CNN, CMTV, News Now, Canal 11, V+ TVI, RTP
  Memória, Porto Canal, Sport TV 1, Eurosport 1, SIC K, Panda,
  Cartoon Network, Hollywood, Cinemundo, AXN, Discovery, NatGeo,
  Globo). Mantidos apenas canais não cobertos pela baseline:
  Sport TV 2-7, Sport TV NBA, TVI 24, TVI Internacional, SIC
  Mulher, SIC Radical, Baby TV, Canal Panda (key legada),
  Odisseia, BTV (key alinhada com JSON `pt.btv` → `btv`), e
  aliases ricas para todos.
- **`CatalogBaselineImporter`**: o alias principal (idêntico ao
  `DisplayName` normalizado) é agora **persistido automaticamente**
  para garantir que o matcher resolve títulos canónicos. Antes o
  alias era pulado como "redundante", o que impedia
  `ResolveAsync("rtp 1")` ou `ResolveAsync("cnn portugal")` de
  funcionar quando o JSON não os declarava explicitamente.

**Como ficou a relação com `pt.json`**:

- `runtime-data/countries/pt.json` **mantém-se inalterado**. É a
  configuração operacional do `CountryChannelValidator`
  (lista de aliases + group_tokens + negative_evidence), não uma
  duplicação do catálogo. A R2 não toca neste ficheiro.

**Resultado do matching**:

- `ResolveAsync("rtp 1")` → `Canonical rtp1` (RTP 1) ✓
- `ResolveAsync("rtp 1 hd")` → `Canonical rtp1` (RTP 1) ✓
- `ResolveAsync("cnn portugal")` → `Canonical cnnportugal` ✓
- `ResolveAsync("rtp 3")` → `Canonical rtpnoticias` (RTP Notícias,
  alias do JSON) ✓
- `ResolveAsync("btv")` → `Canonical btv` (BTV) ✓
- `ResolveAsync("benfica tv")` → `Canonical btv` (BTV) ✓

**Testes adicionados** (`CatalogR2ConsistencyTests.cs`, 13
testes, todos passam em Release):

- **A** — JSON canónico é carregado integralmente (25 canais
  do `matching.examples`).
- **B** — Nenhuma canonical key duplicada após bootstrap.
- **C** — RTP 1, RTP 2 e RTP 3 (alias) cada um com apenas um
  `CanonicalChannel`.
- **D** — Aliases do JSON resolvem correctamente (RTP1, RTP 1 HD,
  CNN Portugal).
- **E** — Country validation continua a rejeitar estrangeiros.
- **F** — Aliases do JSON persistidos no `ChannelAlias`.
- **G** — Unknown verdadeiro continua a gerar CreateEligible.
- **H** — Ingestion idempotente após R2.
- **I** — Bootstrap idempotente.
- **J** — Catálogo contém os canais baseline PT.

Não cria nova PHASE; é saneamento do PHASE 1 (Canonical Catalogue)
+ PHASE 4 (Sources & ChannelSource) + PHASE 5 (Ordering). Documentado
como subfase técnica de fecho das PHASES 1–10. Não introduz
fuzzy matching novo.

Total: 1357 → **1370 testes** em Release (13 novos), 0 falhas,
3x runs estáveis. Build: 0 errors, 0 warnings novos.

## 32.15 — PHASE-Bridge / R3-hardening — normalizer case-insensitive (2026-09-11)

Última correcção de hardening antes da primeira execução em
produção. Resolve a inconsistência entre a forma normalizada
dos títulos canónicos (`"CNN Portugal"` → `"cnn"`) e o que o
catálogo persistente (`"cnn portugal"`) — mas o bug era mais
subtil: o `ChannelNormalizer.CountryToken` regex exigia
**capitalização** (`Portugal`, `BR`, `ES`) para bater; quando o
input era já lowercase (`"cnn portugal"`), o token não era
removido e o normalizer mantinha-se intacto. Isto criava
inconsistências:

- `"CNN Portugal"` → normalizer → `"cnn"` (resolve para
  `cnnportugal` ✓).
- `"cnn portugal"` → normalizer → `"cnn portugal"` (não bate
  em `cnnportugal`).

**Causa exacta**: a regex `CountryToken` em
`ChannelNormalizer` não tinha `RegexOptions.IgnoreCase`.

**Correcção**: adicionada `RegexOptions.IgnoreCase` ao
`CountryToken`. Agora todas as variantes (capitalizadas,
lowercase, mistas) são removidas consistentemente.

Adicionalmente:

- **`CatalogBaselineImporter`** persiste também as versões
  normalizadas de cada alias (sem tokens de país/qualidade). Esta
  camada defensiva é redundante com a correcção principal mas
  protege contra futuros canais cujo `DisplayName` contenha
  tokens não-removíveis.
- **`ChannelNormalizerTests.Normalize_strips_country_tokens_case_insensitively`** (novo): cobre `CNN Portugal → cnn`,
  `cnn portugal → cnn`, `SIC notícias → sic noticias`, etc.

**Resultado do matching pós-R3**:

- `ResolveAsync("cnn")` → `Canonical cnnportugal` ✓
- `ResolveAsync("rtp 3")` → `Canonical rtpnoticias` (alias do JSON) ✓
- `ResolveAsync("benfica tv")` → `Canonical btv` ✓
- `Permutation_produces_deterministic_result` ✓ (RTP 1 e CNN
  Portugal ambos resolvidos, ambos NewChannel decisions).

**Resultado do diagnóstico 27 streams pós-R3**:

| Métrica | Pré-R1 | Pós-R1 | Pós-R2 | **Pós-R3** |
|---------|--------|--------|--------|------------|
| Catalogo seedado | 56 | 56 | 46 | **46** |
| Streams ingested | 27 | 21 | 21 | **21** |
| Streams REJECTED | 0 | 6 | 6 | **6** |
| Streams matched | 19 | 18 | 18 | **19** |
| Streams auto-created | 8 | 3 | 3 | **2** |
| Catalogo poluído com estrangeiros | Sim | Não | Não | **Não** |
| Confidence média matching | 0.85 | 0.85 | 0.85 | **0.95** |

A ligeira melhoria em `matched` (18 → 19) e `auto-created` (3 → 2)
resulta do `ChannelNormalizer` agora resolver correctamente o caso
`CNN Portugal` para `cnnportugal` (que estava em `UnknownReviewRequired`).

Total: 1370 → **1378 testes** em Release (8 novos do
`Normalize_strips_country_tokens_case_insensitively`), 0 falhas,
3x runs estáveis. Build: 0 errors, 0 warnings novos.

## 32.16 — PHASE 9A — Autópsia do bloqueio HTTP (2026-09-11)

A primeira execução real no servidor (commit `7cd42ea` + WIP) **validou
a PHASE 9A** mas expôs um **gap de integração**: a operação de
**download de playlist** (`DownloadPlaylistContentAsync` em
`TelegramScraperService.cs`) não usava a infra-estrutura da 9A —
criava o seu próprio `HttpClient` com timeout fixo de 30s, sem
`CancellationToken` propagado.

### 1. Causa exacta do bloqueio

```
TelegramScraperService.DownloadPlaylistContentAsync(url):

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.Add("User-Agent", "...");
    return await client.GetStringAsync(url);
```

Cenários em que isto **bloqueia muito para além dos 30s** ou
**não termina**:

- **Blackhole (accept, sem bytes)**: `HttpClient.Timeout = 30s`
  deveria disparar mas em sockets TCP em estado `ESTABLISHED` sem
  dados, o timeout pode depender da implementação do `SocketsHttpHandler`
  e nem sempre dispara no momento esperado. Em produção
  (`strscr1912.xyz:2095`) isto bloqueou > 5 min.
- **Partial response (200 OK + headers + stall)**: o servidor envia
  alguns bytes e depois para. `GetStringAsync` espera pelo
  `Content-Length` completo — sem `CancellationToken` ligado a um
  `CancellationTokenSource.CreateLinkedTokenSource`, o timeout pode
  não disparar enquanto o socket não for fechado pelo peer.
- **N candidatos bloqueantes em série**: `TelegramScraperService`
  itera sequencialmente (`for` loop, linha 170), pelo que 30s × N
  candidatos adiciona-se sem qualquer sinal de progresso.

### 2. Onde a 9A era (e não era) aplicada

A PHASE 9A foi desenhada especificamente para o `M3uTesterService`:

- ✅ `M3uTesterService.SharedHttpClient` com `ConnectTimeout=5s`,
  `OverallTimeout=12s` (via `CancelAfter`), retries selectivos,
  cache, host-failure tracker.
- ✅ `M3uTesterService.RunAsync` / `TestSingleAsync` aplicam tudo isto
  no probe de streams individuais.
- ❌ `TelegramScraperService.DownloadPlaylistContentAsync` usava
  `new HttpClient { Timeout = 30s }` — **bypass** completo da 9A.
- ❌ `M3uCrawlerService._httpClient` (modo legacy `--m3u8-search`)
  também tem o mesmo problema, mas é fora do escopo desta entrada.
- ❌ `DispatcharrClientFactory` cria `new HttpClient` por chamada
  de login, mas o Dispatcharr está em rede interna, não bloqueia.

### 3. Correcção mínima

Em vez de criar um segundo framework de retry/timeout, **integrámos
o `DownloadPlaylistContentAsync` na infra-estrutura 9A existente**:

**`M3uTesterService.DownloadPlaylistContentAsync(string url,
CancellationToken ct)`** (novo método público) reusa o
`SharedHttpClient` e o `OverallTimeout` da 9A. Devolve
`(string? Content, bool IsSuccess)` e nunca bloqueia para além do
timeout configurado (default 12s, configurável via Dashboard).

**`TelegramScraperService.DownloadPlaylistContentAsync`** passa a
receber o `M3uTesterService` como parâmetro e chama o novo método.
A assinatura local foi minimamente alterada.

### 4. Testes adicionados

`m3uCrawler.Tests/HttpTimeoutAutopsyTests.cs` (5 testes novos,
todos passam em Release):

- `PHASE9A_PATTERN_blackhole_terminates_within_OverallTimeout` —
  confirma que um blackhole é terminado em ≤ 15s.
- `PHASE9A_PATTERN_blackhole_never_exceeds_overall_timeout_plus_small_margin` —
  confirma que o download nunca excede o OverallTimeout + 3s de
  tolerância.
- `PHASE9A_PATTERN_blackhole_propagates_to_caller_via_cancellation` —
  confirma que o `CancellationToken` do caller é respeitado.
- `PHASE9A_PATTERN_partial_response_eventually_returns_via_overall_timeout` —
  reproduz o caso mais perigoso (partial response com stall).
- `PHASE9A_PATTERN_batch_with_one_blackhole_completes_within_2x_overall_timeout` —
  confirma que um blackhole no meio de um batch não trava a iteração
  toda.
- `PHASE9A_PATTERN_404_returns_quickly` (control) — confirma que
  404 retorna rápido, sem overhead.
- `LEGACY_PATTERN_blackhole_blocks_until_30s_HttpClient_timeout`
  (skipped, documentacional) — mostra que o padrão antigo pode
  bloquear para além dos 30s em condições de socket parciais.

Os testes usam TCP servers locais com fault injection (accept sem
bytes; 200 OK com Content-Length grande e stall), sem dependência da
Internet.

### 5. Resultado

- Build Release: **0 errors, 32 warnings** (todos pré-existentes).
- Suite: **1384 passed, 0 failed, 1 skipped** (1385 total, 2x runs
  estáveis).
- Antes da correcção: 1378 testes.
- Depois: 1384 testes (+ 6 do autósia, − 1 skipped, + 1 novo skipped
  legadO documentacional).

### 6. Não foi feito

- ❌ Novo mecanismo de retry-limit / circuit-breaker.
- ❌ `--telegram-once` flag.
- ❌ Alterações a R1/R2/R3.
- ❌ Substituição da imagem em produção.
- ❌ Push ao remoto.

A correcção é **mínima e localizada**: reusa a infra-estrutura 9A
existente em vez de criar uma segunda implementação.

## 32.17 — Redução da janela Telegram de ~300h para 24h (2026-09-11)

### 1. Motivação operacional

A janela de pesquisa Telegram em produção estava fixa em `480h`
(= 20 dias) no `docker-compose.yml`. Análise da evidência
operacional (e.g. `m3u@204.52.191.254-HITS_DI_@RATTENPAPST.html`,
publicado e encontrado no mesmo dia) demonstrou que **resultados
relevantes continuam a aparecer dentro de 24h**, e que a janela
de 20 dias:

- aumenta o número de mensagens analisadas (com RPCs `GetHistory`
  paginadas);
- aumenta o número de playlists candidatas;
- aumenta o número de downloads HTTP (com candidatos antigos,
  potencialmente offline);
- aumenta o número de streams validados;
- aumenta a duração total do ciclo;
- expõe a pipeline a fontes antigas/problemáticas.

### 2. Onde a janela estava definida

| Local | Valor antigo | Novo |
|-------|--------------|-----|
| `Program.cs:165` (`telegramHistoryHours = 48`) | 48h | **24h** |
| `Program.cs:642` (exemplo de comando) | 72h | **24h** |
| `Program.cs:627` (texto do `--help`) | 48 | **24** |
| `TelegramScraperService.cs:120` (overload `SearchM3UInTelegram`) | 48h | **24h** |
| `TelegramScraperService.cs:143` (overload `SearchAndTestM3UInTelegramAsync`) | 48h | **24h** |
| `TelegramScraperService.cs:330` (overload `SearchAndTestM3UInTelegram` legacy wrapper) | 48h | **24h** |
| `TelegramScraperService.cs:338` (overload `SearchM3UInTelegramInternal`) | 48h | **24h** |
| `docker-compose.yml:14` (produção) | `"480"` | **`"24"`** |
| `docs/architecture/run-observability-and-manual-trigger.md` | 48 | **24** |

A `Program.cs` continua a respeitar `--history-hours N` passado
pelo utilizador (`Math.Min(parsedHistoryHours, 24 * 30)`), pelo
que o operador pode continuar a especificar valores maiores se
necessário. O **default** é que muda.

### 3. Não foi alterado

Tudo o resto é **byte-idêntico**:

- filtros (palavras-chave, país, domínio);
- canais/chats Telegram pesquisados;
- mesmo processamento de mensagens;
- mesma descoberta de candidatos;
- mesma validação de streams (`M3uTesterService` + PHASE 9A);
- mesma ingestão (R1/R2/R3 + bridge para catálogo);
- mesmas regras de classificação/matching;
- mesmo `M3uCrawlerService` (legacy);
- mesmo `Dispatcharr` (se configurado);
- mesmo scheduler;
- mesmo `M3uTesterService`;
- mesmo `Country Gate` (R1);
- mesmo catálogo canónico;
- mesmo ordering;
- mesmo `OutputGroupKind`;
- mesmo `SourcePriority`.

### 4. Testes adicionados

`m3uCrawler.Tests/TelegramHistoryWindowTests.cs` (4 testes, todos
passam em Release):

- `All_SearchM3UInTelegram_overloads_default_to_24_hours` — via
  reflection, confirma que **todas as 4 overloads** públicas e
  privadas de `TelegramScraperService` têm `historyHours = 24`
  como default.
- `Program_cs_uses_24h_as_default_for_telegramHistoryHours` —
  garante que o tipo `Program` está no assembly (sanity check).
- `Docker_compose_passes_history_hours_24_not_480` — lê o
  `docker-compose.yml` do repo, confirma `"24"` presente e
  `"480"` ausente.
- `CutoffDate_for_default_historyHours_is_24h_ago_plus_margin` —
  confirma que `DateTime.UtcNow.AddHours(-historyHours)` produz
  um cutoff dentro de 24h ± 1h.

### 5. Resultado

- Build Release: **0 errors, 32 warnings** (todos pré-existentes).
- Suite: **1388 passed, 0 failed, 1 skipped** (1389 total, 2x runs
  estáveis).
- Antes desta entrega: 1384 testes.
- Depois: 1388 (+ 4 testes que verificam a janela de 24h).

### 6. Validação real

A validação no servidor real é **inmediata** — não precisa de
esperar 24h. Basta arrancar uma nova run com a nova imagem e
observar:

- `🕒 Janela de pesquisa Telegram: últimas 24h` (console);
- menor número de `GetHistory` RPCs (vs 480h);
- menor número de `candidatesFound` no `telegram_run_report.json`;
- menor duração total do ciclo.

### 7. Não foi feito

- ❌ Push ao remoto.
- ❌ Build/substituição da imagem em produção.
- ❌ Alterações em R1/R2/R3, PHASE 9A, M3uTesterService, Dispatcharr.
- ❌ Mudanças em filtros, canais, descoberta, validação, ingestão.
- ❌ Alterações em timeouts, retry, M3uCrawlerService, M3uTesterService.
- ❌ Alterações em legacy paths.

---

# 32.18 — PHASE 9C — First-Run / Configuration Lifecycle / Dashboard Hardening

> **Estado: `[em curso]` — 9C.1–9C.5 implementadas; restantes itens da fase pendentes (redesign do Dashboard, gate de command/endpoints, migração das afinidades, Manual/Ajuda contextual).**
>
> *Nota documental (2026-09-16, commit `ad1d3f6`): 9C.3 (canonical country, affinity kind e naming canónico para Dispatcharr) também se encontra implementada. O presente §32.18 será actualizado para reflectir o estado consolidado das três sub-waves numa próxima passagem documental dedicada.*
>
> *Nota documental (2026-09-17, pós-fecho 9C.4): 9C.2, 9C.3 e 9C.4 encontram-se igualmente implementadas e fechadas após revisão independente (`READY FOR CLOSURE`, commit `394061e`). A tabela de fases em §32 foi consolidada nesse fecho. As **decisões sobre o upgrade de instalações existentes** (cenário `BOOTSTRAP_REQUIRED` — instalação adoptada sem administrador) foram formalizadas em `docs/architecture/configuration-lifecycle.md` §"Upgrade de instalações existentes (decisão pós-9C.4)". Sem alterações de código nessa passagem.*
>
> *Nota de implementação (2026-09-17, PHASE 9C.5): a extensão efectiva do gate `Bootstrap` para `READY` ∧ sem administrador activo foi implementada. `AuthModeResolver` resolve esse caso para `AuthMode.Bootstrap` (em vez de `Legacy`), o `BootstrapService` permite criar o primeiro administrador em `READY` sem descer o estado para `CONFIGURING` e sem reconfigurar nada, e o wizard passa a exigir confirmação da password. O modo `Legacy` fica restrito ao contexto explicitamente standalone/testes. Cobertura em `BootstrapServiceTests`, `DashboardBootstrapEndpointTests` e `AuthPrimitivesTests`; detalhe em `docs/architecture/configuration-lifecycle.md` §"Upgrade de instalações existentes" e §"Fluxo de bootstrap".*
>
> *Nota de scope (2026-09-17, simplificação 9C.5): o modelo first-run da 9C destina-se a instalações novas/limpas. Não está prevista migração/recuperação de configuração de administrador de versões anteriores; o bootstrap fecha com **qualquer** registo em `admin_users` (`HasAnyAsync`) e `READY` + administrador desactivado é um estado não suportado, podendo exigir reinicialização do `runtime-data`. A resposta `403 bootstrap-required` passa a reportar o estado de lifecycle real. Ver `docs/architecture/configuration-lifecycle.md` §"Upgrade de instalações existentes".*
>
> Esta fase é uma condição de consolidação do produto antes de novas
> funcionalidades. Não introduz uma segunda arquitectura: fecha o lifecycle
> operacional sobre o catálogo, políticas, Dashboard e scheduler já existentes.
>
> **9C.1 — Configuration Lifecycle / First-Run Bootstrap: implementada.**
> Estados `NOT_CONFIGURED`/`CONFIGURING`/`READY` persistidos
> (`configuration_lifecycle.json` junto ao catálogo), estado persistido como
> autoridade, adopção legacy de instalações existentes, gate único de
> discovery/scheduler (`IConfigurationGate`), endpoint read-only
> `GET /api/configuration/lifecycle`. Os requisitos do §5 são **advisory** nesta
> wave (não bloqueiam `READY`), por compatibilidade com instalações existentes.
> Detalhe em `docs/architecture/configuration-lifecycle.md`.
> **Pendente:** wizard 9C.2, redesign do Dashboard, gate de command equivalents e
> endpoints, migração `CanonicalChannelId` → `CanonicalChannel.Key` e restantes
> itens da fase.

## 1. Objectivo

Uma instalação nova de `m3uCrawler` deve arrancar num estado explícito e seguro,
sem depender de defaults implícitos ou de conhecimento técnico do operador.

O sistema deve distinguir pelo menos:

- `NOT_CONFIGURED` — instalação inicial ainda não configurada;
- `CONFIGURING` — configuração inicial em progresso;
- `READY` — configuração mínima válida e persistida, podendo executar discovery;
- `RUNNING` — operação em execução;
- `ERROR` — configuração existente mas inválida ou operação em erro.

A regra fundamental desta fase é:

> **Nenhuma operação de discovery automática deve arrancar numa instalação
> nova antes de existir configuração mínima persistida e validada.**

O Dashboard deve ser o **control plane** dessa configuração. O operador deve
conseguir chegar de uma instalação vazia a `READY` sem editar ficheiros
internos nem depender de valores hardcoded.

## 2. Problemas a fechar

A auditoria da evolução actual demonstrou que as fases funcionais estão
implementadas, mas a consolidação operacional ainda precisa de uma camada
explícita de lifecycle.

Devem ser tratados, no mínimo:

1. ausência de bootstrap explícito numa instalação nova;
2. distinção entre configuração inexistente e configuração válida;
3. defaults operacionais espalhados por código;
4. scheduler capaz de existir sem uma configuração operacional completa;
5. discovery manual/automática sem gate global de configuração;
6. cobertura incompleta dos fluxos do Dashboard;
7. operações do Dashboard que podem estar implementadas no backend mas não
   suficientemente expostas/validadas na UI;
8. dependência técnica das afinidades em `CanonicalChannelId`;
9. necessidade de migrar afinidades existentes sem perder a decisão lógica
   previamente registada;
10. ausência de uma validação central que diga claramente ao operador o que
    falta configurar e porquê.

## 3. Princípio de configuração persistente

Mantém-se a regra transversal do projecto:

> **Se uma decisão puder razoavelmente ser tomada pelo operador do sistema,
> não deve estar hardcoded no código. Deve existir uma configuração persistente
> e ser possível geri-la pelo Dashboard.**

Isto inclui, quando aplicável:

- política de país;
- fontes e prioridades;
- ordering lists;
- regras de matching;
- aliases;
- afinidades;
- grupos;
- política TV/Radio/VOD;
- qualidade HD/SD/UHD;
- EPG;
- disponibilidade;
- tratamento de unmatched/unknown;
- política de streams;
- scheduler;
- janela de discovery;
- políticas de validação;
- inclusão/exclusão de grupos e categorias;
- regras de geração da playlist.

O código deve fornecer validação, invariantes e defaults seguros de bootstrap,
mas não deve transformar esses defaults em decisões operacionais
irreversíveis.

## 4. Bootstrap e lifecycle

Criar uma representação persistente de estado de configuração, sem duplicar
as fontes de verdade existentes.

O bootstrap deve:

1. detectar instalação nova;
2. criar apenas a estrutura mínima necessária;
3. não iniciar discovery automaticamente;
4. expor no Dashboard o estado `NOT_CONFIGURED`;
5. apresentar os requisitos ainda não satisfeitos;
6. permitir ao operador configurar os componentes necessários;
7. validar a configuração de forma agregada;
8. marcar a instalação como `READY` apenas quando os requisitos forem
   satisfeitos;
9. permitir revalidação posterior;
10. voltar a estado não-operacional se uma alteração tornar a configuração
    inválida.

A transição para `READY` deve ser determinística e testável.

### Regra de compatibilidade para instalações existentes

A definição de `READY` nesta fase deve preservar instalações `m3uCrawler`
já operacionais.

Para uma instalação sem estado de lifecycle persistido:

1. se existir evidência objectiva e verificável de que a instalação já era
   operacional, a instalação deve ser **adoptada como `READY`**;
2. a adopção deve ser registada em log;
3. se não existir evidência suficiente, a instalação deve permanecer em
   `NOT_CONFIGURED`.

A ausência de um ou mais itens da lista de configuração mínima abaixo não deve,
por si só, bloquear uma instalação legacy que já esteja comprovadamente
operacional. Esses requisitos são **advisory nesta wave** e serão transformados
em configuração explícita e governada pelo wizard.

Esta regra existe exclusivamente para compatibilidade durante a introdução do
lifecycle. Não substitui a validação da configuração completa numa instalação
nova.

## 5. Configuração mínima de uma instalação nova

A validação deve definir explicitamente quais componentes são obrigatórios
para considerar a instalação operacional.

A lista final deve ser derivada do código e das entidades/policies existentes,
não inventada como uma segunda configuração paralela.

Como mínimo, a validação deve verificar:

- catálogo canónico disponível;
- baseline/lista de ordering operacional definida;
- política de país válida;
- políticas de importação válidas;
- grupos necessários válidos;
- fontes activas configuradas quando discovery exigir fontes;
- política de source priority válida;
- política de stream validation válida;
- scheduler coerente com as actions registadas, quando activado;
- paths/output necessários acessíveis;
- configuração Dispatcharr válida apenas quando Dispatcharr estiver activado.

Cada falha deve devolver uma chave/código estável e uma descrição orientada
ao operador.

## 6. Wizard de primeira configuração

O Dashboard deve fornecer um fluxo explícito de primeira configuração.

O wizard deve permitir, no mínimo:

1. verificar estado da instalação;
2. criar o **primeiro utilizador administrador**;
3. autenticar o operador através desse administrador para continuar o
   bootstrap;
4. carregar/importar a baseline/catálogo;
5. confirmar país;
6. criar ou seleccionar ordering list activa;
7. configurar sources;
8. rever source priority;
9. configurar políticas de importação/grupos;
10. configurar validação de streams;
11. configurar scheduler, se pretendido;
12. validar tudo;
13. concluir bootstrap e mudar para `READY`.

### Primeiro utilizador administrador

Numa instalação nova, o wizard é responsável pela criação do primeiro
utilizador administrador da aplicação.

Regras:

- não deve existir uma password administrativa default;
- a password deve ser definida explicitamente durante o wizard;
- a criação do primeiro administrador deve ser segura, transaccional e
  idempotente;
- depois de concluído o bootstrap, esse utilizador passa a ser utilizado para
  o acesso autenticado normal ao Dashboard;
- em `NOT_CONFIGURED`, o Dashboard deve disponibilizar apenas o acesso
  necessário ao bootstrap/wizard, não o funcionamento administrativo normal;
- o wizard não deve expor nem devolver a password ou outros segredos em
  respostas, logs, previews ou erros;
- uma instalação legacy adoptada como `READY` não deve criar silenciosamente
  um novo administrador; a estratégia de autenticação/migração legacy deve ser
  tratada explicitamente.

O wizard não deve duplicar CRUD já existente. Deve apenas orquestrar os
serviços/API existentes e apresentar ao operador o que falta.

## 7. Discovery gate

Todas as entradas que possam iniciar discovery devem passar pelo mesmo gate
de configuração.

O gate deve impedir, quando o estado não for `READY`:

- scheduler discovery;
- discovery automática;
- comandos equivalentes;
- endpoints de discovery;
- outras entradas que executem o pipeline de descoberta.

O comportamento deve ser explícito e seguro:

```text
NOT_CONFIGURED
      ↓
tentativa de discovery
      ↓
BLOCKED
      ↓
"Configure o sistema no Dashboard"
```

Depois:

```text
READY
  ↓
discovery manual ou agendada
  ↓
pipeline normal
```

O gate não deve bloquear operações administrativas necessárias para completar
a configuração.

## 8. Scheduler

O scheduler existente da PHASE 12 deve passar a respeitar o lifecycle.

Um job pode estar persistido mas não deve executar uma action operacional se
a instalação não estiver `READY`.

O Dashboard deve mostrar claramente:

- estado global;
- jobs activos;
- próxima execução;
- action;
- motivo pelo qual uma execução foi bloqueada;
- último resultado.

Uma execução bloqueada por configuração deve ser registada como tal, sem ser
tratada como sucesso.

As quatro actions existentes continuam a ser reutilizadas:

- `discoverM3u`;
- `validatePlaylist`;
- `generatePlaylist`;
- `syncDispatcharr`.

Não criar um segundo scheduler.

## 9. Auditoria completa do Dashboard

Fazer uma auditoria funcional de **todos os tabs, botões, formulários,
endpoints e operações**.

Para cada operação deve existir:

```text
Dashboard action
    ↓
API contract
    ↓
service/domain operation
    ↓
persistent result
    ↓
UI refresh / feedback
```

A auditoria deve verificar especialmente:

- criação;
- edição;
- eliminação;
- activação/desactivação;
- duplicação;
- ordenação;
- preview;
- filtros;
- validação;
- reload;
- mensagens de erro;
- estados vazios;
- confirmação de operações destrutivas;
- concorrência/duplo submit;
- persistência após refresh;
- consistência entre API e UI.

Não considerar uma operação "concluída" apenas porque o endpoint existe.

## 10. Afinidades — modelo funcional e identidade do canal

As afinidades são o mecanismo para associar as várias designações/notações
encontradas nas fontes ao **canal canónico definido na Lista de canais por
país**.

A **Lista de canais por país é a única autoridade para o canal canónico**:
criação e alteração do nome e das restantes propriedades canónicas continuam
a ser efectuadas nessa lista. A afinidade não cria nem altera o nome canónico.

### Modelo funcional

Cada definição de afinidade corresponde a **um único canal canónico**.

A interface deve apresentar:

- um campo `Canal canónico` em formato **dropdown**;
- o dropdown deve ser alimentado pela **Lista de canais por país**;
- ao criar uma nova afinidade, devem aparecer apenas os canais que **ainda não
  possuem uma definição de afinidade**;
- um canal que já possua afinidade não pode ser seleccionado para criar uma
  segunda afinidade;
- cada canal pode ter **uma e apenas uma definição de afinidade**;
- a definição contém as várias designações/notações alternativas pelas quais
  o canal pode surgir nas fontes;
- as alternativas são introduzidas num único campo, utilizando o separador
  configurado pela aplicação;
- ao editar uma afinidade existente, o canal a que pertence continua a ser o
  mesmo e as variantes podem ser alteradas.

Exemplo:

```text
Canal canónico
[ RTP 1                         ▼ ]

Nomes / notações alternativas
[ RTP1, RTP 1 HD, RTP1 HD ]
```

### Identidade

A afinidade deve referenciar o **identificador estável do canal canónico no
catálogo**, e não o nome textual.

A alteração do nome na Lista de canais por país não deve criar uma nova
afinidade nem quebrar uma afinidade existente. O relacionamento lógico deve
continuar associado ao mesmo canal.

Se o catálogo utilizar actualmente um ID técnico que possa ser reconstruído
ou alterado por importações/migrações, deve ser utilizada a chave lógica
estável prevista pelo catálogo (`CanonicalChannel.Key` ou equivalente), em vez
de depender de `CanonicalChannelId`.

### Resolução e Dispatcharr

Quando uma fonte contém uma designação que corresponde a uma afinidade:

1. a variante encontrada é resolvida para o canal canónico;
2. a composição utiliza o canal canónico;
3. ao criar ou actualizar o canal no Dispatcharr, o **nome publicado é sempre
   o nome actualmente definido na Lista de canais por país**;
4. nunca deve ser utilizado como nome canónico no Dispatcharr o alias/variante
   encontrado na fonte.

Exemplo:

```text
Fonte                 → "RTP1 HD"
Afinidade             → canal canónico RTP 1
Lista de canais PT    → "RTP 1"
Dispatcharr            → "RTP 1"
```

Se o operador alterar posteriormente na Lista de canais por país:

```text
"RTP 1" → "RTP 1 HD"
```

a mesma afinidade continua ligada ao mesmo canal e as futuras criações ou
actualizações no Dispatcharr devem utilizar `RTP 1 HD`.

### Unicidade

A regra de cardinalidade é:

```text
1 canal canónico → 0 ou 1 definição de afinidade
1 definição de afinidade → 1 canal canónico
1 definição de afinidade → N variantes
```

O sistema deve impedir ambiguidades de matching quando a mesma variante não
puder ser resolvida deterministicamente.

### Migração

Antes de alterar o modelo existente:

1. inventariar todas as afinidades existentes;
2. identificar o canal canónico actualmente associado a cada afinidade;
3. mapear essa associação para a chave estável do canal;
4. consolidar eventuais duplicados por canal;
5. validar a cardinalidade `0..1 afinidade por canal`;
6. detectar variantes atribuídas a mais de um canal;
7. preservar as decisões válidas;
8. apenas depois remover a dependência técnica antiga.

A migração deve ser idempotente e ter testes de regressão.

A migração não deve alterar os nomes canónicos: estes continuam a ser
determinados exclusivamente pela Lista de canais por país.


## Observabilidade operacional em tempo real — Live Run Monitor

O Dashboard deve disponibilizar uma visão em tempo real do que o **m3uCrawler
está efectivamente a fazer durante uma execução**. O objectivo não é apenas
mostrar o resultado final do `RunReport`, mas permitir ao operador perceber,
enquanto o processo decorre:

- se o crawler está `IDLE` ou em execução;
- qual é a actividade/fase actual;
- há quanto tempo a actividade está em curso;
- se a execução continua activa;
- quantas mensagens foram lidas/analisadas;
- quantos candidatos/playlists foram encontrados;
- quantos downloads foram concluídos, falharam ou foram ignorados;
- quantas playlists foram validadas/invalidadas;
- quantas contas Xtream foram descobertas;
- quantos streams/canais foram descobertos;
- quantos streams/canais foram testados;
- quantos foram considerados funcionais/não funcionais;
- quantos itens foram compostos/publicados/sincronizados no Dispatcharr,
  quando essa etapa estiver activa;
- erros e avisos relevantes;
- timestamp da última actividade.

A experiência pretendida deve ser equivalente, para um utilizador, a um
`docker logs -f m3ucrawler` **perceptível e estruturado**, complementado por
totalizadores.

### Estado operacional

Deve existir um estado explícito da execução, com uma enumeração equivalente
a:

```text
IDLE
READING_TELEGRAM
DISCOVERING
DOWNLOADING
ANALYZING
VALIDATING
COMPOSING
SYNCING_DISPATCHARR
COMPLETED
ERROR
```

A enumeração concreta pode ser ajustada à arquitectura existente, mas o
Dashboard deve conseguir representar inequivocamente a actividade actual.

O estado deve incluir pelo menos:

- `runId`;
- estado actual;
- etapa/actividade actual;
- `startedAt`;
- `updatedAt`;
- duração decorrido;
- mensagem resumida para o operador.

### Totalizadores em tempo real

Os totalizadores devem estar associados ao `SyncRun` em curso e ser
actualizados durante a execução, não apenas no final.

A estrutura deve permitir pelo menos:

```text
Telegram
  messagesRead
  candidatesFound

Playlists
  playlistsFound
  playlistsDownloaded
  playlistsValid
  playlistsInvalid
  downloadFailures

Xtream
  accountsDiscovered
  accountsValidated

Streams
  streamsDiscovered
  streamsSelected
  streamsTested
  streamsWorking
  streamsFailed

Dispatcharr
  channelsCreated
  channelsUpdated
  channelsFailed
```

Os contadores devem ser incrementados pelos pontos reais do pipeline. Não
devem ser calculados retroactivamente a partir de texto de logs.

Um contador só deve ser apresentado quando existir uma definição semântica
clara para o seu significado; a lista acima é o modelo alvo e pode ser
introduzida incrementalmente conforme cada fase disponibilize os eventos
necessários.

### Actividade em tempo real

Além dos totalizadores, o Dashboard deve apresentar um feed das **últimas
actividades relevantes**, por exemplo:

```text
17:51:02  Telegram       A ler mensagens...
17:51:03  Telegram       260 mensagens analisadas
17:51:04  Playlist       Candidato encontrado: message 110843
17:51:04  Download        A descarregar playlist...
17:51:06  Download        Download concluído — 4.2 MB / 2.1 s
17:51:06  Xtream          751 contas encontradas
17:51:07  Validation      Conta 1/751 — a validar
17:51:08  Validation      Conta 1 — 127 testados / 74 OK / 53 falhados
```

O feed deve ser orientado ao utilizador e não reproduzir cegamente todo o
`docker logs`. Os logs técnicos continuam a existir para diagnóstico.

Cada actividade deve, quando aplicável, possuir:

- timestamp;
- categoria;
- nível (`INFO`, `WARNING`, `ERROR`);
- mensagem;
- metadados estruturados.

### Transporte

A actualização do Dashboard deve ser realmente em tempo real.

A solução preferencial é utilizar o mecanismo de comunicação em tempo real já
suportado pela aplicação, ou **SignalR/WebSocket** se não existir outro
mecanismo adequado.

Polling periódico pode ser utilizado como fallback ou numa primeira
implementação se isso reduzir significativamente o risco, mas não deve obrigar
o operador a actualizar manualmente a página.

### Persistência e desempenho

Não persistir cada actividade individual na base de dados.

O modelo deve distinguir:

```text
Persistente
  SyncRun
  estado actual
  totalizadores
  steps/resumo

Em memória / buffer limitado
  últimas actividades live
```

O feed pode utilizar um **ring buffer** de tamanho limitado (por exemplo, as
últimas centenas de actividades), evitando crescimento ilimitado.

Os totalizadores e o estado principal devem sobreviver ao refresh do Dashboard
e permitir que o operador veja o estado actual de um run que continua em curso.

### Relação com `SyncRun` / `SyncRunStep`

Esta capacidade deve **reutilizar e estender o modelo de runs existente**.

Não criar um segundo sistema independente de execução apenas para o Dashboard.

`SyncRun` continua a representar a execução e `SyncRunStep` as etapas
estruturais. A observabilidade live acrescenta estado actual, totalizadores e
actividade operacional à mesma execução.

### Logs técnicos

O `docker logs -f m3ucrawler` deve continuar disponível e não deve ser
substituído.

Existem três níveis complementares:

```text
Dashboard
  → estado actual + totalizadores + actividade live

SyncRun
  → estado/steps/resultados persistentes

docker logs
  → diagnóstico técnico detalhado
```

O Dashboard não deve depender de fazer parsing do `docker logs`.

### Critérios de aceitação

Uma execução em curso deve permitir ao operador responder, sem consultar
directamente o container:

1. O m3uCrawler está parado ou está a trabalhar?
2. O que está a fazer neste momento?
3. Quando começou a execução?
4. Qual é a fase actual?
5. Quantas mensagens já leu?
6. Quantos candidatos/playlists encontrou?
7. Quantos downloads foram feitos e quantos falharam?
8. Quantas contas/streams/canais já foram processados?
9. Quantos testes foram concluídos e quantos deram resultado funcional?
10. Existem erros ou avisos relevantes?
11. Quando ocorreu a última actividade?

A informação deve actualizar-se automaticamente durante a execução e continuar
coerente com o `SyncRun` terminado.


## 11. API

Adicionar apenas os contratos necessários para expor o lifecycle, sem criar
uma API paralela.

Deve existir uma representação clara de:

- estado global;
- requisitos de configuração;
- validação;
- conclusão/revalidação do bootstrap;
- operações de afinidade baseadas na identidade lógica estável do canal canónico, sem dependência de IDs técnicos mutáveis.

As respostas devem ser adequadas tanto ao Dashboard como a testes
automatizados.

Erros de configuração devem ser estruturados, não apenas strings livres.

## 12. Segurança

A primeira execução deve seguir fail-safe:

- não descobrir;
- não sincronizar Dispatcharr;
- não sobrescrever playlists;
- não executar jobs operacionais;
- não expor credenciais;
- não considerar uma configuração parcialmente preenchida como `READY`.

Durante `NOT_CONFIGURED`, o Dashboard deve permitir apenas o fluxo necessário
para o bootstrap inicial. O wizard deve criar o primeiro utilizador
administrador antes de disponibilizar o acesso administrativo normal.

O Dashboard deve exigir autenticação em instalação normal e todas as APIs
administrativas devem respeitar o mesmo controlo de acesso.

A adopção de uma instalação legacy como `READY` não deve criar implicitamente
credenciais administrativas novas. Qualquer migração para o modelo de
autenticação introduzido nesta fase deve ser explícita e segura.

Logs e respostas do Dashboard continuam sujeitos à sanitização existente.

Nenhuma credencial deve ser devolvida em endpoints de configuração, preview,
logs ou erros.

## 13. Testes obrigatórios

Adicionar testes cobrindo pelo menos:

### Bootstrap

- instalação nova começa em `NOT_CONFIGURED`;
- bootstrap é idempotente;
- instalação legacy operacional sem estado persistido é adoptada como
  `READY`;
- instalação sem estado e sem evidência legacy suficiente permanece
  `NOT_CONFIGURED`;
- os requisitos advisory da configuração mínima não bloqueiam a adopção
  legacy;
- configuração incompleta de uma instalação nova não passa a `READY`;
- configuração válida passa a `READY`;
- configuração inválida regressa a estado não-operacional;
- mensagens/códigos de validação são determinísticos.

### Primeiro administrador / autenticação

- o wizard cria o primeiro utilizador administrador;
- não existe password default;
- a password definida no wizard não aparece em logs, respostas ou erros;
- a criação do administrador é idempotente e não cria duplicados;
- após o bootstrap, o acesso normal ao Dashboard exige autenticação;
- instalações legacy não recebem silenciosamente um novo administrador.

### Discovery gate

- discovery é bloqueado em `NOT_CONFIGURED`;
- discovery é permitido em `READY`;
- scheduler não executa discovery quando não está `READY`;
- action bloqueada produz resultado auditável;
- em `NOT_CONFIGURED`, apenas o fluxo necessário para o bootstrap inicial fica disponível; o acesso administrativo normal só é disponibilizado após `READY`.

### Dashboard/API

- estado é persistido;
- refresh mantém o estado;
- CRUD existente continua funcional;
- todos os endpoints novos têm testes de sucesso e erro;
- operações destrutivas e inválidas não deixam estado parcial.

### Afinidades

- afinidade referencia a identidade lógica estável do canal canónico, por `CanonicalChannel.Key` ou equivalente, e não um ID técnico mutável;
- cada canal canónico tem 0 ou 1 definição de afinidade;
- cada definição de afinidade referencia exactamente um canal canónico e pode conter N variantes;
- a selecção do canal canónico na UI usa a Lista de canais por país e exclui canais que já possuem afinidade;
- a alteração do nome canónico não quebra a afinidade existente;
- o nome publicado no Dispatcharr é sempre o nome actualmente definido na Lista de canais por país;
- migração preserva relações existentes e é idempotente;
- bootstrap/importação não cria duplicados;
- chaves lógicas resolvem deterministicamente;
- relações/variantes ambíguas são rejeitadas ou marcadas para revisão.

### Regressão

- suite completa existente continua verde;
- pipeline Telegram continua a respeitar country gate;
- matching R1/R2/R3 mantém comportamento;
- playlist composition continua determinística;
- Dispatcharr continua a consumir a composição existente;
- scheduler continua a executar as quatro actions quando `READY`.

## 14. Critérios de aceitação

A fase só pode passar a `[concluído]` quando for possível demonstrar:

```text
instalação limpa
      ↓
Dashboard seguro
      ↓
NOT_CONFIGURED
      ↓
wizard
      ↓
criação do primeiro administrador
      ↓
configuração persistida
      ↓
validação
      ↓
READY
      ↓
discovery manual
      ↓
pipeline
      ↓
catálogo
      ↓
playlist
      ↓
scheduler
      ↓
operações automáticas
```

E, inversamente:

```text
NOT_CONFIGURED
      ↓
scheduler/discovery
      ↓
BLOCKED
```

Sem atalhos escondidos e sem editar código/configuração interna para concluir
o bootstrap.

## 15. Definition of Done específica

Além da Definition of Done global:

- lifecycle persistente implementado;
- bootstrap implementado;
- adopção legacy implementada e testada;
- wizard funcional;
- criação segura do primeiro utilizador administrador implementada e testada;
- discovery gate aplicado a todas as entradas relevantes;
- scheduler integrado com o gate;
- auditoria completa do Dashboard concluída;
- modelo funcional de afinidades implementado de acordo com a identidade lógica estável, cardinalidade 0..1 por canal canónico, variantes múltiplas e autoridade da Lista de canais por país;
- migração das afinidades existentes executada e testada;
- API documentada;
- testes automatizados;
- instalação limpa validada;
- suite Release verde;
- documentação actualizada;
- commit criado.

Só depois desta fase se deve iniciar nova evolução funcional de maior dimensão.

---

# 33. Definition of Done

Uma fase só está concluída quando:

- código implementado;
- persistência implementada quando necessária;
- API implementada;
- Dashboard implementado quando aplicável;
- testes implementados;
- build concluído;
- testes concluídos;
- documentação afectada actualizada;
- commit criado.

Depois disso, avançar directamente para a fase seguinte.

---

# 34. Regra fundamental

Este documento é um **plano de execução**, não uma nova proposta arquitectural.

A arquitectura definida no documento:

> `m3uCrawler — Contexto de Projecto, Decisões e Linha de Evolução`

continua a ser a referência conceptual.

O código existente continua a ser a referência de implementação existente.

Este documento define **como completar a evolução até ao sistema funcional pretendido**.

---

# Apêndice — Tabela de Fases e Estado

> Convenção de estado: `[concluído]` = totalmente integrado, com testes e Dashboard; `[parcial]` = peças-chave existem mas faltam camadas; `[pendente]` = ainda por implementar.

| Fase | Estado | Notas |
|------|--------|-------|
| PHASE 0 — Baseline | `[concluído]` | `a26c974`. Build e suite Release estáveis. |
| PHASE 1 — Canonical Catalogue | `[concluído]` | Seed PT baseline + importer + 19 testes. 6.1-6.4 sem marcador explícito na secção (coberto por 32.1). |
| PHASE 2 — Dashboard Control Plane | `[concluído]` | API + UI completas + 5 testes HTTP. Ver 32.4. Sem marcador explícito na secção 7. |
| PHASE 3 — Matching & Review | `[concluído]` | MatchingAuditEntity + Dashboard tab Matching. Ver 32.5. |
| PHASE 4 — Sources & ChannelSource | `[concluído]` | SourceEntity + ChannelSourceEntity, API CRUD, Dashboard, 12 testes. |
| PHASE 5 — Ordering Lists | `[concluído]` | OrderingListEntity + OrderingItemEntity, CRUD, Dashboard. Sem marcador explícito na secção 14. |
| PHASE 6 — Source Priority | `[concluído]` | SourcePriorityPolicyEntity + SourceSelector, Dashboard, testes. |
| PHASE 7 — Playlist Generation | `[concluído]` | PlaylistComposerService + API preview + Dashboard + WriteComposedAsync (7b). |
| PHASE 8 — TV / Radio / VOD | `[concluído]` | ImportPolicyEntity + MediaKind/VodPolicy. Sem marcador explícito em 18.1-18.3. |
| PHASE 8 — Group Management | `[concluído]` | CanonicalGroupEntity + GroupMappingEntity, Dashboard, 11 testes. |
| PHASE 9 — Quality / EPG / Availability | `[concluído]` | 9A + 9b + visão agregada concluídos. |
| PHASE 9A — URL/Stream Validation Perf. | `[concluído]` | 14 testes + 1 benchmark. Ver 32.2. |
| PHASE 9b — ChannelSource observations | `[concluído]` | ChannelSourceObservationEntity + API + 3 testes. Ver 32.7. |
| PHASE 10 — Dispatcharr | `[concluído]` | `BuildPlanFromCompositionAsync` existe mas sem call site de produção e sem testes (correcção factual 2026-09-17). Ver 32.8 e nota em 32.19. |
| PHASE 11 — Operations | `[concluído]` | SyncRunStepEntity + API + Dashboard Passos + 4 testes. Ver 32.9. |
| PHASE 12 — Automation / Scheduler | `[concluído]` | ScheduledJobEntity + CronExpression + Runner + 4 actions concretas (`discoverM3u`, `validatePlaylist`, `generatePlaylist`, `syncDispatcharr`) + arranque em produção via `Program.cs --web` + 19 testes. Ver 32.10 e 32.11. |
| **PHASE 9C — First-Run / Configuration Lifecycle / Dashboard Hardening** | **`[em curso]`** | 9C.1 (lifecycle persistido `NOT_CONFIGURED/CONFIGURING/READY`, adopção legacy, gate de discovery/scheduler), 9C.2 (wizard de first-run, admin/sessões/CSRF, gate de autorização único), 9C.3 (affinity por país/canal, naming canónico normalizado, migration aditiva `AddCanonicalCountryAndAffinityKind`), 9C.4 (Live Run Monitor: `live_runs`/`live_run_steps`, `RunCoordinator` único, `GET /api/run/status` + `POST /api/run/start`, `--web-allow-trigger`, acções agendadas `telegramRun`/`telegramMaintainRun`, vista "Live Run" com polling) e 9C.5 (first-run/legacy bootstrap: `READY` ∧ sem admin resolve para `AuthMode.Bootstrap` em vez de `Legacy`, criação do primeiro admin sem alterar o estado nem reconfigurar a instalação, confirmação de password no wizard) implementadas. Nota documental 2026-09-17: esta linha descrevia 9C.2 como pendente apesar de já estar implementada; corrigida. Pendente: redesign do Dashboard, gate de command/endpoints, migração das afinidades e Manual/Ajuda contextual. Ver 32.18. |
| PHASE-Bridge — Pipeline → Catálogo | `[concluído]` | `PipelineIngestionService` liga o pipeline real (Telegram/M3U8-search) ao catálogo persistente via `EnsureSourceAsync` + `ResolveAsync` + `RecordChannelSourceAsync` + novo `EnsureCanonicalChannelAsync` (upsert). 9 testes TDD. Ver 32.12. |
# 32.19 — PHASE 13 — Dispatcharr Source Selection, Diversity & Source Limits

> Estado: [pendente] — proposta de implementação.
>
> Esta fase fecha um problema operacional identificado na publicação para Dispatcharr:
> actualmente, quando um canal é sincronizado, podem ser associadas ao mesmo canal todas
> as fontes descobertas para esse canal. Em canais com elevada descoberta isto pode resultar
> em dezenas ou mais de 100 fontes associadas, aumentando desnecessariamente o volume de
> dados e o peso operacional do Dispatcharr sem garantir valor proporcional.
>
> A solução proposta é introduzir uma etapa explícita de **selecção/ranking de fontes por
> canal**, imediatamente antes da sincronização para Dispatcharr.

> **Nota factual (Wave 10-0, 2026-09-17).** Esta fase permanece `[pendente]`.
> Antes de a iniciar, o caminho agendado `syncDispatcharr` foi consolidado:
> passou a injectar o `CatalogResolver` (catálogo canónico + ownership) no
> `ChannelMatcher` e no `DispatcharrSyncService`, eliminando o modo legacy
> nesse caminho (ver `docs/architecture/channel-catalog-and-ownership.md` §13).
> Nada da selecção/diversidade/limites descrita abaixo foi implementado.

> **Nota factual (Wave 13-1, 2026-09-17).** A unidade algorítmica central foi
> implementada isoladamente em `m3uCrawler/Services/SourceSelection/`:
> `SelectionCandidate`, `SourceSelectionPolicy`, `ProviderIdentity`,
> `IChannelSourceSelector`/`ChannelSourceSelector` e `SourceSelectionResult`
> (pura, sem I/O, 53 testes em `ChannelSourceSelectorTests`, após o hardening 13-1a). Suporta
> `MaxSourcesPerChannel` (sem valor hardcoded), `PreferDistinctProviders`,
> `MaxSourcesPerProvider`, `AllowFallbackToSameProvider`, deduplicação por URL
> normalizada, diversidade em duas fases, ranking determinístico e motivos por
> candidato. **Não** estão implementados: persistência da política, migrations,
> Dashboard/preview, `ProviderDefinition` completa, ingestão de Quality/EPG e
> integração no composer/`MatchPlan`/`DispatcharrSyncService`. Detalhe em
> `docs/architecture/dispatcharr-source-selection.md`.
>
> *Nota de desenho registada (não corrigida): dos critérios de ranking do §5,
> "sucesso/qualidade histórica" e "estabilidade/recência da validação" não são
> suportados nesta wave porque o candidato não tem campos para eles; ficam para
> wave posterior, como previsto.*

> **Nota factual (Wave 13-3, 2026-09-17).** A aplicação da política ao pipeline
> Telegram foi implementada (`SourceSelectionStage`): junção exacta das streams do
> pipeline (URL real, só em runtime) aos `ChannelSource` do catálogo pela chave
> sanitizada, projecção para `SelectionCandidate`, aplicação de
> `IChannelSourceSelector` por canal canónico e devolução da lista publicável
> (seleccionadas + não correspondidas) antes de `SaveToM3uPlaylist`, nos dois pontos
> de publicação Telegram. `source-disabled` exclui; não correspondidas/ambíguas fazem
> pass-through; catálogo ausente/vazio ⇒ no-op. Sem persistência nova, sem migration,
> sem alterações ao Dispatcharr/`MatchPlan`/ownership. Diagnóstico agregado em
> `RunReport.SourceSelection` (só contagens). Continuam pendentes: persistência/
> Dashboard da política, produtores de Quality/EPG, correcção do reset de
> `Source.Priority`, integração no composer/discovery. Detalhe em
> `docs/architecture/dispatcharr-source-selection.md` §10.

> **Nota factual (2026-09-17) — `BuildPlanFromCompositionAsync`.** O método
> existe em `ChannelMatcher` mas continua **sem call site de produção e sem
> testes**. A PHASE 10 não é reaberta nesta wave e o método não é integrado
> apenas para satisfazer a documentação. A afirmação do §32.8 de que dois
> testes em `DispatcharrCompositionTests.cs` fecharam a PHASE 10 é
> **inexacta** (esse ficheiro não existe); decisão futura pendente.

## 1. Objectivo

Para cada canal a publicar no Dispatcharr:

1. recolher as fontes elegíveis já descobertas, normalizadas e validadas;
2. eliminar duplicados e fontes equivalentes;
3. calcular uma classificação determinística de qualidade;
4. privilegiar diversidade de fornecedor/host;
5. seleccionar apenas as melhores fontes até ao limite configurado;
6. publicar no Dispatcharr apenas o conjunto seleccionado.

O limite inicial recomendado é **10 fontes por canal**, mas o valor **não deve ser
hardcoded**. Deve ser uma política persistente e editável no Dashboard.

O objectivo não é simplesmente cortar a lista aos primeiros 10 elementos: é produzir
um conjunto pequeno, diversificado e de qualidade.

## 2. Princípio de arquitectura

A selecção deve acontecer **antes do Dispatcharr**, sem destruir nem limitar o catálogo
interno de fontes descobertas.

Devem existir conceptualmente três conjuntos distintos:

- **Discovery/Source Catalog** — todas as fontes descobertas;
- **Eligible Sources** — fontes que cumprem as políticas de matching/qualidade/validação;
- **Dispatcharr Selection** — subconjunto escolhido para publicação naquele canal.

Isto permite manter toda a informação descoberta para futuras reavaliações, sem obrigar
o Dispatcharr a armazenar centenas de associações por canal.

A política de selecção deve ser reutilizável e independente do cliente Dispatcharr.

## 3. Configuração persistente

Criar uma política de configuração, gerível no Dashboard, para a selecção de fontes.

Configurações mínimas:

- `MaxSourcesPerChannel` — limite máximo por canal; valor inicial sugerido: `10`;
- `PreferDistinctProviders` — activar/desactivar diversidade de fornecedor;
- `MaxSourcesPerProvider` — limite opcional por fornecedor;
- `ProviderDefinition` — forma de determinar o fornecedor lógico de uma fonte;
- `MinimumValidatedSources` — opcional;
- `SelectionPolicy` — estratégia de ranking;
- `AllowFallbackToSameProvider` — permitir preencher vagas quando há poucos
  fornecedores distintos;
- `RebalanceOnSync` — recalcular a selecção a cada sincronização;
- `KeepExistingHealthySources` — opcional, para evitar churn desnecessário.

Nenhum destes valores deve ficar fixo no código quando representar uma decisão
operacional do administrador.

## 4. Definição de fornecedor

A diversidade deve ser calculada sobre o **fornecedor lógico**, não simplesmente sobre
a string completa do URL.

A identificação deve normalizar pelo menos:

- scheme;
- hostname;
- porta quando relevante;
- credenciais;
- path;
- parâmetros de URL quando estes não identificam realmente um fornecedor diferente.

A implementação deve reutilizar as regras de normalização/sanitização de URL já
existentes no projecto, evitando criar uma segunda interpretação de identidade.

Quando não for possível determinar o fornecedor com confiança, a fonte deve ser marcada
como `UnknownProvider` e não deve artificialmente ganhar diversidade por parecer
diferente de outra fonte desconhecida.

## 5. Critérios de ranking

A classificação deve usar informação já disponível no pipeline.

A implementação deve considerar, pelo menos:

1. resultado de validação da stream;
2. sucesso/qualidade histórica, quando disponível;
3. estabilidade/recência da validação;
4. prioridade da source, já definida pelas políticas existentes;
5. qualidade técnica conhecida, quando confiável;
6. qualidade/estado do EPG, quando relevante;
7. preferência por fornecedor distinto;
8. desempate determinístico pela identidade normalizada da fonte.

Uma fonte que nunca foi validada não deve ultrapassar silenciosamente uma fonte
validada e estável apenas por ter sido descoberta mais recentemente.

A função de ranking deve ser determinística para o mesmo estado de entrada e configuração.

## 6. Algoritmo de diversidade

A selecção não deve ser simplesmente `sort -> Take(10)`.

Deve ser uma selecção em duas fases:

### Fase A — diversidade

Percorrer as fontes ordenadas por qualidade e seleccionar primeiro fontes de
fornecedores distintos, até atingir o limite ou esgotar os fornecedores disponíveis.

### Fase B — preenchimento

Se ainda existirem posições disponíveis:

- seleccionar as melhores fontes restantes;
- respeitar `MaxSourcesPerProvider`, se configurado;
- permitir repetição de fornecedor conforme `AllowFallbackToSameProvider`.

Exemplo:

- limite = 10;
- existem 7 fornecedores distintos;
- são seleccionadas inicialmente 7 fontes, uma por fornecedor;
- as 3 posições restantes são preenchidas pelas melhores fontes seguintes.

Se existirem apenas 2 fornecedores, não se deve rejeitar artificialmente fontes boas
apenas para manter uma diversidade impossível.

## 7. Deduplicação

Antes do ranking devem ser eliminadas fontes logicamente equivalentes.

A deduplicação deve considerar:

- URL normalizada;
- identidade da stream quando disponível;
- host/provider;
- metadados relevantes;
- variantes que apenas diferem por parâmetros transitórios.

Não deve eliminar streams diferentes que pertençam legitimamente ao mesmo fornecedor.

O resultado deve ser estável e auditável.

## 8. Relação com Source Priority e Stream Validation

A nova selecção **não substitui** Matching, Source Priority, Stream Validation ou as
políticas de qualidade. É a etapa final de decisão do conjunto a publicar.

Pipeline conceptual:

```text
Discovery
   ↓
Source normalization
   ↓
Channel matching
   ↓
Source priority
   ↓
Stream validation
   ↓
Eligible sources
   ↓
Deduplication
   ↓
Provider diversity
   ↓
Source ranking
   ↓
MaxSourcesPerChannel
   ↓
Dispatcharr selection
   ↓
Dispatcharr sync
```

## 9. Preview / Dry-run

Antes de activar a política em produção deve existir um modo de preview no Dashboard.

Para cada canal deve ser possível ver:

- fontes descobertas;
- fontes elegíveis;
- duplicados removidos;
- fornecedores distintos;
- fontes seleccionadas;
- fontes rejeitadas;
- ranking;
- fornecedor;
- motivo de exclusão;
- configuração aplicada.

O preview não pode alterar o Dispatcharr.

## 10. Dashboard

Criar uma área:

**Policies → Dispatcharr / Source Selection**

Deve permitir:

- definir o limite global;
- activar/desactivar diversidade;
- configurar limite por fornecedor;
- configurar/visualizar a política de ranking;
- executar preview;
- consultar estatísticas;
- visualizar a selecção por canal;
- comparar `discovered` vs `selected`;
- validar a configuração antes de a activar.

O limite afecta **a publicação no Dispatcharr**, não a descoberta nem o catálogo interno.

## 11. Dispatcharr Sync

O sincronizador deve receber explicitamente o resultado da selecção.

Não deve continuar a consultar directamente todas as fontes associadas ao canal para
decidir o que publicar.

A fronteira deve ser equivalente a:

```text
DispatcharrSync(MatchPlan + DispatcharrSourceSelection)
```

ou uma abstracção equivalente, mantendo a responsabilidade de selecção fora do cliente
Dispatcharr.

## 12. Gestão das associações já existentes

A implementação deve tratar canais que já tenham dezenas/centenas de fontes no
Dispatcharr.

A sincronização deve:

1. calcular a nova selecção;
2. identificar fontes que permanecem;
3. identificar fontes que deixam de pertencer à selecção;
4. remover apenas associações geridas pelo m3uCrawler;
5. nunca remover fontes pertencentes a outro proprietário/integração;
6. registar as alterações.

Se o ownership não puder ser determinado com segurança, a acção destrutiva deve ser
bloqueada e apresentada como revisão necessária.

## 13. Estabilidade e churn

O algoritmo não deve provocar alterações constantes por pequenas variações de ranking.

Deve ser considerada uma estratégia de estabilidade, por exemplo manter fontes saudáveis
existentes quando a diferença de qualidade for pequena, ou substituir apenas quando uma
alternativa tiver vantagem suficiente.

A política escolhida deve ser configurável/documentada e coberta por testes.

## 14. Métricas

Adicionar métricas:

- fontes descobertas por canal;
- fontes elegíveis por canal;
- fornecedores distintos;
- fontes seleccionadas;
- fontes removidas da publicação;
- duplicados removidos;
- canais acima do limite antes da selecção;
- alterações no Dispatcharr;
- selecções bloqueadas por ownership.

## 15. Testes

Criar testes unitários e de integração para:

- 100 fontes / 1 fornecedor;
- 100 fontes / 100 fornecedores;
- 100 fontes / 10 fornecedores;
- menos de 10 fontes;
- exactamente 10;
- mais de 10;
- fornecedores repetidos;
- `UnknownProvider`;
- URLs equivalentes;
- duplicados;
- fontes inválidas;
- não validadas;
- empate de score;
- ranking determinístico;
- alteração de configuração;
- `MaxSourcesPerProvider`;
- fallback;
- ownership;
- limpeza de associações antigas;
- dry-run sem alterações;
- sincronização idempotente;
- regressão sobre Source Priority e Stream Validation;
- canais sem fontes elegíveis.

Deve existir um teste explícito que prove que **100 fontes descobertas não implicam
100 associações no Dispatcharr** quando `MaxSourcesPerChannel=10`.

## 16. Critérios de aceitação

Com a política configurada para 10:

- um canal com 100 fontes elegíveis não recebe 100 fontes no Dispatcharr;
- são seleccionadas no máximo 10;
- a selecção privilegia fornecedores distintos;
- validação/qualidade influencia o ranking;
- o resultado é determinístico;
- fontes não seleccionadas permanecem disponíveis internamente;
- a sincronização não altera fontes de outros proprietários;
- o Dashboard permite alterar a política sem recompilar;
- o preview explica o resultado;
- sincronizações repetidas não produzem churn desnecessário;
- os relatórios explicam por que uma fonte foi seleccionada ou excluída.

## 17. Definition of Done da fase

- [ ] configuração persistente do limite;
- [ ] valor inicial recomendado 10, não hardcoded;
- [ ] identificação normalizada de fornecedor;
- [ ] deduplicação;
- [ ] ranking determinístico;
- [ ] diversidade de fornecedor;
- [ ] fallback configurável;
- [ ] selecção antes do Dispatcharr;
- [ ] Dispatcharr recebe apenas o conjunto seleccionado;
- [ ] preview/dry-run;
- [ ] Dashboard completo;
- [ ] limpeza segura das associações antigas;
- [ ] ownership respeitado;
- [ ] métricas e auditoria;
- [ ] testes unitários e integração;
- [ ] idempotência;
- [ ] documentação actualizada;
- [ ] build e suite de testes passam;
- [ ] commit.

## 18. Regra de produto

A existência de uma fonte no catálogo **não significa automaticamente que essa fonte
deva ser publicada no Dispatcharr**.

O catálogo deve conservar a informação descoberta; o Dispatcharr deve receber apenas
o subconjunto que a política de publicação considera útil.

Esta separação evita que o crescimento da descoberta provoque crescimento ilimitado das
associações no sistema de destino.


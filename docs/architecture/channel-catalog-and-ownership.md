# Channel Catalog & Ownership

## 1. Resumo

O `ChannelCategoryLookup` (em memória, dicionário estático) já não
decide se uma identidade pode ser criada como canal novo. O critério
de criação é agora lido da **base de dados SQLite persistente**
(`/data/channel-catalog.db` em produção, equivalente a
`channel-catalog.db` em desenvolvimento). O `ChannelCategoryLookup`
permanece apenas como **compatibilidade de categoria editorial**
— decide o `EditorialCategory` exibido pelo matcher; não decide
`NewChannel`.

### Restrições arquitecturais

- A BD SQLite vive em `runtime-data` (mesmo directório de
  `wtelegram.config` / `session.dat`). Em produção, o caminho é
  `/data/channel-catalog.db` (bind-mount no container).
- A BD não substitui o Dispatcharr. É um catálogo editorial local.
  Aplica Dispatcharr apenas via `DispatcharrSyncService` que já
  existe.
- Sem nova BD externa (Redis, Postgres). Sem novo serviço.
  Sem EF Core para qualquer subsistema fora deste catálogo.
- As migrations são geradas com `dotnet ef migrations add` e
  aplicadas idempotentemente em cada arranque do sync.
- O seed é versionado num único ficheiro
  (`m3uCrawler/Services/Catalog/CatalogSeed.cs`) — sem segredos
  escondidos em SQL inline.
- Nenhuma URL de stream é persistida na BD. Apenas identidade,
  alias, categoria editorial, política de publicação, ownership,
  fingerprint de revisão e contadores de SyncRun.

## 2. Modelo de dados

### `CanonicalChannel`

```
Id              long  PK
Key             text  UK (ex.: "benfica-tv")
DisplayName     text
EditorialCategory    int (Live/Entretenimento/Desporto/Infantil/Documentarios)
EditorialGroup  int (PortugalLive/PortugalFilmes24_7/...)
PublicationPolicy    int (CreateEligible/MergeOnly/ReviewOnly/Excluded)
IsEnabled       bool
CreatedAtUtc    datetime
UpdatedAtUtc    datetime
```

`Key` é o identificador estável; `DisplayName` é o nome editorial;
`EditorialCategory` é a categoria; `EditorialGroup` é o grupo final
de publicação; `PublicationPolicy` é a autorização; `IsEnabled`
desactiva temporariamente sem apagar.

### `ChannelAlias`

```
Id                   long  PK
NormalizedAlias     text  UK
CanonicalChannelId   long  FK → CanonicalChannel
CreatedAtUtc         datetime
```

A normalização deve ser a mesma do matcher (`ChannelNormalizer`).
Lookup por igualdade ordinal exata.

### `IdentityRule`

```
Id                   long  PK
NormalizedIdentity   text  UK
Disposition         int (ReviewOnly / Excluded)
Reason              text
CreatedAtUtc         datetime
UpdatedAtUtc         datetime
```

### `DispatcharrChannelOwnership`

```
Id                       long  PK
DispatcharrChannelId     long  UK
Ownership                int (Unknown / CrawlerManaged / External)
CanonicalChannelId       long  FK? (set null on delete)
FirstObservedAtUtc       datetime
LastObservedAtUtc        datetime
Evidence                 text (sanitised: short label, no URLs/credentials)
CreatedAtUtc             datetime
UpdatedAtUtc             datetime
```

Ownership nunca é promovido automaticamente. Não se infere a
partir de nome, número, posição ou ordem.

### `DispatcharrStreamOwnership`

```
Id                       long  PK
DispatcharrStreamId      long  UK
DispatcharrChannelId     long
Ownership                int (Unknown / CrawlerManaged / External)
CreatedBySyncRunId       long?
CreatedAtUtc             datetime
UpdatedAtUtc             datetime
```

### `ReviewItem`

```
Id                       long  PK
Fingerprint              text  UK (SHA-256 hex, 64 chars)
NormalizedIdentity       text
SourceGroup              text
ReasonSignature          text (ex.: "not-approved-in-publication-catalog")
State                    int (Open / Approved / Excluded)
ApprovedCanonicalChannelId  long  FK?
Note                     text  (sanitised)
CreatedAtUtc             datetime
UpdatedAtUtc             datetime
ResolvedAtUtc            datetime?
```

`Fingerprint` é `SHA-256("{normalizedIdentity}|{sourceGroup}|{reasonSignature}")`.
Idempotente: um segundo upsert com o mesmo fingerprint actualiza
o existente em vez de duplicar.

### `SyncRun`

```
Id                               long  PK
StartedAtUtc                     datetime
FinishedAtUtc                    datetime
AppVersion                       text (commit SHA curto)
CountCreatedCrawlerManaged        int
CountMergedIntoExternal          int
CountProtectedExternalStreams     int
CountRemovedCrawlerManagedStreams int
CountReviewRequired              int
CountExcluded                     int
Result                           text ("ok" / "cancelled" / "error: ...")
```

Sem URLs, credenciais ou tokens. Apenas contadores.

## 3. Seed versionado

`CatalogSeed.cs` mantém uma lista única e testada:

```csharp
public static readonly IReadOnlyList<CanonicalChannelSeed> Channels = ...
public static readonly IReadOnlyList<IdentityRuleSeed> IdentityRules = ...
```

A função `ValidateSeedConsistency()` confirma que não há sobreposições
de alias entre canais (um alias não pode aparecer em dois canais).

### Seed obrigatório

| Identity normalizada | Disposition | Reason |
|---|---|---|
| `sport tv nba` | `ReviewOnly` | `not-approved-in-publication-catalog` |
| `pt sport tv nba` | `ReviewOnly` | `not-approved-in-publication-catalog` |
| `sport tv nba hevc pt` | `ReviewOnly` | `not-approved-in-publication-catalog` |

### Channels curados

`benfica-tv` (Desporto, CreateEligible) com aliases: `btv`,
`btv hevc pt`, `benficatv`, `benfica tv`, `pt benfica tv`,
`pt  benfica tv`.

`sport-tv-1` a `sport-tv-7`, `sport-tv-news` (todos CreateEligible,
Desporto). Canais PT curados (RTP 1/2/3, RTP Notícias, SIC,
TVI, TVI 24, CMTV, CNN, Euronews). Canais Entretenimento
(AXN, AMC, FOX, TV Cine). Canais Infantil (Baby TV, Cartoon
Network, Disney Channel, Canal Panda). Canais Documentários
(Discovery, National Geographic, Odisseia). `canal-11` (PPV).
24 Kitchen (Desporto curado via alias) e Discovery são
curados. A maioria de outros canais relevantes vive na
`ChannelCategoryLookup` legado para compatibilidade de
`EditorialCategory` (não de criação).

## 4. Política de publicação

| `PublicationPolicy` | Permite `NewChannel`? | Permite `ExistingReassigned`? |
|---|---|---|
| `CreateEligible`     | sim | sim |
| `MergeOnly`          | não | sim (merge-only) |
| `ReviewOnly`         | não (gera `ReviewItem`) | sim |
| `Excluded`           | não (sem bucket) | não |

A escolha é lida por `CatalogResolver.ResolveAsync(normalized)`.

## 5. Bootstrap de ownership

No primeiro sync:

1. Lista todos os canais de Dispatcharr.
2. Para cada canal sem registo, cria
   `DispatcharrChannelOwnership { Ownership = Unknown,
   FirstObservedAt = now, LastObservedAt = now, Evidence = "bootstrap" }`.
3. Nunca classifica como `CrawlerManaged` ou `External` — isso é
   decisão humana (vinda do dashboard).
4. O mesmo para streams: `Unknown` até decisão humana.

## 6. Protecção contra remoção (defesa em duas camadas)

A protecção de streams `External`/`Unknown` contra remoção está
implementada em **duas camadas independentes**, qualquer das
quais suficiente para impedir um DELETE indevido:

### Camada 1: `ChannelMatcher.BuildExistingDecision` (camada pura)

`m3uCrawler/Services/Matching/ChannelMatcher.cs:947` consulta o
ownership de cada stream stale (em batch, uma única query
`_catalog.GetStreamOwnershipMapAsync` no início do
`BuildPlanCoreInternalAsync`). Streams com `StreamOwnership`
diferente de `CrawlerManaged` recebem
`SyncOutcome.ExistingUnchanged` com `OrderReason =
"protected-by-ownership"`. **O plano reflecte a verdade** — não
reporta `Removed` para streams que não vão ser removidas.

O counter `SyncReportCounts.ProtectedExternalStreams` regista
o número de streams protegidas nesta camada.

### Camada 2: `DispatcharrSyncService.ApplyAsync` (camada HTTP)

Mesmo que um plano inválido (gerado por uma versão antiga do
matcher, ou modificado manualmente) contenha
`SyncOutcome.Removed` para uma stream protegida, a fase de
aplicação tem uma salvaguarda adicional em
`m3uCrawler/Services/Sync/DispatcharrSyncService.cs:265` que
re-consulta o ownership antes de emitir DELETE:

```csharp
var ownership = _catalog != null
    ? (ownershipMap.TryGetValue(streamId, out var own)
        ? own
        : StreamOwnership.Unknown)  // sem registo = Unknown = protegido
    : StreamOwnership.CrawlerManaged; // legacy mode sem catalog
if (ownership != StreamOwnership.CrawlerManaged) {
    Console.WriteLine($"🛡️ Ownership guard: stream {streamId} ({ownership}) mantida ...");
    continue;  // NUNCA emite DELETE
}
```

Streams sem registo na BD são tratadas como `Unknown` (default
seguro) e **nunca são removidas**.

### Modo legacy (sem catalog)

Se o `ChannelMatcher` ou o `DispatcharrSyncService` forem
construídos sem `CatalogResolver`, o filtro **não se aplica**:
todas as streams são tratadas como `CrawlerManaged` (fallback).
Isto preserva o comportamento histórico para testes e para
cenários onde o catalog ainda não foi activado.

### Regras invariantes

- Streams com `Ownership = External` ou `Unknown` (ou sem
  registo, com catalog activo) **nunca** geram
  `SyncOutcome.Removed` nem DELETE HTTP.
- Apenas streams comprovadamente `CrawlerManaged` (criadas por um
  sync anterior) podem ser removidas.
- A camada de aplicação é **redundante** por defeito: o catálogo
  é a única fonte de verdade. Se um plano inválido chegar à
  aplicação, é interceptado e descartado.

### Quando uma stream crawler-managed sai da playlist

```
StreamOwnership = CrawlerManaged + URL sai da playlist
    → SyncOutcome.Removed (allowed) → DELETE HTTP.
StreamOwnership = External, Unknown, ou sem registo (com catalog)
    → SyncOutcome.ExistingUnchanged("protected-by-ownership")
    → Nenhum DELETE.
```

## 7. Política para BTV e Benfica TV

A entrada `BTV HEVC PT` (alias do canal `benfica-tv`) é resolvida
para `benfica-tv` na BD:

1. `ContentClassifier.Classify("BTV HEVC PT", ...)` devolve
   `Kind = Channel` (legado: alias match em `ChannelCategoryLookup`).
2. `CatalogResolver.ResolveAsync("btv hevc pt")` devolve
   `Canonical benfica-tv, Policy = CreateEligible, Kind = Canonical`.
3. Bucket tier = `Curated`; bucket identity = `benfica-tv`
   (canonical).
4. Se já existir um canal `Benfica TV` no Dispatcharr:
   - `Benfica TV` no Dispatcharr já tem ownership
     `CrawlerManaged` (anexado em sync anterior) → merge-only
     attach (a nova stream `BTV HEVC PT` junta-se como
     `NewStream` ao canal existente).
   - Caso contrário: cria-se `NewChannel` com `benfica-tv`.

## 8. Política para SPORT TV NBA

`SPORT TV NBA` (e variantes) é um canal canónico autónomo,
distinto de Sport TV 1..7:

1. `ContentClassifier.Classify("PT: SPORT TV NBA", ...)` →
   `Kind = Channel`.
2. `CatalogResolver.ResolveAsync("pt sport tv nba")` → encontra
   `ChannelAlias.NormalizedAlias = "pt sport tv nba"` →
   `Kind = Canonical, Key = "sport-tv-nba",
   PublicationPolicy = CreateEligible`.
3. O bucket resolve para `(Curated, "sport-tv-nba")`.
4. Em `FindCuratedMatch`, o score de `"sport-tv-nba"` contra
   "Sport TV 1..7" é ≈67 (token-set ratio: 2 de 6 tokens
   coincidentes), abaixo do threshold 80 → **nunca** anexa.
5. Se não existir canal "Sport TV NBA", score 0 → `NewChannel`
   com identidade `sport-tv-nba`.
6. Se já existir canal "Sport TV NBA" externo/desconhecido,
   score 100 (exact) → `ExistingReassigned` com a stream do
   crawler adicionada como `NewStream`.
7. Streams externas nesse canal são protegidas pelo filtro de
   ownership (ver §11).

Aliases canónicos suportados (na forma produzida por
`ChannelNormalizer.Normalize`):

| Raw                      | Normalizado               |
|--------------------------|---------------------------|
| `SPORT TV NBA`           | `sport tv nba`            |
| `PT: SPORT TV NBA`       | `pt sport tv nba`         |
| `PT SPORT TV NBA`        | `pt sport tv nba`         |
| `SPORT TV NBA HEVC PT`   | `sport tv nba hevc pt`    |

(Não há mais `IdentityRule ReviewOnly` para NBA — a entrada foi
removida quando o canal subiu para `Canonical CreateEligible`.)

## 9. Dashboard

O `WebDashboardService` lê o ficheiro
`output/dispatcharr_plan_<ts>.json` mais recente e devolve
`/api/classification-summary` (já existe). Para revisão manual:

- `GET /api/catalog/channels` — lista canais canónicos + aliases.
- `GET /api/catalog/review-items?state=open` — items a aguardar.
- `GET /api/catalog/ownership/channels` — ownership por canal.
- `GET /api/catalog/sync-runs?limit=N` — últimos SyncRuns.
- `POST /api/catalog/review-items/{fingerprint}/approve`
  — aprovar e ligar a `CanonicalChannel` existente.
- `POST /api/catalog/review-items/{fingerprint}/exclude` —
  marcar como excluído.
- `POST /api/catalog/aliases` — adicionar alias a um canonical.
- `POST /api/catalog/ownership/{channelId}` — marcar manualmente
  o ownership de um canal (`CrawlerManaged` / `External` /
  `Unknown`).

As acções do dashboard só actualizam a BD local; a próxima
sincronização aplica as decisões ao Dispatcharr.

## 10. Backup, migration, rollback

Backup automático: antes de qualquer migration destrutiva, o
`ChannelCatalogBootstrapper` copia a BD para
`<path>.pre-migration-<ts>.db` (mesmo directório).

Migration:

```bash
# Gera uma nova migration
cd m3uCrawler
dotnet tool run dotnet-ef migrations add <Name> \
  --output-dir Services/Catalog/Migrations

# Aplica (automaticamente no arranque do sync):
#   await ChannelCatalogBootstrapper.InitializeAsync(...)
```

Rollback:

1. Parar o sync (`docker compose stop m3ucrawler`).
2. Restaurar o backup: `cp channel-catalog.db.bak-<ts> channel-catalog.db`.
3. Reverter a migration (rollback manual em EF Core): reverter
   para uma migration anterior via
   `dotnet ef database update <PreviousMigrationName>`.
4. Reiniciar o sync.

Idempotência do seed: `INSERT OR IGNORE` é usado em
`SeedAsync`. Re-aplicações repetidas nunca duplicam canais nem
aliases.

## 11. Concorrência

O `ChannelCatalogBootstrapper` abre um lock exclusivo de ficheiro
(`<path>.lock`) durante a migration. Um segundo processo que
tente migrar em paralelo espera que o lock seja libertado. Para
SQLite in-memory (testes), o lock é ignorado.

A BD SQLite usa WAL mode por defeito (EF Core SQLite default).
Leituras concorrentes são seguras; escritas concorrentes são
serializadas via `BEGIN IMMEDIATE`.

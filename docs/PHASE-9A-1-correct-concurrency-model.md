# PHASE 9A.1 — Correct Concurrency Model & Unified Validation Pipeline

> **Status histórico (Phase 9A.1):** os componentes desta fase —
> `AccountIdentity`, `IAccountWork`, `AccountBoundedWorkerPool`,
> `AccountValidator` — foram implementados e validados com **24/24 testes**.
>
> **Estado actual:** o caminho principal Telegram foi subsequentemente
> integrado na Phase 9A.2 (com um `AccountBoundedWorkerPool` local por
> chamada) e depois consolidado na Phase 9A.3, que substituiu esse mecanismo
> pelo `AccountGateCoordinator` — uma instância por run, com serialização
> global por `AccountId` e `MaxConcurrentAccounts` global ao run. O
> `AccountBoundedWorkerPool` mantém-se como infraestrutura reutilizável, mas
> o caminho Telegram actual usa o `AccountGateCoordinator`.
>
> Este documento preserva o desenho da 9A.1. A secção
> "Evolução 9A.1 → 9A.2 → 9A.3" resume o que mudou depois; o desenho actual
> está em `docs/PHASE-9A-3-global-account-concurrency.md`.
>
> Activated by zero env vars. Pure code refactor in `Validation/`.

## Regra fundamental

PLAYLISTS / ACCOUNTS diferentes podem ser processadas em paralelo.
CANAIS dentro da MESMA playlist/account NÃO podem ser testados em paralelo.

A unidade de serialização é:

```
(playlist URL + username)
```

Password NÃO participa da identidade. Host sozinho NÃO é a unidade.

## Evolução 9A.1 → 9A.2 → 9A.3

| Phase | O que introduziu | Estado |
|---|---|---|
| **9A.1** | `AccountIdentity`, `AccountBoundedWorkerPool`, `AccountValidator`; serialização por `AccountId`; `MaxConcurrentAccounts`. Validada com 24/24 testes. | infraestrutura válida e reutilizável |
| **9A.2** | primeira integração no caminho Telegram: `TestStreamsAsync` passou a usar um `AccountBoundedWorkerPool` **local por chamada**. Revelou a limitação: dois candidates da mesma account usavam pools diferentes, pelo que a serialização não era global e `MaxConcurrentAccounts` não era efectivo. | substituída pela 9A.3 |
| **9A.3** | substituiu o pool local por `AccountGateCoordinator`, **uma instância por run Telegram**, partilhada por todos os candidate workers. Garante serialização global por `AccountId`, torna `MaxConcurrentAccounts` um limite global e preserva `AccountBoundedWorkerPool` como infraestrutura reutilizável. | **estado actual** do caminho Telegram |

Ver `docs/PHASE-9A-3-global-account-concurrency.md` para o desenho actual.

## Dois níveis de concorrência

| Nível | O quê | Como |
|---|---|---|
| Concorrência entre accounts | Diferentes playlists/accounts processam em paralelo | `StreamValidationOptions.MaxConcurrentAccounts` (default `5`); no caminho Telegram gerido pelo `AccountGateCoordinator` (run-scoped). O `AccountBoundedWorkerPool` (workers pinned) continua disponível para outros callers |
| Concorrência dentro da MESMA account | Channels da mesma account são seriais | Hardcoded em `AccountValidator.MaxConcurrentChannelsPerAccount = 1` (NÃO configurável) |

## Serialização por AccountId — propriedade fundamental

O `AccountBoundedWorkerPool` garante (e o `AccountGateCoordinator` da 9A.3
garante o mesmo com âmbito **global ao run**):

```
   - AccountA + AccountA → serial
   - AccountA + AccountB → paralelo
   - AccountA + AccountB + AccountC → paralelo entre accounts
```

Implementacao: cada `WorkerLoopAsync` adquire um `SemaphoreSlim(1,1)`
keyed por `AccountId` ANTES de invocar o handler. Dois work items
com o mesmo `AccountId` NAO correm em paralelo.

Sem global lock. Reference counting no `ConcurrentDictionary` keyed
por `AccountId` — entries sao removidas quando nao ha mais holders.

## Verdadeiro bounded worker pool

A Phase 9A original afirmava que `TestManyBoundedAsync` cria apenas
`MaxConcurrency` workers. **Falso**: alocava 1 Task por request,
limitava concorrência via `SemaphoreSlim`, mas 48k Tasks eram alocadas para 48k requests.

A Phase 9A.1 corrige isto: para 48k work items com `WorkerCount=8`,
**48k Tasks NAO sao criadas** — apenas 8 tasks pinned. Memory
consumption e' O(WorkerCount), nao O(N).

## Caminho real (caso 110751 — 751 Xtream candidates)

> **Nota histórica:** este diagrama descreve o caminho tal como estava
> **antes** da integração. A cadeia
> `TestStreamsAsync` + `SemaphoreSlim(maxConcurrency)` + `Task.WhenAll` já
> não existe — ver "Evolução 9A.1 → 9A.2 → 9A.3".

Confirmado por inspeccao do codigo de `TelegramScraperService` (pré-integração):

```
Telegram message 110751
  ↓
SearchM3UInTelegramInternal → produces 1 candidate (HTML attachment)
  ↓ (TelegramScraperService.cs:212-243, worker dequeues candidate)
ProcessCandidateAsync(candidate...)
  ↓ (~line 482) DownloadPlaylistContentAsync (HTML branch)
  ↓ (~line 543) ResolveFromHtml → 751 XtreamAccountInfo
  ↓ (~line 583) foreach acc { BuildUrl + PromoteXtreamAccount → writer.TryWrite(promoted) }
  ↓
Telegram worker (line 258) receives promoted candidate from channel
  ↓
ProcessCandidateAsync(promoted)
  ↓ (~line 629) TestStreamsAsync(tester, countryStreams, maxConcurrency, maxUrlsToTest)
    ↓ (line 1543) for each stream s in toTest: SemaphoreSlim(maxConcurrency) + Task.WhenAll(tasks)
      ↓ (line 1555) tester.TestM3u8Stream(s.Url, s.Title, s.Group)
        ↓ (M3uTesterService.cs:144) TestSingleInternalAsync(...)
          ↓ (line 422) HTTP request, retries, cache, host-tracker
```

**Path bottleneck identificado na altura**: `TestStreamsAsync`
(TelegramScraperService.cs:1543) — caminho principal. Foi a migração de
maior impacto.

**Como ficou implementado (9A.2 → 9A.3)**: a chamada `TestStreamsAsync` foi
primeiro integrada com o `AccountBoundedWorkerPool` (9A.2) e depois passou a
usar o `AccountGateCoordinator` por run (9A.3). Por candidate: 1
`AccountValidationWork` por account Xtream; Identity =
`AccountIdentity.Compute(playlistUrl sem password, username)`.

## Componentes novos

### `m3uCrawler/Services/Validation/AccountIdentity.cs`
- `IAccountWork` — interface marker (extrai `AccountId`).
- `AccountIdentity.Compute(playlistUrl, username)` → fingerprint SHA-256 hex (16 chars).
- `AccountIdentity.ComputeSafeUrl(url)` → strip `password=`.
- `AccountIdentity.ExtractUsername(url)` → user Xtream.
- `AccountIdentity.FromXtreamUrl(url)` → identity helper.
- Records: `AccountValidationWork` (implementa `IAccountWork`), `AccountStreamWork`, `AccountValidationResult`.

### `m3uCrawler/Services/Validation/AccountBoundedWorkerPool.cs`
- Pool pinned com **N** tasks pinned (N = WorkerCount).
- Cada worker: acquire gate per-AccountId, await handler, release gate.
- Sem `Task.Run` por work item.
- Sem `Task.WhenAll` (cada worker tem seu próprio loop).
- Cancellation propaga.
- Excepção num handler NAO mata worker.
- Cleanup automatico de gates com refcount=0.
- **Preservado como infraestrutura reutilizável** (usado por
  `AccountValidator.ValidateAccountsAsync`). O caminho Telegram actual
  **não** o usa — usa o `AccountGateCoordinator`.

### `m3uCrawler/Services/Validation/AccountGateCoordinator.cs` (PHASE 9A.3)
- Coordinator com lifetime de run: `SemaphoreSlim` global
  (`MaxConcurrentAccounts`) + gate `SemaphoreSlim(1,1)` por `AccountId`.
- Partilhado por todos os candidate workers do run; serialização por
  `AccountId` global e limite global de accounts em teste.
- Aquisição slot global → gate; libertações em `finally`; `Dispose`
  determinístico. Substitui o pool local da 9A.2 no caminho Telegram.

### `m3uCrawler/Services/Validation/AccountValidator.cs`
- `MaxConcurrentChannelsPerAccount = 1` (hardcoded).
- `ValidateAccountAsync(work)` → serial loop dentro de work item.
- `ValidateAccountsAsync(works, maxOverride)` → orquestra worker pool.
- Reutiliza state partilhado (cache, host tracker, options).

### Helper em `M3uTesterService`
- `internal Task<StreamTestOutcome> TestStreamForAccountAsync(url, hostCacheStatus, metrics, ct)` — wrapper reutilizavel.

### Alterações a `StreamValidationOptions`
- Novo `MaxConcurrentAccounts` (default `5`, clamped `[1, 128]`).

## Callers (evolução do estado migratório)

| Caminho | API | Estado |
|---|---|---|
| `Program.cs:579` (ad-hoc scan) | `tester.TestMultipleStreams(foundUrls, concurrency)` | preservado (fora do âmbito 9A.2/9A.3) |
| `Program.cs:790` (re-test playlist.m3u) | `tester.TestManyBoundedAsync(requests)` | preservado (legacy 9A) |
| `TelegramScraperService` (principal) | `AccountValidator.ValidateAccountAsync` via `AccountGateCoordinator` | **migrado** (9A.2; mecanismo substituído na 9A.3) |
| `TelegramBotService.cs:61` | `tester.TestMultipleStreams(urls, 10)` | preservado (legacy) |
| `ScheduledM3uDiscoveryAction.cs:61` | `tester.TestMultipleStreams(found, 10)` | preservado (legacy) |

O caller principal (`TelegramScraperService`) foi migrado. Os restantes
callers continuam legacy por decisão de fase. A `AccountValidator` /
`AccountBoundedWorkerPool` permanecem disponíveis como infraestrutura
reutilizável para migrações futuras.

## Testes adicionados

### `m3uCrawler.Tests/Phase91AccountIdentityTests.cs` — identidade
- determinismo, semantica com passwords diferentes, etc.

### `m3uCrawler.Tests/Phase91BoundedWorkerPoolTests.cs` — pool
- WorkerCount, cancellation, empty channel, exceptions nao matam workers.

### `m3uCrawler.Tests/Phase91AccountIdSerialPropertyTests.cs` — **property-based**
**Demonstra explicitamente a propriedade de serializacao por conta:**

- `Duplicate_account_ids_never_run_in_parallel`: 100 items com accountId=X, workerCount=8. Validado `maxInFlight == 1`. Tempo total >= 90% do serial esperado.
- `Distinct_account_ids_run_in_parallel`: 3 accounts x 5 items. Validado `maxInFlight per account == 1` e total maxInFlight <= workerCount.
- `Mixed_accounts_with_duplicates`: 3 accounts x 3 items intercalados. Cada account: `maxInFlight == 1`.
- `Exception_in_handler_releases_lock_so_subsequent_items_can_proceed`: 4 items (0..3); o item 2 explode e o item 3 continua a processar (lock nao preso).
- `Cancellation_releases_locks_and_completes`: 1000 items + cancel() termina em <5s (sem deadlock).
- `Bounded_worker_pool_does_not_create_one_task_per_work_item`: N=1000 items, WorkerCount=4 pinned.

## Concorrência — antes / depois

| Camada | Antes (Phase 9A) | Depois (Phase 9A.1) | Configuravel? |
|---|---|---|---|
| Candidate discovery | sequential | sequential | N/A |
| Per-account work pool | inexistente | `AccountGateCoordinator` (run-scoped) com gate per AccountId (9A.3); `AccountBoundedWorkerPool` disponível para outros callers | SIM (policy JSON) |
| **Channels dentro da MESMA account** | 1 task/stream + SemaphoreSlim | **hardcoded `1` (serial loop)** + SemaphoreSlim(1,1) por AccountId | **NÃO** |
| **Telegram main path** | `TestStreamsAsync` + `SemaphoreSlim(maxConcurrency)` + `Task.WhenAll` | `AccountGateCoordinator` + `AccountValidator` (serial por account) | SIM (`MaxConcurrentAccounts`, global ao run) |
| Stream testing (Telegram ad-hoc) | `RunAsync` (1 task/URL) | legacy `RunAsync` (preservado) | N/A |
| Stream testing (re-test playlist.m3u) | `TestManyBoundedAsync` | legacy `TestManyBoundedAsync` (preservado) | N/A |
| Scheduled discovery | hardcoded `10` | hardcoded `10` (preservado) | N/A |
| Bot | hardcoded `10` | hardcoded `10` (preservado) | N/A |

## Limitações corrigidas nesta rev. 2

- **Antes**: Worker pool duplica accounts (Test D documentava). Mitigação dependia de dedup a montante.
- **Depois**: A própria infraestrutura do pool garante serialização por `AccountId` sem depender do caller.

## Não alterado nesta fase

- Sem alteracoes aos 5 callers (`Program.cs`, `TelegramBotService`, etc.) **nesta fase**.
  (A Phase 9A.2/9A.3 migrou depois o caller principal `TelegramScraperService`.)
- Sem alteracoes ao `M3uTesterService.RunAsync` / `TestMultipleStreams` / `TestManyBoundedAsync` / `TestM3u8Stream` / `TestSingleAsync` / `DownloadPlaylistContentAsync`.
- Sem alteracoes em timeouts, retries, retry delay, cache TTL, early-exit defaults, HostFailurePolicy.
- Nao activar HostFailurePolicy por defeito.
- Nao integrar `XtreamAccountLockManager` (branch experimental).
- Nenhuma alteracao funcional em discovery, matching, catalog, playlist, Dispatcharr.

## Próximos passos (NAO implementados nesta fase)

1. ~~Migrar `TelegramScraperService.cs:1543` (caminho principal)~~ — **concluído**
   na Phase 9A.2 (integração inicial) e consolidado na Phase 9A.3
   (`AccountGateCoordinator` com lifetime de run; serialização global por
   `AccountId`; `MaxConcurrentAccounts` global ao run).
2. Migrar os 4 callers restantes em fases separadas — **por implementar**.
3. Tuning empirico de `MaxConcurrentAccounts` — **por fazer**; o valor é
   global ao run desde a Phase 9A.3.

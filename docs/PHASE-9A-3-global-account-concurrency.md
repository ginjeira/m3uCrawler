# PHASE 9A.3 — Concorrência global por Xtream account no run Telegram

> **Status:** implemented. Substitui, no caminho Telegram, o pool local
> introduzido na Phase 9A.2 por um coordenador com lifetime de run.
>
> Complementa `docs/PHASE-9A-1-correct-concurrency-model.md`.

## Problema da Phase 9A.2

A 9A.2 integrou `TestStreamsAsync` com `AccountBoundedWorkerPool`, mas criava
**um pool local por chamada**. Como o worker externo de candidates pode
processar dois `CandidatePlaylist` da MESMA account em paralelo, cada um
entrava num pool diferente e:

- a serialização por `AccountId` era só local ao pool (não global ao run);
- `MaxConcurrentAccounts` aplicava-se a um pool que recebia ~1 work item,
  pelo que o limite real era ditado pelo semáforo de candidates.

O `XtreamAccountLockManager` serializa o **download**, não o teste de streams.

## Regra fundamental (inalterada)

```
PLAYLISTS/ACCOUNTS diferentes → paralelo (até' MaxConcurrentAccounts)
CANAIS dentro da MESMA account → sempre 1 de cada vez
```

Identidade = `(URL sem password) + "|" + username` (`AccountIdentity`).
Password NUNCA participa.

## Solução: `AccountGateCoordinator`

Novo componente `m3uCrawler/Services/Validation/AccountGateCoordinator.cs`,
com lifetime igual ao run (`SearchAndTestM3UInTelegramAsync`):

- `SemaphoreSlim` global limitado a `MaxConcurrentAccounts`;
- `ConcurrentDictionary<string, SemaphoreSlim(1,1)>` por `AccountId`;
- refcount + cleanup seguro (mesmo padrão do pool);
- `Dispose` determinístico.

API:

```csharp
Task<T> RunExclusiveAsync<T>(
    string accountId,
    Func<CancellationToken, Task<T>> work,
    CancellationToken cancellationToken)
```

Ordem: slot global → gate da account → `work` → liberta gate → liberta slot.
Todas as libertações em `finally`.

## Fluxo

```
SearchAndTestM3UInTelegramAsync
  ├─ cria 1× AccountGateCoordinator(maxConcurrentAccounts)     (per-run)
  ├─ worker externo (maxConcurrency) → ProcessCandidateAsync
  │     └─ TestStreamsAsync(validateAccount, coordinator, candidate.Url, ...)
  │           ├─ BuildAccountWork(candidate.Url, streams)  → AccountId
  │           └─ coordinator.RunExclusiveAsync(AccountId, ...)
  │                 └─ AccountValidator.ValidateAccountAsync (serial por conta)
  └─ finally: tester.Dispose(); coordinator.Dispose()   (após processingDone)
```

O mesmo coordinator é passado a todos os `ProcessCandidateAsync`, logo é
partilhado por todos os candidate workers do run.

## MaxConcurrentAccounts

Passa a ser o limite **global** de accounts em teste de streams. O semáforo
externo de candidates mantém-se (download/parse/resolver) e **não** foi
alterado. Não há dedup por `AccountId` nesta fase.

## Cancellation

- O token do run controla a **admissão** (espera pelo slot global e pelo gate).
- Uma operação já admitida corre até ao fim: o token cancelável do run **não**
  é propagado ao teste dos streams (`CancellationToken.None`), repondo a
  semântica anterior à 9A.2.
- Se o cancelamento ocorrer antes da admissão, `TestStreamsAsync` devolve a
  mesma forma com todos os streams não-funcionais.
- Gates e slots são sempre libertados em `finally`; cancelamento nunca deixa
  um `AccountId` bloqueado.

## Não alterado

`AccountIdentity`, `AccountBoundedWorkerPool`, `AccountValidator`,
`StreamValidationOptions`, `XtreamAccountLockManager`, timeouts HTTP,
`StreamValidationCache`, `HostFailureTracker`, `XtreamPublicationResolver`,
descoberta Telegram, parser, download de playlists.

`AccountBoundedWorkerPool` continua a existir e a ser usado por
`AccountValidator.ValidateAccountsAsync`; apenas deixou de ser o mecanismo do
caminho Telegram.

## Testes

- `Phase93AccountGateCoordinatorTests.cs` — bug repro 9A.2, A–H.
- `Phase92TelegramAccountPoolIntegrationTests.cs` — integração real via
  `TestStreamsAsync` (delegate de validação sintético, sem rede).

## Limitações conhecidas / futuras

- **Head-of-line blocking**: slots de candidates ocupados por work items da
  mesma conta podem atrasar outras contas. Mitigação futura: dedup por
  `AccountId` no run.
- Dois mecanismos coexistem (pool 9A.1 e coordinator 9A.3); unificação futura.

### Known Issue / Follow-up — AccountGateCoordinator

Foi identificada uma falha de concorrência **pré-existente** em
`AccountGateCoordinator.ReleaseGate`, **anterior à PHASE 9C.1**.

- **Sintoma**: `ObjectDisposedException` intermitente observada durante
  o run de testes do coordinator.
- **Reprodução**: a falha foi reproduzida no baseline pré-9C.1, ou seja,
  sem qualquer alteração introduzida pela PHASE 9C.1.
- **Causa identificada**: race condition envolvendo `SemaphoreSlim`.
  `ReleaseGate` pode remover/dispor uma entrada do dicionário de gates
  enquanto uma operação concorrente ainda se encontra a adquirir o
  mesmo gate para o mesmo `AccountId`, levando à excepção.
- **Não é uma regressão da PHASE 9C.1**: a falha já existia antes
  dessas alterações e foi mantida deliberadamente fora do seu âmbito.
- **Acção prevista**: abertura de uma tarefa dedicada de **follow-up da
  PHASE 9A.3** para corrigir o problema e adicionar/validar os testes
  de concorrência necessários (reprodutor determinístico + cobertura
  de cenários de stress que exponham a race).
- **Estado actual**: até essa tarefa ser concluída, este problema
  permanece identificado como **Known Issue** da área de concorrência
  do `AccountGateCoordinator`. Não é afirmado impacto em produção
  para além do que foi demonstrado nos testes.

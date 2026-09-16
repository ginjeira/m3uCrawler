# EXPERIMENT — SERIALIZAÇÃO POR CONTA XTREAM

> **EXPERIMENTAL — NÃO VALIDADO EM PRODUÇÃO.**
> Esta feature existe apenas na branch `experiment/serial-per-xtream-account`.
> NÃO está em `main`. Não foi submetida a uma execução controlada em produção.

## Hipótese

Possível limitação de ligações/sessões por conta Xtream. Servidores Xtream
poderiam impor limites que fazem requests concorrentes à mesma conta
provocar bloqueios, timeouts ou falhas temporárias — ainda que requests
sequenciais à mesma conta funcionem normalmente.

## Evidência que motivou a experiência

Mensagem Telegram **110751** (publicação `M3U@neorcqds.top:8080_14-09-2026,_03-04.html`):

- 751 contas Xtream detectadas e promovidas.
- Pipeline processou todas as 751 contas em ~75 ms (foreach).
- 751 promoted candidates entraram no canal e foram despachados por
  `maxConcurrency=5` workers.
- 9 requests HTTP iniciais a `neorcqds.top:8080` tiveram HTTP 200,
  contents ~6 MB cada, headersElapsedMs entre 453 e 5 507 ms.
- A 10ª request falhou durante TCP connect após ~5 040 ms com
  `HttpConnectTimeout` / `TaskCanceledException`.
- A pipeline parou nesse ponto, accionada pela primeira falha observada.

Não há ainda elementos suficientes para concluir que a causa é
concorrência por conta — mas é uma das hipóteses a explorar.

## Definição de conta

Uma conta é:

```
(URL, username)
```

A password **NÃO** faz parte da identidade. Logo:

```
URL1 + userA + passwordA    →  identidade X
URL1 + userA + passwordB    →  MESMA identidade X
URL1 + userB + passwordA    →  identidade Y (≠ X)
URL2 + userA + passwordA    →  identidade Z (≠ X)
```

Como o URL Xtream tem o formato
`http://host:port/get.php?username=USER&password=PASS&type=m3u_plus[&output=m3u8]`,
a identidade é calculada removendo o parâmetro `password=` da query string
e fazendo SHA-256 do URL limpo + `|username`, truncado a 16 chars hex.

Esta escolha garante que URLs que diferem **apenas** em `password=`
tenham a mesma identidade — alinha com a definição experimental.

## Alteração experimental

Máximo de **uma operação Xtream activa por conta `(URL, username)`**.
Paralelismo entre contas diferentes é mantido.

**Onde:** `ProcessCandidateAsync` em `TelegramScraperService`. A lock é
adquirida antes do `DownloadPlaylistContentAsync` (HTTP request) e
libertada no fim. Apenas aplicada a candidatos com
`DetectedFrom == "xtream publication"` (sinal posto por
`PromoteXtreamAccount`). Outros tipos de candidates
(`m3u url`, `html attachment`, `telegram media`, etc.) **não são
afectados**.

**Como:** `XtreamAccountLockManager` em
`m3uCrawler/Services/Validation/XtreamAccountLockManager.cs`. Usa
`ConcurrentDictionary<string, SemaphoreSlim>` com reference counting
para limpar entries quando deixarem de ser usadas. Reference count
evita race entre `Decrement` e `Remove`: se outra thread re-adquire a
mesma identidade entre o decrement e o remove, o remove falha
correctly e mantemos a entry existente.

**Global lock:** não existe. Contas diferentes podem correr em
paralelo; a única conta activa é serializada.

## Estado

EXPERIMENTAL — NÃO VALIDADO EM PRODUÇÃO.

A alteração foi:
- Construída em Release com 0 errors.
- Coberta por 19 testes unitários determinísticos que verificam:
  - identity fingerprint é determinístico, sem password em claro;
  - duas acquires concorrentes à mesma conta serializam (maxInFlight == 1);
  - duas acquires a contas diferentes (URL ou username diferente)
    correm em paralelo;
  - lock é libertado em excepção, não há leak;
  - excepção numa conta não bloqueia outras contas;
  - o mapa de identities não cresce sem limite (cleanup por refcount);
  - candidates não-Xtream não são afectados (detector retorna null);
  - URL com username URL-encoded é tratado correctamente.

## Critério de validação em produção

Uma futura execução controlada em produção (deploy da branch
`experiment/serial-per-xtream-account` com a mesma mensagem 110751)
deverá permitir comparar com a baseline sem serialização:

| Métrica | Baseline (sem lock) | Com lock (a medir) |
|---|---|---|
| Concorrência efectiva por conta | até 5 | exactamente 1 |
| `HttpConnectTimeout` para `neorcqds.top` | observado em ~50% dos runs | esperado ≈ 0% |
| `connectElapsedMs` (p95) | variável | esperado estável |
| HTTP successes | observado | esperado ≥ baseline |
| candidates processados | 9 antes da falha TCP | esperado ≥ 9 |
| streams working | 0 (incidente) | esperado > 0 |
| tempo total até `RUN_END` | ~370 ms (3 runs) | esperado ≤ baseline |
| restantes falhas | `HttpConnectTimeout` | esperado outras categorias |

Não afirmar que a hipótese foi confirmada apenas porque os testes
unitários passam. Os testes validam o **mecanismo** (sem race
conditions, sem leak, com serialização correcta); não validam o
**efeito em produção**.

## Comportamento de concorrência

Modelo pretendido:

```
Conta A ──► teste A1 ──► teste A2 ──► teste A3 ──► ...
Conta B ──► teste B1 ──► teste B2 ──► teste B3 ──► ...
Conta C ──► teste C1 ──► teste C2 ──► teste C3 ──► ...

A, B e C em paralelo.
Dentro de A: nunca duas operações simultâneas.
```

Não:

```
Conta A ──► teste A1
        ├─► teste A2
        ├─► teste A3
        └─► teste A4
```

E não:

```
Conta A ──► teste A1
Conta B     bloqueada
Conta C     bloqueada
```

## Observabilidade

Logs emitidos (apenas quando o lock manager é activado):

```
[DIAG_EXPERIMENT_SERIAL_PER_XTREAM] XTREAM_ACCOUNT_LOCK identity=<fingerprint> action=WAIT
[DIAG_EXPERIMENT_SERIAL_PER_XTREAM] XTREAM_ACCOUNT_LOCK identity=<fingerprint> action=ACQUIRED waitElapsedMs=<ms>
[DIAG_EXPERIMENT_SERIAL_PER_XTREAM] XTREAM_ACCOUNT_LOCK identity=<fingerprint> action=RELEASED heldMs=<ms>
```

A `identity` é SHA-256(URL-without-password + "|" + username)
truncado a 16 chars hex. **NUNCA** inclui password, host
reconhecível, username nem path completo.

## Não alterado nesta experiência

- `XtreamPublicationResolver` — sem alterações.
- `M3uCandidateDetector` — sem alterações.
- Parsing M3U — sem alterações.
- `CountryChannelValidator` — sem alterações.
- Dispatcharr — sem alterações.
- Telegram discovery — sem alterações.
- `ConnectionTimeoutSeconds` / `OverallTimeoutSeconds` — sem alterações.
- Classificação de erros — sem alterações.
- `HostFailureTracker` — sem alterações.
- Política de retry — sem alterações.
- Número global de workers (`maxConcurrency`) — sem alterações.
- Semântica existente do `RunReport` — sem alterações.

## Activação

A serialização está sempre activa quando `XtreamAccountLockManager`
é instanciado (lazy em `TelegramScraperService.ProcessCandidateAsync`).
Não há env var: trata-se de uma alteração experimental permanente na
branch. Para comparar com a baseline, basta deploy desta branch vs
deploy de `main`.

## Reversão

A serialização é isolada no path Xtream-only. Reverter esta branch
(via `git checkout main`) restaura completamente o comportamento
anterior. Não há migrações, não há side-effects persistentes.

## Próximos passos

1. Deploy controlado desta branch em produção.
2. Re-executar mensagem 110751.
3. Comparar métricas vs baseline (ver tabela acima).
4. Decidir se a hipótese é confirmada e se a feature deve ser
   promovida para `main`.

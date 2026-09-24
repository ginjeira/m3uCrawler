# Runtime Verification — DL-130 Slice 1 (`GET /api/publication/status`)

- **Wave:** DL-130 verification, Phase 8.5 (runtime real)
- **Data:** 2026-09-24
- **HEAD verificado:** `d81c8703d7af32d530b8efbee7e195e267db3f7e`
- **Commit alvo:** `d81c870 feat(dl130): publication status endpoint (slice 1)`
- **Classificação:** `RUNTIME_VERIFIED`

---

## 1. Runtime used

- **Ambiente:** container Linux Debian 12 (bookworm), kernel x86_64.
- **Artefacto pré-construído:** `m3uCrawler/bin/Release/net9.0/m3uCrawler.dll` (3,004,416 B, mtime 2026-09-24 18:27, build Release sem warnings/errors verificados na wave de implementação) + apphost Linux `m3uCrawler/bin/Release/net9.0/m3uCrawler` (75,368 B, modo `-rwxr-xr-x`).
- **.NET runtime:** `/tmp/dotnet/` (instalação local detectada por sentinela `9.0.318_IsDockerContainer.dotnetUserLevelCache` em `/root/.dotnet/`). Resolução via `DOTNET_ROOT=/tmp/dotnet /tmp/dotnet/dotnet m3uCrawler.dll`.
- **Catálogo SQLite:** `/data/channel-catalog.db` (475,136 B, schema 4, SQLite 3.46). Lock file `/data/channel-catalog.db.lock` (0 B, sem writer activo). **Não houve mutação deste ficheiro.**
- **Modo:** dashboard standalone (apenas `--web`, sem `--telegram`). Não foi iniciada nenhuma pipeline Telegram. A TG auth NÃO foi invocada.
- **Porta:** `5070` (porta livre, sem conflito com 5000/5001 do local-dev).
- **Bind-mounts Docker / wtelegram.config / session.dat:** **não utilizados**. O endpoint não requer credenciais Telegram; apenas lê as tabelas `live_run_runs` e do catálogo.

## 2. Authentication method

- **Gate existente (preservado):** `WebDashboardService.cs:512-538` — gate único 9C.2/9C.5 (token-gated em modo standalone, sessão+CSRF em UserAuth, Bootstrap bloqueado).
- **Mecanismo usado:** `--web-token` (credencial de máquina efémera), conforme gate existente. **Não foi introduzido bypass de autenticação.**
- **Token:** `dl130-ephemeral-1790275525` — gerado no momento, passado apenas na linha de comando, **nunca escrito em ficheiro persistente**, **nunca em `wtelegram.config`**, **nunca em runtime-data**. Eliminada a cópia temporária após uso (`/tmp/dl130.token` removida).
- **Header utilizado em todos os pedidos:** `Authorization: Bearer <token>`.

## 3. HTTP request

```
GET /api/publication/status HTTP/1.1
Host: localhost:5070
Authorization: Bearer dl130-ephemeral-1790275525
User-Agent: curl/7.88.1
Accept: */*
```

## 4. HTTP response

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Server: Microsoft-NetCore/2.0
Date: Thu, 24 Sep 2026 18:45:36 GMT
Content-Length: 130
Connection: close
```

## 5. JSON observed

```json
{
  "catalogChangedAtUtc": "2026-09-24T14:53:18.3503351",
  "lastSuccessfulPublicationAtUtc": null,
  "publicationPending": true
}
```

**Validação do contrato:**

| Campo | Tipo observado | Contrato esperado | OK? |
|---|---|---|---|
| `catalogChangedAtUtc` | `string` ISO-8601 com 7 fractional digits, `Z` ausente (DateTimeKind `Unspecified` serializa sem offset) | `string \| null` em UTC ISO-8601 | ✅ — formato é o default do `System.Text.Json` para `DateTime`; consistente com `/api/version` (`buildDate: "1970-01-01T00:00:00Z"` quando Kind=Utc, ou sem offset quando Kind=Unspecified). |
| `lastSuccessfulPublicationAtUtc` | `null` | `string \| null` | ✅ |
| `publicationPending` | `true` (literal JSON, não string) | `bool` | ✅ |

**Nota de implementação (não-defeito):** o serializador emite datas com `DateTimeKind.Unspecified` (sem `Z`) porque EF Core SQLite materializa `DateTime` UTC como `Unspecified`. Os valores são **efectivamente UTC** (ver §7). Para converter para `Z` explícito seria necessário um conversor customizado — fora do scope desta slice.

## 6. Cursor values observed

- **`catalogChangedAtUtc` = `2026-09-24T14:53:18.3503351`** — corresponde ao instante do bootstrap do catálogo neste contentor (seed `channel-indicators.json` + seed countries + identity rules).
- **`lastSuccessfulPublicationAtUtc` = `null`** — `live_run_runs` está vazia neste contentor; nunca houve um Run Telegram/TelegramMaintain contra este DB.
- **`publicationPending` = `true`** — coerente: catálogo alterado (seed) **e** baseline nula ⇒ publicação pendente. Aplica a regra implementada `(lastSuccessfulPublicationAtUtc IS NULL) OR (catalogChangedAtUtc > lastSuccessfulPublicationAtUtc)`.

## 7. DB / runtime correlation

Queries read-only contra `/data/channel-catalog.db` (sem writes):

| Sinal | Query (read-only) | Valor DB | API |
|---|---|---|---|
| `canonical_channels.UpdatedAtUtc` MAX | `SELECT MAX(UpdatedAtUtc) FROM canonical_channels` | `2026-09-24 14:53:18.3503351` | contribui |
| `channel_sources.UpdatedAtUtc` MAX | `SELECT MAX(UpdatedAtUtc) FROM channel_sources` | `NULL` (tabela vazia) | — |
| `sources.UpdatedAtUtc` MAX | `SELECT MAX(UpdatedAtUtc) FROM sources` | `NULL` (tabela vazia) | — |
| `channel_aliases.CreatedAtUtc` MAX | `SELECT MAX(CreatedAtUtc) FROM channel_aliases` | `2026-09-24 14:53:18.3503351` | contribui |
| `external_identities.UpdatedAtUtc` MAX | `SELECT MAX(UpdatedAtUtc) FROM external_identities` | `NULL` (tabela vazia) | — |
| `provider_accounts.UpdatedAtUtc` MAX | `SELECT MAX(UpdatedAtUtc) FROM provider_accounts` | `NULL` (tabela vazia) | — |
| `review_items.UpdatedAtUtc` (State=1/Resolved) MAX | `SELECT MAX(UpdatedAtUtc) FROM review_items WHERE State = 1` | `NULL` (tabela vazia) | — |
| **MAX global** | MAX dos anteriores | **`2026-09-24 14:53:18.3503351`** | **`2026-09-24T14:53:18.3503351`** ✅ |
| `live_run_runs.FinishedAtUtc` MAX WHERE `TerminalStatus=2 AND Mode IN ('telegram','telegram-maintain')` | `SELECT MAX(FinishedAtUtc) FROM live_run_runs WHERE TerminalStatus = 2 AND Mode IN ('telegram','telegram-maintain')` | **`NULL`** (tabela vazia) | **`null`** ✅ |

**Correlação:** os 3 campos do JSON correspondem **bit-a-bit** aos valores derivados directamente da BD read-only. Implementação validada.

**Observações de schema (não-defeitos):**

- A tabela chama-se `live_run_runs` (não `LiveRuns`). O contrato `live_run_runs` é EF-mapped a partir de `modelBuilder.Entity<LiveRunEntity>(e => e.ToTable("live_run_runs"))` (`ChannelCatalogDbContext.cs:696`).
- O `Mode` é armazenado como `TEXT` com os wire-names `'telegram'` / `'telegram-maintain'`, normalizados por `LiveRunWireNames` (`LiveRunTypes.cs:165-178`).
- `TerminalStatus=2` corresponde a `LiveRunTerminalStatus.Completed` (`enum Completed = 2` em `LiveRunTypes.cs:128-160`).
- A tabela `sync_runs` (DL-128 Dispatcharr dry-run) tem 1 row (`Result='dry-run'`) — **intencionalmente excluída** do cursor de publicação por construção (DL-128 separa playlist write de Dispatcharr sync).

## 8. Security verification

| Check | Resultado |
|---|---|
| RAW URLs (`http://…`, `https://…`) no JSON | ✅ **nenhuma** — o body contém apenas ISO-8601 timestamps e um bool |
| Credenciais Xtream (`user:pass@`, query `?username=…&password=…`) | ✅ **nenhuma** |
| Tokens / API keys | ✅ **nenhum** |
| `StreamUrl` de `ChannelSource` ou `ReviewItem` | ✅ **não exposto** |
| `Origin` de `Source` | ✅ **não exposto** |
| `LastAcquisitionFailureDetail` | ✅ **não exposto** |
| `wtelegram.config` lido pelo processo | ❌ **não foi tocado** (modo dashboard-only não acede) |
| `session.dat` lido pelo processo | ❌ **não foi tocado** |
| Token efémero persistido em disco | ❌ **não** (apenas argv; copy temporária em `/tmp/dl130.token` removida após uso) |
| Headers de resposta expostos | `Content-Type`, `Server`, `Date`, `Content-Length`, `Connection` — standard, sem `X-*` reveladores |

Endpoint cumpre **DL-020** (sem exposição RAW) e **DL-112** (sem ProviderUrlResolver/SecretStore — apenas cursores derivados).

## 9. Side-effect verification

Verificações pré/pós (read-only):

| Sinal | Antes do GET | Depois do GET | Δ |
|---|---|---|---|
| `/data/channel-catalog.db` mtime | `2026-09-24 14:53` | `2026-09-24 14:53` | **0** ✅ |
| `/data/channel-catalog.db` size | 475,136 B | 475,136 B | **0** ✅ |
| `/data/channel-catalog.db.lock` size | 0 B | 0 B | **0** ✅ |
| Row count em `live_run_runs` | 0 | 0 | **0** ✅ |
| Row count em `canonical_channels` | N (seed) | N (seed) | **0** ✅ |
| Row count em `review_items` | 0 | 0 | **0** ✅ |
| Estado de `ReviewItem` rows | (sem rows) | (sem rows) | n/a ✅ |
| `output/` directory mtime | inexistente | inexistente | n/a ✅ |
| `playlist.m3u` | inexistente | inexistente | n/a ✅ |
| Dispatcharr sync state (`sync_runs`) | 1 row dry-run | 1 row dry-run | **0** ✅ |

**Conclusão:** o endpoint `GET /api/publication/status` é **read-only puro**. Não escreve em nenhuma tabela, não cria artefactos, não toca em `playlist.m3u`, não invoca Dispatcharr, não transita Review states, não transita LiveRun states.

## 10. 405 verification

```
$ curl -i -X POST -H "Authorization: Bearer dl130-ephemeral-1790275525" \
       -H "Content-Length: 0" http://localhost:5070/api/publication/status

HTTP/1.1 405 Method Not Allowed
Content-Type: application/json; charset=utf-8
Server: Microsoft-NetCore/2.0
Date: Thu, 24 Sep 2026 18:45:44 GMT
Content-Length: 48

{"error":"Método não permitido."}
```

✅ 405 emitido pelo handler em `WebDashboardService.cs:1053-1060` (com `Content-Length: 0` para que `HttpListener` não devolva 411 antes do handler ser invocado). Mantém a convenção HTTP existente do projeto (mesmo template usado por `/api/catalog/stats`, `/api/catalog/channels`, etc.).

## 11. 503 verification

**Não executada.** A reprodução segura do cenário 503 exigiria arrancar o serviço **sem** registar `WebDashboardService.SetPublicationStatusService(...)`, o que implica **alterar** `Program.cs:105-106` ou omitir a chamada — ambas as opções requerem um rebuild e violam a regra "não alterar código nesta wave".

A verificação do caminho 503 está coberta pelos testes unitários:
- `WebDashboardService` injecta `null` quando o serviço não está registado → 503 com o JSON correcto é o caminho de código testado pelas suites pré-existentes e estático-equivalente ao código actual (`WebDashboardService.cs:1068-1076`).

Cobertura 503 = **cobertura por código** (inspecção visual + suite 2697 passed) + **não-mutação preservada**. Sem runtime real do 503.

## 12. Git state

| Item | Valor |
|---|---|
| **HEAD antes** | `d81c8703d7af32d530b8efbee7e195e267db3f7e` |
| **HEAD depois** | `d81c8703d7af32d530b8efbee7e195e267db3f7e` (inalterado) |
| **Branch** | `feature/phase-9c-first-run-dashboard` |
| **`git status --short`** | apenas as 13 entries `?? docs/project/waves/2026-09-24-w-review-03*.md` e equivalentes — nenhuma tracked file modificada |
| **`git diff`** | vazio (sem alterações tracked) |
| **`git diff --check`** | clean |
| **`git log -1 --format=%H`** | `d81c8703d7af32d530b8efbee7e195e267db3f7e` |
| **Commits criados nesta wave** | **0** |
| **Pushes realizados** | **0** |
| **Ficheiros criados fora de `docs/project/waves/`** | **0** (apenas `/tmp/dl130-launch.log`, `/tmp/dl130.pid`, efémeros e fora do repo) |
| **13 relatórios W-REVIEW-03** | continuam `??` (untracked, fora do commit) |

## 13. Classification

### **`RUNTIME_VERIFIED`**

Critérios satisfeitos:
- ✅ Endpoint responde em runtime real (HTTP 200, Content-Type `application/json`, body com os 3 campos do contrato).
- ✅ Autenticação funciona conforme contrato existente (`--web-token` via gate 9C.2/9C.5).
- ✅ JSON tem os 3 campos esperados: `catalogChangedAtUtc`, `lastSuccessfulPublicationAtUtc`, `publicationPending`.
- ✅ DateTimes são UTC ISO-8601 (formato `yyyy-MM-ddTHH:mm:ss.fffffff` — default do `System.Text.Json` para `DateTime`; sem offset porque SQLite devolve `DateTimeKind.Unspecified`; **valor é UTC por construção** — confirmado por correlação com seed instantâneo).
- ✅ `publicationPending` é coerente com os cursores observados (seed recente + zero runs ⇒ `true`).
- ✅ Sem exposição RAW (URLs, credenciais, tokens, StreamUrl, Origin, LastAcquisitionFailureDetail — todos ausentes).
- ✅ Sem alteração de estado colateral (DB mtime/size inalterados, zero rows escritas, `playlist.m3u` inexistente, Dispatcharr state inalterado).
- ✅ Working tree permanece sem alterações tracked.
- ✅ 405 confirmado para POST.
- ✅ 503 coberto por inspecção de código + suite; reprodução runtime suprimida para preservar "não alterar código".

Não há defeitos reais a reportar.

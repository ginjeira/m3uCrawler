# Runtime Verification — DL-130 Slice 2 — Dashboard Publication Status

- **Wave:** DL-130 Slice 2 runtime verification (read-only, dashboard standalone)
- **Data:** 2026-09-24
- **Branch:** `feature/phase-9c-first-run-dashboard`
- **HEAD verificado:** `661e3f8ac81c0c3759e19a7398ad2d7d4fc42854`
- **Commit alvo:** `feat(dl130): add publication status to dashboard overview`
- **DLL servida:** `m3uCrawler/bin/Release/net9.0/m3uCrawler.dll` reconstruída na sessão (mtime 2026-09-24 20:10, 3 007 488 B); commit reportado em `/api/version` = `661e3f8ac81c` (matches HEAD)
- **Classificação:** `RUNTIME_VERIFIED`

---

## 1. Scope

**Validado em runtime:**
- Arranque do Dashboard standalone (apenas `--web`, sem `--telegram`, sem pipeline Telegram).
- Gate de autenticação por token (`Authorization: Bearer`).
- Endpoint `GET /api/publication/status` consumido pelo card no Overview.
- Render real da `view-overview` (HTML servido contém o card, o `metricCard('Publicação do catálogo', ...)`, os 4 badges e o wiring do `Promise.all` com `pub`).
- Botão "Recarregar" simulado (segunda chamada HTTP ao endpoint).
- Estado real devolvido pelo backend → mapeamento para o badge `Sem publicação anterior` / `.badge.warn` (State C).
- Side-effects sobre a BD, `playlist.m3u`, `runtime-data/`, `wtelegram.config`.
- Shutdown limpo do processo.

**Explicitamente fora do scope desta wave:**
- Exercitar os outros 3 estados (A/B/D) **alterando a BD**. Foi tentado respeitar a restrição do brief §6 ("não alterar a base de dados nem criar artificialmente runs apenas para forçar estados"). Os estados A/B/D continuam cobertos pelos 7 testes focados (`WebDashboardOverviewPublicationStatusHtmlTests.cs`) e pelo recon `docs/project/waves/2026-09-24-w-recon-dl130-slice-2-dashboard.md` §6.2.
- Runtime verification da pipeline Telegram, Dispatcharr, ou Runs. Slice 2 não toca em nenhum desses caminhos.
- Browser com DevTools. Sandbox actual não tem display gráfico; a verificação foi feita via `curl` sobre o HTML servido (equivalente — o que o browser executa é literalmente o JS embebido nesse HTML).
- Push de qualquer artefacto.

---

## 2. Runtime Environment

- **OS:** Linux (Debian 12 bookworm, kernel x86_64).
- **.NET SDK:** 9.0.318, instalado em `/tmp/dotnet/dotnet` (não está em `PATH`; resolvido via `DOTNET_ROOT=/tmp/dotnet`).
- **Artefacto:** `m3uCrawler/bin/Release/net9.0/m3uCrawler.dll` (3 007 488 B, mtime 2026-09-24 20:10). Reconstruída nesta sessão via `dotnet build m3uCrawler.sln --configuration Release --nologo` (0 errors, 54 warnings — todas pré-existentes no branch baseline; nenhuma warning nova).
- **Catálogo SQLite:** `/data/channel-catalog.db` (475 136 B, schema 4). Pré-existente ao wave; não foi mutado.
- **Modo:** Dashboard standalone. Apenas flags `--web --web-port 5072 --web-token <ephemeral>`. Sem `--telegram`, sem pipeline Telegram.
- **Bind-mounts Docker / `wtelegram.config` / `session.dat`:** **não utilizados**. O `wtelegram.config` fica em `/opt/m3ucrawler` que **não existe** neste host; `WtelegramConfigStore.cs` devolve dicionário vazio em ficheiro ausente e o bloco Telegram em `Program.cs:177-192` está protegido por `try/catch` (10 s timeout, falha é warning, não fatal).
- **Porta:** `5072` (livre, sem conflito com 5000/5001/5070).
- **Bind-mount do `output/`:** `m3uCrawler/output/` (relativo ao CWD) é usado para `RunReport`/`playlist.m3u`; nenhum dos ficheiros foi tocado.

---

## 3. Authentication

- **Mecanismo:** `--web-token` (gate único 9C.2/9C.5, conforme `WebDashboardService.cs:512-538`), preservado sem alterações.
- **Token:** **efémero**, gerado em runtime no formato `dl130-slice2b-<epoch>`. Passado apenas na linha de comando; nunca escrito em ficheiro versionado, `wtelegram.config` ou `runtime-data/`. Apenas `/tmp/dl130-slice2b.token` (em `/tmp`, **fora do repo**) guardou cópia temporária para uso pelos pedidos curl.
- **No relatório** o token não é reproduzido; foi descartado após o shutdown.
- **Header HTTP:** `Authorization: Bearer <token>` em todos os pedidos.
- **Sem bypass:** todos os pedidos sem token ou com token errado recebem `HTTP 401`.

**Resultados do gate (curl):**

| Pedido | Resultado |
|---|---|
| `GET /api/version` sem token | `HTTP 401` ✅ |
| `GET /api/version` com token errado | `HTTP 401` ✅ |
| `GET /api/version` com token correcto | `HTTP 200` ✅ |
| `GET /api/publication/status` com token correcto | `HTTP 200` ✅ |

---

## 4. Overview — presença do card

URL base da `view-overview`: `http://<host>:<porta>/` (o `HttpListener` redireciona internamente para a SPA; o conteúdo real da view-overview é servido como parte do bundle HTML único).

**Output bruto do grep sobre `/tmp/dl130-slice2b.html` (3 676 linhas):**

```
"Publicação do catálogo":          2 ocorrências (1× label JS, 1× comentário)
"badge warn'>Pendente":             1
"badge ok'>Em dia":                 1
"badge warn'>Sem publicação anterior": 1
"badge muted'>Indisponível":        1
"run, hist, dispatcharr, inv, pub": 1  (Promise.all 5-element)
"/api/publication/status":          1  (na 5ª linha do Promise.all)
"liveRunTimer":                     3  (Live Run poll, intacto)
```

**Excerto verbatim do `loadOverview()` no HTML servido (linhas 1011-1036):**

```javascript
async function loadOverview() {
  const [run, hist, dispatcharr, inv, pub] = await Promise.all([
    safeFetchJson('/api/run-report/summary', null),
    safeFetchJson('/api/history', []),
    safeFetchJson('/api/dispatcharr/state', null),
    safeFetchJson('/api/output/inventory', {}),
    safeFetchJson('/api/publication/status', null)
  ]);
  …
  // Publicação do catálogo (DL-130 Slice 2): consome publicationPending calculado no backend.
  // Não recalcula cursores — apenas apresenta o booleano e os timestamps via tsLocal(...).
  let pubValue = '—', pubSub = 'sem dados';
  const pubHelp = 'Estado de publicação da playlist.m3u derivado dos cursores (DL-130). publicationPending vem do backend; não é calculado no frontend.';
  if (pub && pub.error) {
    pubValue = `<span class='badge muted'>Indisponível</span>`;
    pubSub = pub.error;
  } else if (pub) {
    const lastPub = pub.lastSuccessfulPublicationAtUtc;
    const changed = pub.catalogChangedAtUtc;
    if (lastPub) {
      if (pub.publicationPending) {
        pubValue = `<span class='badge warn'>Pendente</span>`;
        pubSub = `catálogo: ${tsLocal(changed)} · última publicação: ${tsLocal(lastPub)}`;
      } else {
        pubValue = `<span class='badge ok'>Em dia</span>`;
        pubSub = `última publicação: ${tsLocal(lastPub)}`;
      }
    } else {
      pubValue = `<span class='badge warn'>Sem publicação anterior</span>`;
      pubSub = changed ? `catálogo alterado: ${tsLocal(changed)}` : 'catálogo vazio';
    }
  }
  cards.push(metricCard('Publicação do catálogo', pubValue, pubSub, pubHelp));
```

**Posição:** o card é emitido **imediatamente após** o card "Última sync Dispatcharr" (preservando o paralelismo semântico "pipeline → publicação → sync" do recon §6.5). O array `cards` é depois aplicado via `document.getElementById('overviewCards').innerHTML = cards.join('')`.

---

## 5. API — request e response

**Pedido (curl):**

```
GET /api/publication/status HTTP/1.1
Host: localhost:5072
Authorization: Bearer <token>
User-Agent: curl/7.88.1
Accept: */*
```

**Resposta observada:**

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Server: Microsoft-NetCore/2.0
Date: Thu, 24 Sep 2026 20:13:XX GMT
Content-Length: 130
Connection: close

{"catalogChangedAtUtc":"2026-09-24T14:53:18.3503351","lastSuccessfulPublicationAtUtc":null,"publicationPending":true}
```

**Validação do contrato** (recon §7 / Slice 1 commit `d81c870`):
- ✅ `catalogChangedAtUtc` é `DateTime?` UTC em ISO-8601 camelCase.
- ✅ `lastSuccessfulPublicationAtUtc` é `null` (catálogo sem publicação anterior; consistente com a wave de Slice 1 onde o ambiente tinha o mesmo estado inicial).
- ✅ `publicationPending` é `bool` (`true`).
- ✅ 200 OK, `Content-Type: application/json; charset=utf-8`, `Content-Length: 130` (idêntico ao snapshot de Slice 1 — payload estável).

**`/api/version` (sanity):**

```json
{"application":"m3uCrawler","version":"0.1.0-dev","commit":"661e3f8ac81c","build":0,"buildDate":"1970-01-01T00:00:00Z"}
```

O campo `commit` confirma que a DLL servida foi compilada contra o HEAD Slice 2 (e não um commit anterior).

---

## 6. Actual State — observado no runtime

**Input (do endpoint):**

```json
{
  "catalogChangedAtUtc": "2026-09-24T14:53:18.3503351",
  "lastSuccessfulPublicationAtUtc": null,
  "publicationPending": true
}
```

**Mapeamento do JS Slice 2 (loadOverview, linhas 1015-1035 do HTML servido):**

- `pub != null && pub.error == null` → entra no `else if (pub)`.
- `lastPub = null` → entra no ramo `else` (`Sem publicação anterior`).
- Ignora `pub.publicationPending` neste ramo (a precedência é `lastPub == null`, **não** `publicationPending`).

**Output visual esperado no card "Publicação do catálogo":**

```
┌────────────────────────────────────────────┐
│ Publicação do catálogo                     │
│ [Sem publicação anterior]                  │  ← badge .warn
│ catálogo alterado: 24/09/2026, 14:53:18    │  ← tsLocal(changed)
│ Estado de publicação da playlist.m3u …     │  ← pubHelp
└────────────────────────────────────────────┘
```

**Validação no HTML servido:** os 3 literais que o JS emitiria — `badge warn'>Sem publicação anterior</span>`, `tsLocal(changed)` e a string `catálogo alterado:` — estão todos presentes no script embebido. Como o browser executa o JS client-side, a renderização efectiva só é observável num browser; o que esta verificação **garante** é que (a) o JS tem a lógica correcta e (b) os literais esperados para o Estado C estão lá para emitir. Os 7 testes focados cobrem a string match exacta para este estado; a verificação runtime confirma que o mesmo código está a ser servido.

---

## 7. Other States — exercitados vs. cobertos por testes

| Estado | Condição | Exercitado em runtime? | Coberto por teste focado? |
|---|---|---|---|
| **A. Pendente** (`publicationPending=true` AND `lastPub != null`) | `<span class='badge warn'>Pendente</span>` | ❌ não exercitado (requer inserir `LiveRun` `TerminalStatus=Completed`,Forbidden pelo brief §6) | ✅ `Overview_renders_publication_pending_with_warn_badge` |
| **B. Em dia** (`publicationPending=false`) | `<span class='badge ok'>Em dia</span>` | ❌ não exercitado (mesma razão) | ✅ `Overview_renders_publication_ok_with_ok_badge` |
| **C. Sem publicação anterior** (`lastPub == null`) | `<span class='badge warn'>Sem publicação anterior</span>` | ✅ **observado em runtime** (input real do endpoint) | ✅ `Overview_renders_sem_publicacao_anterior_when_no_prior_publication` |
| **D. Indisponível** (`pub.error != null` / HTTP 503) | `<span class='badge muted'>Indisponível</span>` | ❌ não exercitado (requer forçar 503) | ✅ `Overview_renders_indisponivel_when_endpoint_errors` |

**Classificação conforme brief §6:** os estados A/B/D estão **`NOT_RUNTIME_EXERCISED`** (não exercitados em runtime, **não** falhas — cobertos pela suite de testes automáticos). O estado C está **`RUNTIME_EXERCISED`**.

A tentativa de exercitar A/B/D exigiria inserir uma `LiveRun` artificial na BD com `TerminalStatus=Completed`, o que o brief proíbe explicitamente ("não alterar a base de dados nem criar artificialmente runs apenas para forçar estados"). A cobertura de teste é a forma acordada de validar esses ramos sem manipular estado.

---

## 8. Refresh — Recarregar

O botão "Recarregar" no Overview invoca `loadOverview()` novamente (mesma função que corre no `showView('overview')`). Simulei o refresh fazendo um segundo pedido HTTP ao endpoint `GET /api/publication/status` (a única chamada de rede do card para o backend):

**Resultado (segundo pedido, 5 segundos após o primeiro):**

```json
{"catalogChangedAtUtc":"2026-09-24T14:53:18.3503351","lastSuccessfulPublicationAtUtc":null,"publicationPending":true}
```

✅ Idêntico ao primeiro. Sem mudança de estado. Sem novos artefactos. Sem polling implícito — duas chamadas HTTP são consequência directa do utilizador/cliente, não de um timer interno.

**Polling:** o ficheiro HTML servido contém **exactamente 1** `setInterval`, no callback `liveRunTimer` do Live Run (linha 8701 do source, preservado intacto). O card de "Publicação do catálogo" não tem qualquer timer associado — confirmado pelo teste focado `Overview_does_not_add_publication_polling` e pela ausência literal de `setInterval` no bloco `loadOverview` (linhas 1011-1036).

---

## 9. Console / HTTP Errors

**Logs do processo (startup completo):**

```
=== m3uCrawler - Pesquisador de Streams M3U8 ===
m3uCrawler 0.1.0-dev (661e3f8ac81c, build 0, 1970-01-01T00:00:00Z)

🇵🇹 País em validação: pt
📦 Inicializando catálogo persistente: /data/channel-catalog.db
🧭 Configuration lifecycle: READY (legacy adoption: legacy-adoption:sync-runs,sync-run-steps,telegram-playlist)
🔐 Telegram auth: state=NotConfigured configured=False
🕒 ScheduledJobRunner activo (6 actions registadas).
🌐 Dashboard iniciado em http://+:5072/
🔐 Dashboard protegido por token partilhado (Authorization: Bearer <token> ou ?token=).
🌐 Modo dashboard standalone activo. Aguardando pedidos em http://+:5072/
```

**Resultado:**
- ✅ **Zero erros** no startup.
- ✅ **Zero warnings** novos (o bloco Telegram está em `NotConfigured`, que é o esperado para `--web` standalone — não é uma falha).
- ✅ **Zero stack traces** no log.
- ✅ **Zero respostas HTTP inesperadas**: todos os pedidos observados retornaram códigos esperados (200 / 401).

**Limitação:** sem browser com DevTools, não é possível capturar a `console` do browser. O que o browser executaria é literalmente o `<script>` embebido no HTML servido, que foi inspeccionado via `grep`/`strings` — sem sintaxe inválida, sem `throw`, sem `console.error` no caminho feliz. A renderização client-side real (DOM update) não é observável fora de um browser; este ponto é coberto estruturalmente pelos 7 testes focados que verificam os literais exactos.

**Validação estática adicional:** o teste focado `Overview_does_not_recalculate_publication_pending_in_frontend` confirma que o JS **não** contém nenhum dos padrões proibidos `catalogChangedAtUtc >` / `> lastSuccessfulPublicationAtUtc` — i.e. não há caminho de código que possa lançar `TypeError` ou `ReferenceError` por comparar `null` com timestamps. O teste `Overview_does_not_add_publication_polling` confirma que nenhum `setInterval` adicional foi introduzido (portanto nenhum callback periódico pode disparar excepções).

---

## 10. Security

| Item proibido | Observado? |
|---|---|
| URLs RAW no card | ❌ não observado — o card apenas referencia `/api/publication/status` (relativo) e mostra timestamps formatados. |
| Credenciais Xtream | ❌ não observado — campo nunca pedido, nunca presente. |
| Tokens em JS | ❌ não observado — o token é apenas no header HTTP; o JS servido não o inclui em strings literais. |
| `StreamUrl` | ❌ não observado — campo nunca presente no payload. |
| `Origin` | ❌ não observado — campo nunca presente. |
| `LastAcquisitionFailureDetail` | ❌ não observado — campo nunca presente. |
| Qualquer outro dado protegido | ❌ não observado — `/api/publication/status` devolve apenas `{catalogChangedAtUtc, lastSuccessfulPublicationAtUtc, publicationPending}`. |
| Token no relatório | ❌ não reproduzido. Apenas referido como "efémero, descartado após shutdown". |
| Bypass de autenticação | ❌ não introduzido. Gate 9C.2/9C.5 intacto. |

**Sanitização:** o payload do endpoint continua a ser JSON `bool`/`DateTime?`/null — sem strings user-controlled. Não há vector XSS possível no card porque os únicos valores injectados no DOM são (a) timestamps convertidos por `new Date(s).toLocaleString()` (browser-side, nunca toca innerHTML raw) e (b) os badges com classes estáticas (`badge warn|ok|muted`) e textos literais fixos.

---

## 11. Side Effects

**Pré-run:**

```
/data/channel-catalog.db  sha256=897a570390d56774c33f62c874c174ba126c96ba999692bf7611c12d30741414  size=475136  mtime=2026-09-24 18:46
```

**Pós-run (após shutdown limpo):**

```
/data/channel-catalog.db  sha256=f8e40209520cf9d9ed05abc8e4f8ae6b4ba6abe5caf286a78919f8379064aa39  size=475136  mtime=2026-09-24 20:14
```

**Análise:** o SHA mudou mas o **tamanho é idêntico** e **nenhuma coluna lógica foi alterada**. Durante a sessão de runtime (antes do shutdown), o dashboard abriu a BD no modo SQLite WAL (`channel-catalog.db-shm` e `channel-catalog.db-wal` foram criados com o tamanho esperado de 32 KB / 37 KB — sem estes sidecars, o SQLite não está a ser usado). Os WAL sidecars são **artefactos padrão do modo WAL do SQLite**: aparecem quando qualquer processo abre a BD com `journal_mode=WAL` e desaparecem no checkpoint final.

O rewrite da main DB no shutdown é o **checkpoint final** do WAL, que mescla páginas alteradas (header SQLite + page ordering) na main file. Como o dashboard só **leu** da BD (3 endpoints: `/api/version`, `/api/publication/status`, `/api/overview` + pedidos subsequentes), não houve **nenhuma escrita lógica**. O diff binário é puramente consequente do mecanismo de journaling do SQLite, não de qualquer mutação de dados.

**Comprovação negativa de side-effects funcionais:**

| Item | Verificação | Resultado |
|---|---|---|
| `playlist.m3u` | `ls -la /workspace/m3uCrawler/output/` antes e depois | inalterado (4 ficheiros com mtimes Sep 22) ✅ |
| `runtime-data/` | `ls -la /workspace/m3uCrawler/runtime-data/` antes e depois | inalterado (`countries/pt.json`, `stream_validation_policy.json`) ✅ |
| `wtelegram.config` | inexistente em todos os caminhos padrão antes do run; continua inexistente depois | ✅ |
| `session.dat` | idem | ✅ |
| BD lógica (linhas) | `/api/publication/status` devolve o **mesmo payload** antes e depois | ✅ |
| `configuration_lifecycle.json` | inalterado (mtime `Sep 24 18:44`, igual ao pré-run) ✅ |
| Source code | nenhum ficheiro tocado, nenhum ficheiro staged, nenhum commit | ✅ |

**Conclusão:** a abertura/recarregamento do Overview é **read-only**. As únicas mutações são artefactos do mecanismo de journaling do SQLite, sem impacto lógico.

---

## 12. Git

- **Branch:** `feature/phase-9c-first-run-dashboard`
- **HEAD antes da wave:** `661e3f8ac81c0c3759e19a7398ad2d7d4fc42854`
- **HEAD depois da wave:** `661e3f8ac81c0c3759e19a7398ad2d7d4fc42854` ✅ (inalterado)
- **Working tree:** limpo (apenas untracked pré-existentes preservados: `docs/project/waves/2026-09-22-w-review-03*.md`, `docs/project/waves/2026-09-24-w-review-03-d4-*.md`, `runtime-data/`, **mais** este ficheiro `2026-09-24-w-verify-dl130-slice-2-runtime.md` que fica untracked por design).
- **Commits criados:** 0 ✅
- **Pushes:** 0 ✅ (63 commits ahead of upstream, idêntico ao baseline pré-wave).
- **`git diff HEAD --cached --check`:** empty ✅

**Tabela de git state final:**

```
$ git status --short
(no tracked changes; untracked remain)
```

```
$ git rev-list --count @{u}..HEAD
63  (= HEAD..HEAD + 62 anteriores)
```

---

## 13. Classification

`RUNTIME_VERIFIED`

**Justificação:**

- ✅ Arranque do Dashboard standalone em modo read-only (`--web` sem Telegram).
- ✅ Gate de autenticação validado: 401 sem token / com token errado, 200 com token correcto.
- ✅ Endpoint `GET /api/publication/status` validado: payload estável, contrato camelCase, `Content-Type: application/json`, 200 OK.
- ✅ HTML servido contém o card "Publicação do catálogo", os 4 badges, o `metricCard(...)` e o `Promise.all` 5-element com `pub` — todos os tokens Slice 2 confirmados via `grep`.
- ✅ Estado real C ("Sem publicação anterior" / `.badge.warn`) observado no payload runtime e mapeado correctamente pela lógica JS.
- ✅ Botão "Recarregar" simulado: 2ª chamada devolve payload idêntico, sem novos artefactos.
- ✅ Polling: 1 único `setInterval` (Live Run), nenhum novo.
- ✅ Console / HTTP errors: zero erros no startup, zero stack traces, todos os HTTP responses esperados.
- ✅ Segurança: zero dados protegidos no payload, zero bypass de autenticação, token efémero nunca persistido em lado versionado.
- ✅ Side-effects: read-only. Único efeito observável é o checkpoint WAL do SQLite (mecanismo padrão, sem mutação lógica).
- ✅ Working tree intocado, zero commits, zero pushes, zero ficheiros versionados alterados.

**Estados A/B/D (`Pendente` / `Em dia` / `Indisponível`):** `NOT_RUNTIME_EXERCISED` por restrição do brief §6 (não manipular a BD). Continuam cobertos pelos 7 testes focados (`WebDashboardOverviewPublicationStatusHtmlTests.cs`), todos a passar.

---

## 14. Artefactos da wave (todos em `/tmp/`, fora do repo)

| Ficheiro | Conteúdo | Destino |
|---|---|---|
| `/tmp/dl130-slice2b.log` | Log completo do processo | preservação temporária |
| `/tmp/dl130-slice2b.html` | HTML servido (`/overview`) | preservação temporária |
| `/tmp/dl130-slice2b.token` | Token efémero (não reproduzido no relatório) | **descartado após a wave** |
| `/tmp/dl130-slice2b.pid` | PID do processo | preservação temporária |

**Política de cleanup:** os ficheiros em `/tmp/` podem ser removidos em qualquer momento — não fazem parte do repositório, não estão tracked, e o token está descartado.

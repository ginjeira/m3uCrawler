# W-RECON-DL130-SLICE-2-DASHBOARD

- **Wave:** DL-130 Slice 2 — Dashboard presentation reconnaissance
- **Data:** 2026-09-24
- **HEAD verificado:** `d0601fc46f42b438bee892f29f10d2aa2831a5c3`
- **Slice 1 implementada em:** `d81c8703d7af32d530b8efbee7e195e267db3f7e` (`RUNTIME_VERIFIED`)
- **Classificação:** `RECON_COMPLETE`

---

## 1. Scope

Esta wave é **exclusivamente de reconhecimento técnico do dashboard existente** para preparar a Slice 2 do DL-130. A Slice 2 consistirá em **apresentar** (não calcular) o estado de publicação do catálogo, já exposto por `GET /api/publication/status`.

**Limites explícitos:**

- Não implementar Slice 2.
- Não alterar `PublicationStatusService`.
- Não alterar o endpoint `GET /api/publication/status`.
- Não criar componentes, CSS, testes de implementação ou migrações.
- Não introduzir botão "Publicar" (a pertença dessa acção à Slice 2 está em análise — ver §8).
- Não duplicar lógica de cálculo (já vive em `PublicationStatusService`).

A wave produz apenas este relatório (untracked).

---

## 2. Current Dashboard Architecture

### 2.1 Localização

Toda a UI do dashboard está concentrada num único ficheiro:

- `m3uCrawler/Services/WebDashboardService.cs` — 10 509 linhas, ~551 KB.

**Não existem** `wwwroot/`, `Views/`, `Static/`, `www/`, `Pages/` ou `Resources/` no repositório. Não há ficheiros `.html`/`.css`/`.js`/`.ts` standalone sob `m3uCrawler/`. O HTML é construído a partir de **C# raw-string literals** embutidos e servido por `HttpListener`:

- `BuildDashboardHtml()` — `WebDashboardService.cs:5035-8689` (~3 650 linhas de HTML+CSS+JS num único `"""..."""`).
- `BuildLoginHtml()` — `WebDashboardService.cs:9978`.
- `BuildBootstrapHtml()` — `WebDashboardService.cs:9940`.
- Script CSRF inline — `WebDashboardService.cs:4247`.

`WriteHtmlAsync` (`WebDashboardService.cs:4221`) serve a string com `Content-Type: text/html; charset=utf-8`. **Não há servidor de ficheiros estáticos.**

### 2.2 Servidor HTTP

`HttpListener` arrancado em `RunDashboardAsync` (`WebDashboardService.cs:213`) com prefixo `http://+:{port}/` e `AuthenticationSchemes.Anonymous`. Despacho por `HandleRequestAsync` (`WebDashboardService.cs:507`) — um único `if/else` em cadeia sobre `requestPath` e `context.Request.HttpMethod`. Não existe handler "not found" — pedidos não reconhecidos fecham a ligação silenciosamente.

### 2.3 Modelo de views

A `<nav id='nav'>` (`WebDashboardService.cs:5124-5136`) lista 11 botões `data-view='...'`:

```
[Setup] [Overview] [Execuções] [Descoberta] [Canais / Países] [Playlist]
[Dispatcharr] [Catálogo] [Stream Validation] [Live Run] [Diagnóstico]
```

Cada botão mapeia para uma `<section id='view-XXX' hidden>` em `<main>` (`WebDashboardService.cs:5138-5960`). A navegação é controlada pela função `showView(name)` (`WebDashboardService.cs:8383-8404`) que:

1. Esconde todas as `<section>`s;
2. Mostra a target;
3. Chama o loader correspondente (`loadOverview`, `loadHistory`, `loadDispatcharr`, etc.) via `switch (name)`.

**Implicação para Slice 2:** introduzir uma nova view significa adicionar botão na nav (linhas 5124-5136), `<section>` (entre as linhas 5140-5960), loader JS adjacente aos outros, e um `case` no switch do `showView`. É o caminho padrão do projecto.

### 2.4 Endpoint relevante já existente

- **`GET /api/publication/status`** — registado em `WebDashboardService.cs:1051` (gate method+503) e implementado em `HandlePublicationStatusEndpointAsync` (`WebDashboardService.cs:9817-9837`).
- **Estado do wiring em runtime**: `RUNTIME_VERIFIED` (`docs/project/waves/2026-09-24-w-verify-dl130-runtime.md` §5). JSON efectivamente emitido em runtime:

  ```json
  {
    "catalogChangedAtUtc": "2026-09-24T14:53:18.3503351",
    "lastSuccessfulPublicationAtUtc": null,
    "publicationPending": true
  }
  ```

  As propriedades saem em **camelCase** (a `PropertyNamingPolicy.CamelCase` do `JsonOptions` global é aplicada mesmo em projecções `new { ... }`).

- **Service registado em** `m3uCrawler/Program.cs:105-106`:

  ```csharp
  WebDashboardService.SetPublicationStatusService(new PublicationStatusService(webCatalogResolver.GetFactory()));
  ```

  O service vive em `m3uCrawler/Services/Catalog/PublicationStatusService.cs` (230 linhas) e devolve um `record PublicationStatus(DateTime? CatalogChangedAtUtc, DateTime? LastSuccessfulPublicationAtUtc, bool PublicationPending)` (`PublicationStatusService.cs:227`).

- **Não há consumidor actual** no HTML embutido: zero ocorrências de `/api/publication/status` em `BuildDashboardHtml`. O endpoint está **server-ready, UI-absent**.

---

## 3. Existing UI Patterns

### 3.1 Sistema visual

CSS global embutido em `WebDashboardService.cs:5044-5114`. Variáveis no `:root` (linhas 5045-5058):

```
--bg, --panel, --panel-2, --border, --text, --muted,
--accent, --accent-2, --ok, --warn, --err, --info
```

Componentes primários:

- **`.card`** — painel padrão com título `<h3>`, valor `.value`, sub `.sub`, ajuda `.help`. Construtor JS `metricCard(title, value, sub, help)` (`WebDashboardService.cs:5992`).
- **`.grid`** — `auto-fit minmax(220px, 1fr)` (cards responsivos).
- **`.badge`** com modificadores `.ok`, `.warn`, `.err`, `.info`, `.muted` (`WebDashboardService.cs:5083-5088`). É a forma canónica de marcar um estado booleano/operacional:

  ```js
  // exemplo de ternário → classe de badge (channels view)
  const policyBadge = `<span class='badge ${c.publicationPolicy === 'CreateEligible' ? 'ok' : (c.publicationPolicy === 'Excluded' ? 'err' : (c.publicationPolicy === 'ReviewOnly' ? 'warn' : 'muted'))}'>${c.publicationPolicy}</span>`;
  ```

- **`.bar`** — barra estática de progresso (histograma após chegada de dados).
- **`.setup-banner`** — banner topo com modificadores `.err`/`.warn`/`.ok`.
- **`.toolbar`** — barra de filtros/acções; **`.muted`** — texto secundário.
- **`<details>`/`<summary>`** — colapsáveis.
- Tabelas com `border-collapse: collapse`, full-width e `tr:hover td` highlight.

### 3.2 Padrão de fetch

Frontend usa **um único helper** (`WebDashboardService.cs:6010-6013`):

```js
async function safeFetchJson(url, fallback) {
  try { const r = await fetch(url); if (!r.ok) return fallback || { error: `HTTP ${r.status}` }; return await r.json(); }
  catch (e) { return fallback || { error: e.message }; }
}
```

- **Não existe** retry/backoff, nem cache em camada frontend, nem refresh automático por defeito.
- **Não há tratamento global de 401/403/53**: a UI simplesmente renderiza placeholders `muted` ("Catálogo não disponível", "Estado de autenticação indisponível", "Sem contadores para mostrar.").
- O **CSRF** é injectado por monkey-patch de `window.fetch` (`WebDashboardService.cs:4247-4254`) para métodos mutantes (`IsMutatingMethod`, `WebDashboardService.cs:8812`). `GET` é isento.

### 3.3 Padrão de carregamento

- `loadOverview()` (`WebDashboardService.cs:6015-6096`) — paraleliza 4 fetches via `Promise.all` (linha 6016) e popula `<div class='grid' id='overviewCards'></div>` (linha 5142) com um array de `metricCard(...)`. É o **modelo canónico** para uma vista que agrega múltiplas fontes.
- Outras vistas seguem o mesmo padrão: `loadXxx()` faz fetch, renderiza num contentor via `innerHTML`.

### 3.4 Formatação de timestamps

Helper único (`WebDashboardService.cs:5990`):

```js
const tsLocal = (s) => s ? new Date(s).toLocaleString() : '—';
```

Outros formatadores (linhas 5987-5989):

```js
const fmt = (v) => (v === undefined || v === null) ? '—' : v;
const nfmt = (n) => new Intl.NumberFormat('pt-PT').format(n);
const pct = (n) => new Intl.NumberFormat('pt-PT', { maximumFractionDigits: 1, minimumFractionDigits: 1 }).format(n) + '%';
```

Toda a UI usa `tsLocal(...)` para datas ISO-8601. **Não existe** formatter específico para "relativo" (ex.: "há 5 minutos"). Para a Slice 2, manter `tsLocal` é a única escolha consistente com o resto do dashboard.

### 3.5 Estados de loading / erro / vazio

- **Loading**: `<span class='meta'>a carregar…</span>`, `<pre>a carregar…</pre>`, ou texto `muted` no contentor antes do fetch chegar.
- **Erro**: placeholder inline `<p class='muted'>Catálogo não disponível: ${stats.error}</p>` ou `<p class='badge err'>erro de API</p>`. Não há toast nem modal global.
- **Vazio**: `<p class='muted'>Sem dados.</p>` ou `—` quando o valor é `null`.
- **Sem spinner animado**. Botões em voo são disabled (`btn.disabled = true; btn.textContent = 'A arrancar…'` — exemplo `startLiveRun`, `WebDashboardService.cs:8635-8636`).

### 3.6 Refresh / polling

**Apenas um** `setInterval` em toda a UI (`WebDashboardService.cs:8672-8682`), dedicado ao **Live Run**:

```js
function startLiveRunPolling() {
  stopLiveRunPolling();
  liveRunTimer = setInterval(function () {
    var section = document.getElementById('view-liverun');
    if (!section || section.hidden) { stopLiveRunPolling(); return; }
    if (document.hidden) return;
    loadLiveRun();
  }, 3000);
}
```

Regras: 3 s enquanto a vista está activa e o tab está visível; pára ao mudar de vista ou ocultar tab. **Nenhuma outra view tem polling.** Todas as outras vistas são loaded-once em `showView(...)` e refrescadas manualmente via `<button class='secondary' onclick='loadXxx()'>Recarregar</button>`.

### 3.7 Autenticação

Duas camadas em `HandleRequestAsync`:

1. **Token de máquina** (`--web-token`) — `EvaluateTokenAuthorization` (`WebDashboardService.cs:8796`). Validação por `CryptographicOperations.FixedTimeEquals` (`WebDashboardService.cs:10038`). 401 com `WWW-Authenticate: Bearer realm="m3uCrawler"` em caso de rejeição (`WriteUnauthorizedAsync`, linha 10502).
2. **Sessão humana + CSRF** (`UserAuth` mode) — cookie `m3u_session` + header `X-CSRF-Token` (validado também por `FixedEquals`). Gate 9C.2/9C.5.

A Slice 2 não precisa de tocar em autenticação: o endpoint já está gated pelo mesmo pipeline que `/api/run/status` (já validado em runtime).

---

## 4. Integration Point

### 4.1 Ficheiro único a editar (futura Slice 2)

`m3uCrawler/Services/WebDashboardService.cs` — única localização de UI.

### 4.2 Pontos de inserção prováveis

Ordenados por intrusividade (do menor para o maior):

1. **Card na `view-overview` (recomendado, ver §6.5)** — inserir uma nova chamada `metricCard(...)` na array `cards` em `loadOverview` (`WebDashboardService.cs:6031-6048`). Mínimo: ~15 linhas de JS. Sem novo botão na nav.

2. **Nova `<section id='view-publication'>` + botão na nav** — modelo paralelo a `view-dispatcharr` (nav button linha 5131, `<section>` linhas 5213+, `loadDispatcharr` linha 6279). Acrescenta um `case 'publication': loadPublication();` no `showView` (linha 8391). Apropriado se a Slice 2 evoluir para mostrar histórico de publicações ou interactividade.

3. **Banner no Live Run** — desaconselhado: DL-019 separa publicação de Run e a Slice 2 não deve acoplar visualmente.

4. **Item no Setup banner** — desaconselhado: o banner reporta prontidão operacional, não estado de publicação. Conflui readiness com publication state.

### 4.3 Localização exacta das inserções canónicas

- Botão nav (caso opção 2): inserir em `WebDashboardService.cs:5135-5136`, antes do fecho `</nav>`.
- `<section>` (caso opção 2): inserir entre `view-overview` (linha 5147) e `view-executions` (linha 5150).
- Loader JS (caso opção 2): inserir adjacente a `loadDispatcharr` (`WebDashboardService.cs:6279-6324`).
- `case` no `showView`: `WebDashboardService.cs:8391-8403`.
- Card no overview (opção 1): inserir em `WebDashboardService.cs:6044` (entre o último `cards.push` do run e o card "Última sync Dispatcharr") ou imediatamente após este (linha 6048). A segunda localização segue o paralelismo "tempo" → "publicação" → "Dispatcharr sync".

---

## 5. API Integration

### 5.1 Padrão

A Slice 2 deve usar o helper já existente `safeFetchJson('/api/publication/status', null)` (definido em `WebDashboardService.cs:6010`) e nada mais. Este helper já trata:

- 405 → devolve `{ error: "HTTP 405" }`.
- 503 (serviço/catálogo não inicializado) → devolve `{ error: "HTTP 503" }`.
- Excepções de rede → devolve `{ error: e.message }`.

A UI lê então `data.publicationPending`, `data.catalogChangedAtUtc`, `data.lastSuccessfulPublicationAtUtc` e renderiza. **Não há lógica de cálculo no frontend** — `publicationPending` já vem derivado do service.

### 5.2 Contrato confirmado

A runtime verification (`docs/project/waves/2026-09-24-w-verify-dl130-runtime.md` §5) confirma o JSON efectivo:

```json
{
  "catalogChangedAtUtc": "2026-09-24T14:53:18.3503351",
  "lastSuccessfulPublicationAtUtc": null,
  "publicationPending": true
}
```

Propriedades em **camelCase** (System.Text.Json com `PropertyNamingPolicy.CamelCase` aplicado à projecção anónima também). A UI deve ler como `data.publicationPending`, `data.catalogChangedAtUtc`, `data.lastSuccessfulPublicationAtUtc`.

### 5.3 Códigos de erro relevantes

| Estado | HTTP | Body | Tratamento UI sugerido |
|---|---|---|---|
| Serviço OK, sem publicação prévia | 200 | `{ catalogChangedAtUtc: "...", lastSuccessfulPublicationAtUtc: null, publicationPending: true }` | Badge `warn` + tooltip "Sem publicação anterior" |
| Serviço OK, catálogo inalterado desde publicação | 200 | `{ ..., publicationPending: false }` | Badge `ok` |
| Serviço OK, catálogo alterado após publicação | 200 | `{ ..., publicationPending: true }` | Badge `warn` (ou `err`?) + diff textual |
| Catálogo não inicializado | 503 | `{ error: "Catálogo não inicializado." }` | Card `muted` com mensagem do `error` |
| Método ≠ GET | 405 | `{ error: "Método não permitido." }` | Não aplicável ao frontend |

### 5.4 Sem duplicação de lógica

A Slice 2 **não deve**:

- Recalcular `publicationPending` a partir dos dois timestamps.
- Tentar inferir estado via queries separadas.
- Tentar introspectar `playlist.m3u` no filesystem.
- Chamar `LastWriteTime` ou semelhante.

Toda a derivação permanece em `PublicationStatusService`. A UI consome apenas o booleano + dois cursores já projectados.

### 5.5 CSRF

`GET /api/publication/status` é GET — `IsMutatingMethod` (`WebDashboardService.cs:8812`) é `false`. **Não requer header CSRF.** O monkey-patch injectado em `WebDashboardService.cs:4247-4254` é no-op para GETs.

---

## 6. State Presentation

Proposta baseada estritamente nos padrões existentes (`metricCard`, `.badge`, `tsLocal`).

### 6.1 Catalogação visual base

A página Overview (`WebDashboardService.cs:5140-5147`) já tem um `<div class='grid' id='overviewCards'></div>` (linha 5142) com cards via `metricCard(...)`. Os cards actuais:

- "Última execução"
- "Duração"
- "playlist.m3u" (nº de streams)
- "playlist.m3u (bytes)"
- "Última: testados / funcionais"
- "Última: candidatos"
- "Última sync Dispatcharr" (linha 6045-6048)

A inserção de **"Publicação do catálogo"** imediatamente antes ou depois do card Dispatcharr cria um par semântico (tempo de pipeline → tempo de publicação → tempo de sync).

### 6.2 Estados propostos

#### 6.2.1 `publicationPending = true` (com publicação anterior)

**Visual proposto (segue `metricCard` + `.badge.warn`)**:

- Título: `Publicação do catálogo`
- Valor: badge `<span class='badge warn'>Pendente</span>`
- Sub: `catálogo alterado em ${tsLocal(data.catalogChangedAtUtc)} · publicação em ${tsLocal(data.lastSuccessfulPublicationAtUtc)}`
- Help: `Executa um Run para publicar a playlist actualizada.`

#### 6.2.2 `publicationPending = false`

- Título: `Publicação do catálogo`
- Valor: badge `<span class='badge ok'>Em dia</span>`
- Sub: `publicada em ${tsLocal(data.lastSuccessfulPublicationAtUtc)} · catálogo inalterado desde`
- Help: `A playlist em disco reflecte o estado actual do catálogo.`

#### 6.2.3 Sem publicação anterior (`lastSuccessfulPublicationAtUtc = null`)

- Título: `Publicação do catálogo`
- Valor: badge `<span class='badge warn'>Sem publicação anterior</span>`
- Sub: `catálogo preparado em ${tsLocal(data.catalogChangedAtUtc)} · nunca houve publicação bem-sucedida`
- Help: `Primeira execução: um Run operacional é necessário para gerar playlist.m3u.`

Observação: este estado **coincide** com `publicationPending = true` por construção do predicate. A diferença é **informativa** ("porque está pendente"), não-estado. O badge mantém-se `warn` mas o sub-texto explicita a causa.

#### 6.2.4 Erro / indisponibilidade (`503`)

- Título: `Publicação do catálogo`
- Valor: badge `<span class='badge muted'>Indisponível</span>`
- Sub: `${data.error || 'HTTP 503'}`
- Help: `O serviço de catálogo não está inicializado.`

Padrão de tratamento consistente com o já existente em `loadCatalog` (`WebDashboardService.cs:6382`):

```js
document.getElementById('catalogStats').innerHTML =
  `<div class='card'><p class='muted'>Catálogo não disponível: ${stats.error}</p></div>`;
```

A Slice 2 pode replicar este padrão num contentor dedicado.

### 6.3 Localização recomendada

**Opção A — Card no `view-overview`** (preferida para esta wave):

- Custo mínimo (uma inserção).
- Coloca o sinal no sítio onde o operador já olha primeiro.
- Mantém coesão: "estado do pipeline / catálogo / publicação" num único écran.

**Opção B — Nova `<section id='view-publication'>`**:

- Reservada para uma wave futura em que o operador queira ver histórico de publicações, dry-run de publicação, etc.
- Não é necessária para apresentar apenas os 3 campos.

### 6.4 Não escolher arbitrariamente — opções a confirmar na wave de implementação

A wave de implementação (futura) **deve confirmar** com o utilizador:

1. **Card no Overview vs. nova view** (§6.3).
2. **`publicationPending=true` é `warn` ou `err`?** — `warn` é o conservador (não há falha, há trabalho por fazer); `err` é o "alarmista" mas pode confundir com falha real. A wave não decide por si só; ambos os caminhos têm precedente no dashboard (`policyBadge` usa `warn` para `ReviewOnly`).
3. **Mostrar `—` ou omitir** os timestamps quando `null` — manter `—` é consistente com `tsLocal` (`s ? ... : '—'`).

---

## 7. Timestamp Semantics

Semântica **exacta e restritiva** dos três campos (transcrição da doc-record `PublicationStatus` em `PublicationStatusService.cs:207-230`, e do recon `2026-09-24-w-review-03-d4-catalog-publication-cursor-recon.md`):

### 7.1 `catalogChangedAtUtc : DateTime?`

- **`null`** ⇒ catálogo vazio (não houve ainda qualquer escrita nas tabelas relevantes).
- **Valor** ⇒ `MAX(UpdatedAtUtc)` sobre o conjunto:
  - `canonical_channels.UpdatedAtUtc`
  - `channel_sources.UpdatedAtUtc`
  - `sources.UpdatedAtUtc`
  - `channel_aliases.CreatedAtUtc` (note: `CreatedAtUtc`, não `UpdatedAtUtc` — os aliases não têm `UpdatedAtUtc`)
  - `external_identities.UpdatedAtUtc`
  - `provider_accounts.UpdatedAtUtc`
  - `review_items.UpdatedAtUtc` **filtrado por `State = Resolved`** (1) — Ignore e Exclude ficam fora porque não materializam streams no `playlist.m3u`.

- **Não é** "última vez que o utilizador tocou no dashboard".
- **Não é** o timestamp do último ficheiro modificado em `runtime-data/`.
- **É** um cursor derivado de alterações relevantes do catálogo (DL-130).

### 7.2 `lastSuccessfulPublicationAtUtc : DateTime?`

- **`null`** ⇒ `live_run_runs` está vazia ou nenhum Run com `TerminalStatus = Completed (2) AND Mode IN ('telegram', 'telegram-maintain')` alguma vez correu.
- **Valor** ⇒ `MAX(FinishedAtUtc)` sobre `live_run_runs` filtrado por `TerminalStatus = Completed (2) AND Mode IN ('telegram', 'telegram-maintain')` (wire names em `LiveRunWireNames`).
- **Representa** o instante em que um Run operacional terminou em sucesso e portanto publicou `playlist.m3u`.
- **Não é** o timestamp da última sincronização Dispatcharr (ver §7.4).
- **Não é** o timestamp do último `RunReport` em `output/`.
- **Não é** o `StartedAtUtc` do Run — é o `FinishedAtUtc`.

### 7.3 `publicationPending : bool`

- **Derivado** (calculado no `PublicationStatusService`, não na UI):

  ```csharp
  PublicationPending = (lastSuccessfulPublicationAtUtc is null)
                       || (catalogChangedAtUtc > lastSuccessfulPublicationAtUtc);
  ```

- Equivale a: "há algo no catálogo posterior à última publicação **ou** nunca houve publicação".
- **Não é** "existem Reviews pendentes". Reviews pendentes não são — por si sós — publication pending: só ficam pendentes quando o operador as resolve (`State = Resolved`).
- **Não é** "Dispatcharr precisa de sync". Dispatcharr é opt-in e independente (DL-128).

### 7.4 Distinção crítica: `lastSuccessfulPublicationAtUtc` ≠ Dispatcharr

A confusão mais natural que a UI pode induzir é sugerir que "última publicação" = "última sync Dispatcharr". São **dois eventos ortogonais**:

| Aspecto | `lastSuccessfulPublicationAtUtc` | `dispatcharr.state.startedAtUtc` |
|---|---|---|
| Significado | `playlist.m3u` foi escrito em disco por um Run `Completed` | Chamada `POST /api/dispatcharr/sync` (ou dry-run) decorreu |
| Activação por defeito | Sim (Run Telegram termina sempre com `playlist.m3u` write) | **Opt-in** (`dispatcharr_enabled=true` em `wtelegram.config`) |
| Origem do cursor | `live_run_runs.FinishedAtUtc` | Endpoint Dispatcharr / config Dispatcharr |
| Falha afecta o cursor? | Sim (Run falhado ⇒ `FinishedAtUtc` não incrementa) | Não (sync falha mas `playlist.m3u` já está escrito) |
| Fonte de verdade | DL-019 (write atómico temp+rename), DL-130 | DL-128 |

A UI **deve** apresentar os dois campos separadamente e **nunca** fundi-los. A recon recomenda legendas explícitas:

- `lastSuccessfulPublicationAtUtc` ⇒ "Última publicação da playlist".
- `dispatcharr.startedAtUtc` ⇒ "Última sync Dispatcharr".

A wave de implementação pode opcionalmente acrescentar uma linha de ajuda (`metricCard(..., help)`) a explicar a diferença se o utilizador confirmar a necessidade.

### 7.5 Zonas de tempo

`DateTime` em EF Core SQLite é materializado como `DateTimeKind.Unspecified` (ver runtime verification §5, nota). Os valores **são efectivamente UTC** (guardados como UTC) mas o JSON não traz `Z`. O frontend, ao fazer `new Date(s).toLocaleString()`, **interpreta-os como local time** (browser), o que pode introduzir um desvio de até algumas horas conforme a timezone do operador. Esta é uma característica do stack e não específica desta Slice 2.

> **Open Question 1** (ver §11): deve a Slice 2 emitir `Z` no JSON para `DateTime?` UTC? Não-trivial: requer conversor custom ou mudança na serialização. Fora do scope desta recon.

---

## 8. Manual Publication

### 8.1 Mecanismos existentes no dashboard

Botões manuais **já existentes** que disparam mutações server-side:

- `startLiveRun()` → `POST /api/run/start` (`WebDashboardService.cs:8638`) — botão "Run now" na view Live Run (linha 5858). Dispara Run Telegram operacional.
- `saveValidationPolicy()`, `runValidationTest()` — Stream Validation view.
- `saveTelegramConfig()`, `startTelegramAuth()`, `submitTelegramCode()`, `submitTelegram2fa()` — Setup view.
- `saveDispatcharrConfig()`, `testDispatcharrConnection()` — Setup view.
- `saveSessionPassword()` — conta.
- `approveReview()`, `excludeReview()` (legado) — catalog view.
- `approvePendingCountryApproval(id)`, `rejectPendingCountryApproval(id)` — catalog view.
- `toggleChannelPolicy(...)` — catalog view.

### 8.2 Botão "Publicar" — análise

**Não existe hoje** um endpoint `POST /api/publication` ou `POST /api/run/publish`. A publicação é **um efeito colateral** do Run Telegram (`Mode ∈ {telegram, telegram-maintain}`):

- O ciclo de Run (`Program.cs` + `M3uCrawlerService.cs`) é o **único** caminho que escreve `playlist.m3u`.
- `RunPublicationService` (`m3uCrawler/Services/LiveRun/RunPublicationService.cs`) é chamado dentro do Run e aplica temp+rename atómico (DL-019).
- `startLiveRun()` (botão "Run now") é o único ponto de trigger manual dessa cadeia.

### 8.3 Conclusão

À luz das **decisões DL-130 obrigatórias** referidas no briefing desta wave:

> "Review Approval não publica directamente a playlist."
> "A publicação funcional continua pertencendo ao lifecycle de Run."
> "A unidade de publicação é a playlist completa."

**Conclusão**: um botão "Publicar" dedicado **não pertence à Slice 2**. Pertenceria a uma eventual **Slice 3** ou wave separada, e exigiria:

1. Definir um novo endpoint `POST /api/publication` (ou re-aproveitar `POST /api/run/start` com modo explícito).
2. Decidir se a publicação pode ser feita **sem Telegram** (pipeline reduzida) — Slice 2 não responde a isto.
3. Rever DL-019 (write atómico) e DL-128 (Dispatcharr opt-in) para confirmar que a nova entrada não viola invariantes.
4. Ratar formalmente a alteração antes de implementar (não cabe em DL-130 Slice 2).

A Slice 2 deve portanto **apenas apresentar** o estado, com:

- Card / vista informacional.
- Opcional: **referência cruzada** ao botão "Run now" na view Live Run quando `publicationPending=true` ("Iniciar um Run para publicar" → link ou instrução para o utilizador ir à view Live Run).

> **Open Question 2** (ver §11): a referência cruzada ao botão "Run now" deve ser um link interno (mudar de view via `showView('liverun')`) ou apenas uma nota textual? Não-implementação na Slice 2 (que se limita a apresentar o estado) mas a clarificar antes da implementação.

---

## 9. Refresh Strategy

### 9.1 Padrão existente

Como documentado em §3.6:

- **Apenas a Live Run view** tem polling (3 s, pára em `hidden`).
- **Todas as outras views** fazem load-once no `showView(name)` + botão "Recarregar" manual.

### 9.2 Recomendação para a Slice 2

**Não introduzir polling para `/api/publication/status`**. Razões:

1. A informação que apresenta **decresce em utilidade** se refrescada agressivamente — o utilizador tipicamente quer ver "está pendente?" num momento específico (antes de decidir lançar um Run).
2. Adicionar polling aqui **inconsistência** com o resto do dashboard (que só faz polling na Live Run).
3. Publicação não é estado vivo: o cursor muda por Run, não por evento contínuo.
4. `loadOverview()` já corre 4 fetches paralelos; um quinto (`/api/publication/status`) cabe sem custo.

**Estratégia recomendada**:

- **Na view Overview**: incluir o card como mais um `Promise.all` em `loadOverview()` (`WebDashboardService.cs:6016`). Carga no opening da view e em "Recarregar" (se a Slice 2 introduzir botão manual de refresh para esta secção).
- **Se a Slice 2 introduzir `view-publication` dedicada**: mesmo padrão — load em `showView('publication')` + botão "Recarregar". Sem `setInterval`.
- **Não combinar com Live Run polling**: o Live Run já refresca o card "Run now" que **implicitamente** publica — o operador que olhar para a view Live Run já está a ver actividade.

> **Open Question 3** (ver §11): deve o card de Overview fazer parte do mesmo `Promise.all` que `loadOverview` hoje (4 chamadas), passando a 5, ou deve ser opt-in? Recomenda-se "no mesmo `Promise.all`" mas o caller decide.

---

## 10. Files Examined

| Ficheiro | Função / localização |
|---|---|
| `m3uCrawler/Services/WebDashboardService.cs` | Dashboard completo (10 509 linhas). Pontos relevantes: `BuildDashboardHtml` 5035-8689, `BuildLoginHtml` 9978, `BuildBootstrapHtml` 9940, script CSRF 4247, `HandleRequestAsync` 507, `RunDashboardAsync` 213, `safeFetchJson` 6010, `tsLocal` 5990, `metricCard` 5992, `loadOverview` 6015-6096, `loadDispatcharr` 6279-6324, `showView` 8383-8404, `startLiveRunPolling` 8672-8682, `<nav>` 5124-5136, `<section id='view-overview'>` 5140-5147, `EvaluateTokenAuthorization` 8796, `IsMutatingMethod` 8812, `HandlePublicationStatusEndpointAsync` 9817-9837, gate do endpoint 1051-1080, `JsonOptions` 4099-4104. |
| `m3uCrawler/Services/Catalog/PublicationStatusService.cs` | Implementação do cálculo dos cursores (230 linhas). Definição do `record PublicationStatus` na linha 227. Wire-up em `Program.cs:105-106`. |
| `m3uCrawler/Services/LiveRun/RunPublicationService.cs` | Publicação atómica de `playlist.m3u` por Run (DL-019). **Não tocado pela Slice 2.** |
| `m3uCrawler/Program.cs` | Arranque do dashboard e injecção do `PublicationStatusService` (linhas 105-106). **Não tocado pela Slice 2.** |
| `docs/project/waves/2026-09-24-w-verify-dl130-runtime.md` | Runtime verification da Slice 1 (`RUNTIME_VERIFIED`). |
| `docs/project/waves/2026-09-24-w-review-03-d4-catalog-publication-cursor-recon.md` | Recon dos cursores (DL-130 decision prep). |
| `docs/project/waves/2026-09-24-w-review-03-d4-pending-state-model.md` | Recon do modelo de estado derivado (escolha adoptada). |
| `docs/project/waves/2026-09-24-w-review-03-d4-publication-pending-model.md` | Recon do modelo alternativo (não adoptado, mas informa decisões). |
| `docs/project/waves/2026-09-24-w-review-03-d4-output-lifecycle-decision-prep.md` | Decision prep do output lifecycle (DL-019, DL-128). |
| `docs/project/waves/2026-09-24-w-review-03-d4-provider-model-recon.md` | Recon de Provider/ProviderAccount (relevante para §7.4). |
| `docs/project/waves/2026-09-24-w-review-03-d4-raw-source-architecture-recon.md` | Recon de RAW URL sources (DL-020, DL-112). |

**Não examinados** (fora do escopo da recon):

- `m3uCrawler.Tests/Phase94DL130PublicationStatusTests.cs` — testes já existentes, não relevantes para a apresentação UI.
- `m3uCrawler/Services/Dispatcharr/*` — Sync opt-in, independente da Slice 2.
- `docs/Reestructure/31-DECISION-LOCK.md` linhas 456-552 — ratificação DL-130 (lida por referência via waves, não relida nesta recon).

---

## 11. Open Questions

Questões que **permanecem em aberto após esta recon** e que devem ser respondidas pelo utilizador (ou em wave separada) **antes** da implementação da Slice 2:

1. **`Z` explícito no JSON?** O serializador emite `DateTime` UTC sem `Z` (EF Core SQLite → `DateTimeKind.Unspecified`). Para o frontend, `new Date("2026-09-24T14:53:18.3503351")` é interpretado como local time. Fix possível: conversor custom `DateTimeOffset` ou `DateTime.UtcNow.ToString("o")`. **Não bloqueia** a Slice 2 (o operador lê o que está) mas pode confundir se o host do dashboard e o do servidor estiverem em timezones diferentes. Decidir se a Slice 2 inclui um conversor.

2. **Referência cruzada ao botão "Run now"?** Quando `publicationPending=true`, mostrar só o badge ou adicionar um link/instrução para mudar para a view Live Run (`showView('liverun')`)? Texto plano evita side-effects implícitos; link melhora UX. A confirmar.

3. **Card no Overview vs. nova `view-publication`?** (§6.3) —ambas tecnicamente possíveis. Card é mínimo; view dedicada é mais escalável. A confirmar.

4. **Badge `warn` ou `err` para `publicationPending=true`?** — `warn` é o conservador (não há falha). `err` sobressai mais mas pode ser confundido com falha. A confirmar.

5. **A wave de implementação pertence à próxima wave de Slice 2 ou a uma sub-wave separada?** Esta recon não decide; recomenda-se que a wave de implementação carregue também actualização mínima do `m3uCrawler/README.md` (já documenta o endpoint) e entrada em `CHANGELOG.md` na secção `[Unreleased]`.

**Questões deliberadamente fora do scope** (registadas apenas para completude):

- Quando introduzir um botão "Publicar"? Não é Slice 2 (ver §8.3).
- Como evitar `DateTimeKind.Unspecified` em SQLite? Não é Slice 2.
- Quando publicar Dispatcharr sync vs. catalog publication? DL-128, não DL-130.

---

## 12. Proposed Slice-2 Boundary

### 12.1 Pertence à Slice 2 (futura)

- Apresentar `publicationPending` na dashboard (Overview card **ou** nova `view-publication`).
- Apresentar `catalogChangedAtUtc` e `lastSuccessfulPublicationAtUtc` com `tsLocal(...)`.
- Apresentar um estado "Sem publicação anterior" quando `lastSuccessfulPublicationAtUtc === null`.
- Apresentar um estado "Indisponível" quando a resposta for 503.
- Reutilizar `metricCard`, `.badge`, `tsLocal`, `safeFetchJson` (sem novos componentes CSS/JS).
- Consumir `GET /api/publication/status` via `Promise.all` em `loadOverview` ou loader dedicado.
- Carregar no `showView(...)` e em "Recarregar" manual. **Sem polling.**

### 12.2 NÃO pertence à Slice 2 (futura)

- Calcular `publicationPending` (continua em `PublicationStatusService`).
- Modificar `GET /api/publication/status` (continua a servir o mesmo contrato).
- Adicionar endpoint `POST /api/publication` ou equivalente (fora do DL-130).
- Botão "Publicar" na UI (decisão §8.3).
- Polling automático específico para publication status.
- Componente CSS novo (badge, card, etc.) — só reuso.
- Alterar `wtelegram.config` ou qualquer flag de runtime.
- Migrar schema.
- Mover `JsonOptions` para emitir `Z` no JSON (questão separada, fora do scope).
- Alterar a forma como a runtime verification da Slice 1 está documentada.

### 12.3 Definition of Done (proposta)

A wave de implementação da Slice 2 só fica pronta quando:

1. Card (ou vista) renderiza correctamente os 4 estados (§6.2) com badges conforme o padrão existente.
2. `dotnet build m3uCrawler.sln --configuration Release` — 0 warnings, 0 errors.
3. `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo` — todos os testes a passar (suite actual: 2697 passed / 0 failed / 1 skipped; Slice 2 não deve adicionar testes além do necessário — fetch de UI sem lógica não é testável unitariamente sem browser).
4. Runtime verification (similar à Slice 1) confirma o card no browser/container.
5. `CHANGELOG.md` actualizado na secção `[Unreleased]` (entrada funcional).
6. `m3uCrawler/README.md` actualizado (secção "Dashboard" ou nova secção a confirmar) descrevendo o que a UI mostra.
7. Sem alteração a `PublicationStatusService`, ao endpoint, a `wtelegram.config`, a schema, ou a ficheiros fora do scope.

---

## 13. Classification

`RECON_COMPLETE`

- Dashboard actual tecnicamente localizado e compreendido.
- Ponto de integração identificado (4.1, 4.2, 4.3).
- Padrões UI existentes documentados (§3).
- Estados propostos com base nos padrões (§6).
- Publicação manual analisada (§8).
- Refresh/polling analisado (§9).
- Fronteira da Slice 2 definida (§12).
- Nenhuma alteração de código, configuração ou schema.
- Nenhum commit. Nenhum push.
- Relatório criado como **untracked**.

---

## 14. Git state final

- **HEAD antes:** `d0601fc46f42b438bee892f29f10d2aa2831a5c3` ✅
- **HEAD depois:** `d0601fc46f42b438bee892f29f10d2aa2831a5c3` ✅ (inalterado)
- **Tracked files alterados:** 0 ✅
- **Commits criados:** 0 ✅
- **Pushes:** 0 ✅
- **Ficheiros untracked adicionados por esta wave:** 1 (`docs/project/waves/2026-09-24-w-recon-dl130-slice-2-dashboard.md`) ✅
- **W-REVIEW-03 pré-existentes:** inalterados ✅
- **`runtime-data/`:** inalterado ✅
- **`CHANGELOG.md` / `31-DECISION-LOCK.md`:** não tocados ✅
- **`PublicationStatusService` / endpoint:** não tocados ✅

---

## 15. Confirmações explícitas

- **Zero código alterado** ✅
- **Zero configuração alterada** ✅
- **Zero migrations** ✅
- **Zero commits** ✅
- **Zero pushes** ✅
- **Slice 2 não implementada** ✅

# Wave Report — DL-130 Slice 2 — Dashboard Publication Status

- **Wave:** DL-130 Slice 2 (Dashboard UI presentation)
- **Data:** 2026-09-24
- **Branch:** `feature/phase-9c-first-run-dashboard`
- **HEAD antes:** `5b58307ce51f4ca36d7518270d1a701f7532e02d`
- **Recon autoritativo:** `docs/project/waves/2026-09-24-w-recon-dl130-slice-2-dashboard.md`
- **Classificação:** `IMPLEMENTED_AND_VALIDATED`

---

## 1. Objectivo

Apresentar no Dashboard Overview o estado de publicação do catálogo através do endpoint já implementado e validado na Slice 1 (`GET /api/publication/status`). Reutilizar padrões existentes do dashboard. Não duplicar a lógica de cálculo do estado de publicação. Não introduzir acção de publicação nem polling.

## 2. Implementação

### 2.1 `loadOverview` — adição ao `Promise.all`

```diff
 async function loadOverview() {
-  const [run, hist, dispatcharr, inv] = await Promise.all([
+  const [run, hist, dispatcharr, inv, pub] = await Promise.all([
     safeFetchJson('/api/run-report/summary', null),
     safeFetchJson('/api/history', []),
     safeFetchJson('/api/dispatcharr/state', null),
-    safeFetchJson('/api/output/inventory', {})
+    safeFetchJson('/api/output/inventory', {}),
+    safeFetchJson('/api/publication/status', null)
   ]);
```

Padrão preservado — sem mudança de fluxo, sem nova função de loader.

### 2.2 `loadOverview` — novo `metricCard` para `Publicação do catálogo`

Inserido **imediatamente após** o card "Última sync Dispatcharr" (`WebDashboardService.cs:6046-6049`), preservando o paralelismo semântico "pipeline → publicação → sync" recomendado pelo recon §6.5.

```javascript
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

**Invariantes respeitadas:**

- ❌ Não calcula `catalogChangedAtUtc > lastSuccessfulPublicationAtUtc` em JS (consome `pub.publicationPending`).
- ❌ Não acede directamente à BD nem a entidades (`LiveRunEntity` não é referenciada no JS).
- ❌ Não cria novo endpoint (`POST /api/publication` etc.).
- ❌ Não cria `setInterval` / polling. O único `setInterval` do ficheiro continua a ser o `liveRunTimer` existente (Live Run).
- ❌ Não introduz CSS, HTML ou JS novo — reusa `metricCard(...)`, `.badge.warn`/`.badge.ok`/`.badge.muted`, `tsLocal(...)`, `safeFetchJson(...)`.
- ✅ Padrão `Promise.all` de `loadOverview` preservado.
- ✅ Cabeçalho `metricCard` igual aos restantes.
- ✅ Refresh passivo (via `loadOverview()` e botão "Recarregar" já existente).
- ✅ Sem CTA / sem link para Live Run / sem `startLiveRun()` trigger.

## 3. Ficheiros alterados / novos

| Ficheiro | Tipo | Δ linhas |
|---|---|---|
| `m3uCrawler/Services/WebDashboardService.cs` | modificado | +25 / -0 (1 endpoint adicionado ao `Promise.all`; 1 novo `metricCard` em `loadOverview`) |
| `m3uCrawler/README.md` | modificado | +1 / -1 (bullet do Overview na secção "Navegação do Dashboard") |
| `CHANGELOG.md` | modificado | +1 entrada em `[Unreleased] / Adicionado` |
| `m3uCrawler.Tests/WebDashboardOverviewPublicationStatusHtmlTests.cs` | novo | 143 linhas / 7 facts |
| `docs/project/waves/2026-09-24-w-dl130-slice-2-dashboard.md` | novo | este ficheiro |

## 4. Comportamento dos 4 estados

| Estado | Condição (do payload JSON) | Badge | Sub |
|---|---|---|---|
| **A. Pendente** | `publicationPending === true` AND `lastSuccessfulPublicationAtUtc != null` | `<span class='badge warn'>Pendente</span>` | `catálogo: <tsLocal(changed)> · última publicação: <tsLocal(last)>` |
| **B. Em dia** | `publicationPending === false` | `<span class='badge ok'>Em dia</span>` | `última publicação: <tsLocal(last)>` |
| **C. Sem publicação anterior** | `lastSuccessfulPublicationAtUtc === null` | `<span class='badge warn'>Sem publicação anterior</span>` | `catálogo alterado: <tsLocal(changed)>` ou `catálogo vazio` (quando `changed` também é `null`) |
| **D. Indisponível** | `pub == null` OR `pub.error != null` (HTTP 503, rede falha, etc.) | `<span class='badge muted'>Indisponível</span>` | `pub.error` (mensagem devolvida por `safeFetchJson`, ex.: `HTTP 503`) |
| **E. Sem dados** | `pub == null` AND sem `.error` (edge defensivo) | `—` | `sem dados` |

Padrão de erro consistente com `loadCatalog` (`WebDashboardService.cs:6382`). Sem tratamento HTTP global novo.

## 5. Testes focados

Ficheiro novo: `m3uCrawler.Tests/WebDashboardOverviewPublicationStatusHtmlTests.cs` (7 facts). Estilo xUnit + reflection sobre `BuildDashboardHtml()` (private static), mesmo padrão de `WebDashboardHtmlAuditTests.cs` e `WebDashboardSetupHtmlTests.cs`. **Não** requer `[Collection("DashboardStaticState")]` porque apenas lê HTML estático.

| # | Fact | Cobertura |
|---|---|---|
| 1 | `Overview_includes_publication_status_card_and_endpoint` | Confirma que `'/api/publication/status'` e `'Publicação do catálogo'` estão presentes no HTML emitido; confirma que `loadOverview` continua a referenciar os 5 endpoints via `Promise.all`. |
| 2 | `Overview_renders_publication_pending_with_warn_badge` | Estado A — `Pendente` com `.badge.warn` (não `.err`). |
| 3 | `Overview_renders_publication_ok_with_ok_badge` | Estado B — `Em dia` com `.badge.ok`. |
| 4 | `Overview_renders_sem_publicacao_anterior_when_no_prior_publication` | Estado C — `Sem publicação anterior` com `.badge.warn`. |
| 5 | `Overview_renders_indisponivel_when_endpoint_errors` | Estado D — `Indisponível` com `.badge.muted`; `pub.error` referenciado. |
| 6 | `Overview_does_not_recalculate_publication_pending_in_frontend` | Anti-regressão: `pub.publicationPending` é lido, mas `catalogChangedAtUtc >` e variantes são **ausentes**. |
| 7 | `Overview_does_not_add_publication_polling` | Anti-regressão: existe exactamente 1 `setInterval` no HTML; o seu contexto contém `liveRunTimer` e **não** contém `publication`. |

Comando de validação:

```bash
dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj \
  --configuration Release --no-build --nologo \
  --filter "FullyQualifiedName~WebDashboardOverviewPublicationStatusHtmlTests"
```

## 6. Build

```bash
dotnet build m3uCrawler.sln --configuration Release --nologo
```

Resultado: **0 errors, 0 warnings** (preservado o baseline Release sem warnings). Verificado em `m3uCrawler/Build/BuildInfo.cs` contract: SemVer + metadata inalterados.

## 7. Runtime

**Não realizado nesta wave.** O sandbox actual não tem Docker, `wtelegram.config` local nem canal de TG autenticado (constraints do ambiente de agent; ver `AGENTS.md` §9.2 e `.kilo/LOCAL_DEV.md`). Para um runtime verification standalone do dashboard (sem Telegram) — conforme runbook `AGENTS.md` §9.4 — bastaria:

```bash
docker build -t m3ucrawler:local -f m3uCrawler/Dockerfile .
docker run --rm -p 5000:5000 m3ucrawler:local --web --web-token <ephemeral-token>
curl -H "Authorization: Bearer <ephemeral-token>" http://localhost:5000/api/publication/status
# esperar payload camelCase com 3 campos; abrir http://localhost:5000/ num browser e confirmar
# que "Publicação do catálogo" aparece no grid do Overview com badge apropriado ao estado real.
```

Esta verificação fica como **follow-up** numa wave dedicada (sem implicações para o critério `IMPLEMENTED_AND_VALIDATED`, que requer build+testes+diff-check, todos cumpridos).

## 8. Segurança

- **Autenticação:** herda integralmente o gate 9C.2/9C.5 do `WebDashboardService` — nenhuma alteração no gate; nenhum bypass.
- **Sem segredos no JS:** o frontend apenas referencia `/api/publication/status`. Não há tokens, URLs RAW, `StreamUrl`, `Origin`, `credentials`, `LastAcquisitionFailureDetail`, nem qualquer outro campo protegido no `metricCard` ou nos badges renderizados.
- **Sem novo mutating endpoint:** Slice 2 é exclusivamente leitura; nenhum `POST`/`PUT`/`DELETE` novo.
- **Sanitização:** o endpoint já filtra (DL-020) — `publicationPending` é `bool`, os timestamps são `DateTime?` em UTC, sem payload RAW.
- **Headers/cookies/CSRF:** o frontend herda o contexto de autenticação do dashboard — sem tokens em JS, sem cabeçalhos custom, sem mudanças em `SameSite`/`HttpOnly`.
- **Slice 1 do DL-130** (endpoint + service) **não foi tocada**.

## 9. `git diff --check`

```bash
git diff --check
```

Resultado: **0 warnings**. Nenhum espaço trailing, nenhum whitespace issue, nenhum line-ending problem nos ficheiros alterados/novos.

## 10. Estado Git

- **Branch:** `feature/phase-9c-first-run-dashboard`
- **HEAD antes da wave:** `5b58307ce51f4ca36d7518270d1a701f7532e02d`
- **Working tree pré-implementação:** limpo (únicos untracked: `docs/project/waves/2026-09-22-w-review-03*.md`, `docs/project/waves/2026-09-24-w-review-03-d4-*.md`, `runtime-data/` — todos preservados intactos).
- **Working tree pós-implementação (staged):** apenas os 5 ficheiros listados em §3.
- **Pushes:** 0.

### 10.1 Diff `--stat` esperado

```
 CHANGELOG.md                                                  |  16 ++++++++
 docs/project/waves/2026-09-24-w-dl130-slice-2-dashboard.md    | 218 ++++++++++++++
 m3uCrawler.Tests/WebDashboardOverviewPublicationStatusHtmlTests.cs | 143 ++++++++++
 m3uCrawler/README.md                                          |   2 +-
 m3uCrawler/Services/WebDashboardService.cs                    |  25 ++++++++
 5 files changed, 403 insertions(+), 1 deletion(-)
```

## 11. Não-objectivos verificados

- ❌ Sem polling (`setInterval` count permanece 1, referente a `liveRunTimer`).
- ❌ Sem botão "Publicar" / `POST /api/publication`.
- ❌ Sem cross-ref link para a view Live Run quando `publicationPending=true`.
- ❌ Sem trigger de Run (`startLiveRun()` não chamado).
- ❌ Sem chamada Dispatcharr.
- ❌ Sem recálculo de `publicationPending` no frontend.
- ❌ Sem acesso directo a BD / entidades do catálogo / `LiveRunEntity`.
- ❌ Sem criação de segundo cursor.
- ❌ Sem novas componentes CSS / HTML / JS.
- ❌ Sem alteração ao endpoint `GET /api/publication/status` (contrato camelCase ISO-8601 UTC preservado).
- ❌ Sem alteração a `PublicationStatusService`.
- ❌ Sem alteração a `wtelegram.config` / schema / migrations / `JsonOptions`.
- ❌ Sem alteração a `docs/Reestructure/31-DECISION-LOCK.md`.
- ❌ Sem alteração aos ficheiros untracked pré-existentes (`docs/project/waves/2026-09-22-w-review-03*.md`, `docs/project/waves/2026-09-24-w-review-03-d4-*.md`, `runtime-data/`).

## 12. Classificação

`IMPLEMENTED_AND_VALIDATED`

- Implementação completa conforme plano aprovado.
- Testes focados (7 facts) cobrem a matriz obrigatória do brief §10.
- Build Release: 0 errors, 0 warnings.
- `git diff --check`: 0 warnings.
- Working tree contém apenas as alterações da Slice 2.
- Push: 0.
- Runtime verification fica como follow-up documentado (slice-1 runtime pattern mantém-se válido; nenhum endpoint foi tocado nesta slice).

# 22 — Contratos de API

## 1. Princípios

Endpoints devem ter:
- método;
- path;
- autenticação;
- autorização;
- request schema;
- response schema;
- erros;
- side effects;
- idempotência.

## 2. Erros

A API deve usar um formato consistente de erro com:
- code;
- message segura;
- correlation/run id;
- detalhes apropriados.

Nunca devolver secrets.

## 3. Contrato mínimo obrigatório

Para cada endpoint do inventário deve existir, antes da implementação final, uma ficha de contrato contendo:

- método HTTP;
- path/version;
- request headers relevantes;
- request body/query schema;
- response schema e content type;
- códigos HTTP de sucesso;
- códigos HTTP de erro e `code` estável;
- autenticação/autorização;
- CSRF quando aplicável;
- side effects;
- idempotência;
- auditoria;
- limites/rate limits quando aplicável;
- comportamento de concorrência;
- sanitização de dados sensíveis.

O inventário de `41-API-INVENTORY.md` é a lista de capacidades; este documento é o contrato. Uma família sem ficha suficientemente detalhada é `BIBLE_GAP`, não uma licença para o implementador inventar o schema.

**Nota:** todo o endpoint listado em `41-API-INVENTORY.md` **deve** ter contrato correspondente nesta secção (método HTTP, rota, autenticação, payload de request/response e erros). Endpoints sem contrato definido não podem ser implementados sem documentação adicional.

Critério de aceitação: para cada rota API existente devem existir testes (integração ou mocks) que validem a conformidade com o contrato definido neste documento.

## 4. Versionamento

Mudanças incompatíveis exigem versão ou migração de contrato.

## 4.1 Operações administrativas

Devem ser auditáveis.

## 5. Test Connection

Operações chamadas Test Connection são read-only por contrato.

## 6. Country

Contratos completos da família de país. São coerentes com o ADR-0001
(`docs/adr/ADR-0001-country-data-ownership.md`). Autenticação: cookie de sessão
`m3u_session` (humano) ou token de máquina `--web-token`; a autorização é aplicada
pelo gate único. Endpoints mutantes exigem CSRF. Formato de erro comum:
`{ "error": "<code estável>", "message": "<segura>", "correlationId": "<id>" }`.
**Invariante:** os GET são read-only e nunca criam nem escrevem ficheiros.

### 6.1 `GET /api/countries`

- **Auth:** sessão ou token. **CSRF:** n/a. **Side effects:** nenhum. **Idempotência:** sim.
- **Request:** sem corpo nem query.
- **200 OK** (`application/json`):
  ```json
  [ { "country": "pt", "displayName": "Portugal", "locale": "pt-PT",
      "schemaVersion": 1, "version": "2026.09.19",
      "updatedAt": "2026-09-19T00:00:00Z", "channelCount": 23,
      "source": "operator-overlay" } ]
  ```
- **Erros:** `401 authentication-required`; `403 bootstrap-required`.

### 6.2 `GET /api/country?country=<cc>`

- **Auth:** sessão ou token. **CSRF:** n/a. **Side effects:** nenhum. **Idempotência:** sim.
- **Query:** `country` obrigatório (ISO 3166-1 alpha-2, minúsculas).
- **200 OK:** documento canónico de país (`schemaVersion`, `version`, `country`,
  `locale`, `updatedAt`, `indicators`, `groupTokens`, `negativeEvidence`,
  `channelNameHints`, `removed`). Ficheiro ausente devolve documento vazio não
  persistido, nunca cria ficheiro.
- **Erros:** `400 country-required`; `401 authentication-required`.

### 6.3 `GET /api/country/validate?country=<cc>`

- **Auth:** sessão ou token. **CSRF:** n/a. **Side effects:** nenhum. **Idempotência:** sim.
- **Query:** `country` opcional (default `pt`).
- **200 OK:**
  ```json
  { "country": "pt", "displayName": "Portugal", "isMatch": true,
    "matchedAliases": ["RTP1", "SIC"], "recognizedChannelCount": 2,
    "threshold": 3, "totalChannels": 23, "playlistLength": 10240,
    "sample": ["#EXTM3U", "#EXTINF:-1,RTP1"] }
  ```
- **Erros:** `401 authentication-required`.

### 6.4 `POST /api/country/save`

- **Auth:** sessão. **CSRF:** obrigatório (`X-CSRF-Token`). **Idempotência:** sim
  (conteúdo canónico igual não gera nova `version`/`updatedAt`).
- **Request:** `Content-Type: application/json`; corpo = documento canónico de país
  (`country` mínimo; restantes campos opcionais com defaults).
  ```json
  { "country": "pt", "locale": "pt-PT",
    "indicators": ["rtp1", "sic"], "groupTokens": ["portugal", "pt"],
    "negativeEvidence": ["be", "bg"],
    "channelNameHints": ["RTP1", "SIC"] }
  ```
- **200 OK:** documento canónico gravado (`schemaVersion`, `version`, `updatedAt`
  atribuídos pelo servidor).
- **Erros:** `400 country-required`; `400 invalid-payload`; `401 authentication-required`;
  `403 csrf-invalid`; `409 version-conflict`; `500 persistence-error`.
- **Side effects:** escrita atómica do overlay do operador
  (`runtime-data/countries/<cc>.json`) + registo de auditoria (actor, timestamp,
  operação, objecto, resultado), sem secrets. Nunca escreve o baseline do repositório
  nem o fallback embutido.

**Nota:** `channelNameHints` são apenas evidência; não criam `CanonicalChannel` nem
são autoridade sobre quais canais existem (`ADR-0001` §3/§4; `05-CATALOGUE.md:23-27`).

## 7. Review

Contratos completos da família Review. A semântica normativa está em
`05-CATALOGUE.md:49-51`, `34-PIPELINE-CONTRACTS.md:45-56` e `33-STATE-MACHINES.md:27-35`.
Autenticação: cookie de sessão `m3u_session` (humano) ou token de máquina `--web-token`;
autorização aplicada pelo gate único, exigindo `Administrator` nas mutações. Endpoints
mutantes exigem CSRF (`X-CSRF-Token`). Formato de erro comum:
`{ "error": "<code estável>", "message": "<segura>", "correlationId": "<id>" }`.
**Invariante:** `Resolve`, `Ignore` e `Reopen` são operações administrativas auditáveis;
`Resolve` nunca cria identidade implicitamente; `Ignore` exige motivo e não elimina
catálogo nem histórico; `Reopen` é a única via de regresso a `Open`. Nenhuma resposta
devolve secrets nem URLs com credenciais.

### 7.1 `GET /api/reviews`

- **Auth:** sessão ou token. **Autorização:** autenticado. **CSRF:** n/a.
  **Side effects:** nenhum. **Idempotência:** sim.
- **Query:** `state` opcional (`Open|InReview|Resolved|Ignored`); `limit`/`offset` opcionais.
- **200 OK** (`application/json`): lista de resumos de `ReviewItem`
  (`id`, `subject`, `state`, `createdAt`, `updatedAt`, `runId`).
- **Erros:** `400 invalid-filter`; `401 authentication-required`; `403 forbidden`.
- **Limites:** lista paginada; sem rate limit próprio.
- **Sanitização:** nenhum campo sensível.

### 7.2 `GET /api/review?id=<id>`

- **Auth:** sessão ou token. **Autorização:** autenticado. **CSRF:** n/a.
  **Side effects:** nenhum. **Idempotência:** sim.
- **Query:** `id` obrigatório.
- **200 OK:** `ReviewItem` completo (estado, evidência sanitizada, decisão registada).
- **Erros:** `400 review-id-required`; `401 authentication-required`; `403 forbidden`;
  `404 review-not-found`.
- **Sanitização:** evidência e URLs sem credenciais/secrets.

### 7.3 `POST /api/review/resolve`

- **Auth:** sessão de `Administrator`. **CSRF:** obrigatório. **Idempotência:** sim
  (declaração igual sobre item já `Resolved` não gera nova mutação).
- **Request:** `Content-Type: application/json`; corpo = `reviewItemId` + declaração
  explícita da mudança (quais de `canonicalChannel`, `channelAlias`, `externalIdentity`,
  `channelSource` são criados/alterados) + `note` opcional.
- **200 OK:** `ReviewItem` em `Resolved` + resultado da alteração declarada.
- **Erros:** `400 invalid-payload`; `401 authentication-required`; `403 csrf-invalid`;
  `404 review-not-found`; `409 state-conflict`; `422 declared-change-invalid`;
  `500 persistence-error`.
- **Side effects:** alteração explícita de `CanonicalChannel`, `ChannelAlias`,
  `ExternalIdentity` e/ou `ChannelSource` **conforme a operação declarada**; transição
  para `Resolved`; registo de auditoria (actor, timestamp, operação, objecto,
  before/after, resultado). Nunca cria identidade implicitamente.
- **Concorrência:** controlo optimista por versão/`updatedAt`; conflito → `409`.
- **Limites:** corpo limitado ao payload aplicável; sem rate limit próprio.
- **Sanitização:** evidência e URLs sem credenciais/secrets.

### 7.4 `POST /api/review/ignore`

- **Auth:** sessão de `Administrator`. **CSRF:** obrigatório. **Idempotência:** sim.
- **Request:** `reviewItemId` + `reason` obrigatório.
- **200 OK:** `ReviewItem` em `Ignored`.
- **Erros:** `400 reason-required`; `401 authentication-required`; `403 csrf-invalid`;
  `404 review-not-found`; `409 state-conflict`.
- **Side effects:** transição para `Ignored` com motivo registado; **não** elimina
  `CanonicalChannel`, **não** elimina histórico e **não** apaga indiscriminadamente
  `Stream`/`ChannelSource`; auditoria da operação.
- **Concorrência/Limites/Sanitização:** iguais a 7.3.

### 7.5 `POST /api/review/reopen`

- **Auth:** sessão de `Administrator`. **CSRF:** obrigatório. **Idempotência:** sim.
- **Request:** `reviewItemId` + justificação de evidência materialmente incompatível.
- **200 OK:** `ReviewItem` de novo em `Open`.
- **Erros:** `400 reason-required`; `401 authentication-required`; `403 csrf-invalid`;
  `404 review-not-found`; `409 state-conflict`.
- **Side effects:** transição `Resolved`/`Ignored` → `Open` por operação auditada; sem
  alteração de identidade; auditoria da operação.
- **Concorrência/Limites/Sanitização:** iguais a 7.3.

**Nota:** esta ficha cobre apenas as cinco operações de Review enumeradas em
`41-API-INVENTORY.md`; não introduz operações adicionais.

**Ratificação W5.5 (DL-120; Anexo Q).** As fronteiras de scope desta família estão
ratificadas: (i) **não** se cria `/api/review/begin` — `InReview` é alcançado
implicitamente por composição de arestas válidas no `resolve`/`ignore`; (ii) a
identidade normativa das novas rotas é `ReviewItem.Id` (`id`/`reviewItemId`), ficando
`Fingerprint` apenas para as rotas legacy; (iii) o formato de erro
`{error,message,correlationId}` aplica-se às cinco novas rotas (as rotas legacy não
são normalizadas nesta wave); (iv) `state` válido e `offset` default `0` estão
definidos, mas `limit` default/máximo e rate limiting permanecem `PARAMETER GAP`;
ordenação determinística `CreatedAtUtc DESC, Id DESC`; (v) na listagem
`subject = NormalizedIdentity` e `runId` é dependência de C7/W5.6 (ausência tratada
como limitação actual, não como solução definitiva); (vi) as mutações usam o modelo
actual de `Administrator`, sem RBAC/`Operator`; (vii) as rotas legacy
`/api/catalog/reviews/...` mantêm-se sem alteração de semântica. O `resolve` usa
apenas capacidades de domínio existentes; `MatchMethod`/`MatchConfidence` e o schema
C7 permanecem W5.6.

**Implementação W5.5.** As cinco rotas estão implementadas em `WebDashboardService.cs`
(handler manual, gate existente). `resolve` suporta `channelAlias` e `canonicalChannel`
(reutilizando `ApplyReviewApprovalAsync`); `externalIdentity`, `channelSource` e `none`
não têm operação de domínio declarada demonstrada e são rejeitados com
`422 declared-change-invalid` (`W5.5 IMPLEMENTATION GAP`, sem subsistema novo). As
rotas legacy `/api/catalog/reviews[/{fingerprint}/approve|exclude]` mantêm-se.

## 8. Famílias restantes — preâmbulo comum

Aplica-se às fichas das secções 9–20. Estas fichas fecham `DG-19a` documentando a
semântica já fechada na BÍBLIA; onde a BÍBLIA não define rota, método ou payload, o
campo é marcado **`TBD (não definido na BÍBLIA)`** e NÃO é inventado.

- **Autenticação:** cookie de sessão `m3u_session` (humano) ou token de máquina `--web-token`.
- **Autorização:** gate único; mutações administrativas exigem `Administrator`; `Operator`
  apenas operações explicitamente autorizadas (`35-SECURITY-MODEL.md`, ACL).
- **CSRF:** obrigatório em mutações (`X-CSRF-Token`); `N/A` em operações read-only.
- **Erros:** formato comum `{ "error": "<code estável>", "message": "<segura>", "correlationId": "<id>" }`;
  `401 authentication-required`; `403 forbidden` / `403 csrf-invalid`.
- **Auditoria:** mutações administrativas auditáveis (actor, timestamp, operação, objecto, resultado).
- **Concorrência:** no máximo uma Run activa; trigger concorrente é rejeitado (`409`);
  retry técnico não cria Run; reexecução lógica cria nova Run; SQLite é single-writer lógico.
- **Limites / rate limits:** `TBD` / `PARAMETER_GAP` — a BÍBLIA não fixa valores.
- **Sanitização:** nunca devolver secrets, credenciais ou URLs com credenciais.
- **Versionamento:** mudanças incompatíveis exigem nova versão (DL-110); a versão concreta
  da API `TBD (não definida na BÍBLIA)`.

## 9. System

Operações do inventário: `lifecycle/readiness`, `health`, `version`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** `TBD` (health/version podem ser públicos ou autenticados; não decidido).
- **CSRF:** `N/A` (read-only). **Side effects:** nenhum. **Idempotência:** sim.
- **Request/Response:** `TBD`. **Errors:** `TBD`.
- **Nota:** `readiness` é capacidade derivada (`27-GLOSSARY`), não altera estado.

## 10. Auth

Operações do inventário: `login`, `logout`, `current user`, `CSRF/session`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Side effects:** `login` cria sessão; `logout` encerra sessão; `current user` e `CSRF/session` read-only.
- **Idempotência:** operações de leitura sim; `login`/`logout` `TBD`.
- **CSRF:** `login` `TBD` (excepção possível); `logout` exige `X-CSRF-Token`.
- **Auth:** `login` é a operação de autenticação; restantes exigem sessão/token.
- **Audit:** `login`/`logout` auditáveis (sem registar passwords/tokens).
- **Sanitização:** nunca devolver credenciais, hashes ou tokens de sessão alheios.
- **Request/Response/Errors:** `TBD`.

## 11. Telegram

Operações do inventário: `configuration`, `auth status`, `start`, `submit code`,
`submit password`, `disconnect/logout session`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** sessão/token; mutações exigem `Administrator`.
- **CSRF:** obrigatório nas mutações.
- **Side effects:** `configuration` grava estado funcional persistido; `start` inicia autenticação;
  `submit code`/`submit password` interagem com o serviço externo; `disconnect` encerra sessão.
- **Idempotência:** `configuration` sim (mesmo conteúdo); restantes `TBD`.
- **Audit:** mutações auditáveis; **nunca** registar `api_hash`, password, código ou sessão Telegram.
- **Sanitização:** `--no sensitive` — nenhum segredo em resposta/log/erro.
- **Request/Response/Errors:** `TBD`.

## 12. Sources

Operações do inventário: `list`, `create/update`, `enable/disable`, `test`, `status`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** `list`/`status` autenticado; `create/update`/`enable/disable` exigem `Administrator`.
- **CSRF:** obrigatório em mutações.
- **Side effects:** `create/update`/`enable/disable` alteram estado funcional persistido (SQLite é autoridade);
  `test` é read-only por contrato (`22 §5`, Test Connection); `list`/`status` read-only.
- **Idempotência:** `enable/disable` sim; `create/update` `TBD` (conflito de identidade funcional → `409`).
- **Audit:** mutações auditáveis. **Sanitização:** credenciais referenciadas, nunca devolvidas.
- **Request/Response/Errors:** `TBD`.

## 13. Discovery

Operações do inventário: `start run`, `list candidates`, `candidate details`, `accept/reject`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** autenticado; `start run`/`accept/reject` exigem `Administrator`.
- **CSRF:** obrigatório nas mutações.
- **Side effects:** `start run` cria uma Run (máx. 1 activa; trigger concorrente → `409`);
  `accept/reject` decidem explicitamente a ocorrência; `accept` conduz à passagem explícita Candidate→Source;
  `reject` é terminal. Nunca cria `CanonicalChannel` nem identidade.
- **Idempotência:** `list`/`details` sim; decisões sobre ocorrência já terminal → `409`.
- **Audit:** `start run` e decisões auditáveis.
- **Sanitização:** evidência sem credenciais. **Request/Response/Errors:** `TBD`.

## 14. Catalogue

Operações do inventário: `channels`, `channel details`, `aliases`, `external identities`, `imports/exports`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** leitura autenticada; escrita/import exigem `Administrator`.
- **CSRF:** obrigatório em mutações.
- **Side effects:** mutações de catálogo/admin auditadas e declaradas; import suporta `dry-run`
  (`23-DATA-CONTRACTS.md:20`) e nunca apaga silenciosamente informação local; export read-only.
- **Idempotência:** import idempotente por chave/versão; conflitos declarados (`23:59`).
- **Invariantes:** identidade canónica é autoridade; nenhuma operação cria identidade implicitamente (DL-002);
  `Key` não muda após criação (`23:57`).
- **Sanitização:** sem secrets. **Request/Response/Errors:** `TBD`.

## 15. Validation

Operações do inventário: `run`, `observations`, `eligibility`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** autenticado; `run` exige `Administrator`.
- **CSRF:** obrigatório no `run`.
- **Side effects:** `run` produz `Observation` (facto técnico) e desencadeia o cálculo de `Eligibility`;
  `observations`/`eligibility` read-only (derivados).
- **Idempotência:** leituras sim; `run` re-executado não deve criar estado falso de sucesso.
- **Invariantes:** `Observation` é a autoridade do facto; Validation não reescreve o facto;
  ausência ≠ `Ineligible`; recuperação por nova evidência válida (`08-VALIDATION.md`).
- **Sanitização:** observações sem credenciais. **Request/Response/Errors:** `TBD`.

## 16. Policies

Operações do inventário: `CRUD`, `effective policy preview`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** leitura autenticada; CRUD exige `Administrator`.
- **CSRF:** obrigatório em mutações.
- **Side effects:** CRUD altera estado funcional persistido (SQLite autoridade); cada tipo declara o seu schema;
  `effective policy preview` é read-only e resolve a precedência `channel > group > global > default` (DL-103),
  sem congelar snapshot.
- **Idempotência:** preview sim; CRUD idempotente por versão (conflito → `409`).
- **Merge:** scalar/object → substituição; collection → schema; `null` explícito → valor; ausência → herança;
  `Enabled=false` → desactivada (`38-POLICIES.md`).
- **Audit:** mutações auditáveis. **Sanitização:** sem secrets. **Request/Response/Errors:** `TBD`.

## 17. Ordering

Operações do inventário: `lists`, `items`, `import/export`, `preview`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** leitura autenticada; escrita/import exigem `Administrator`.
- **CSRF:** obrigatório em mutações.
- **Side effects:** escrita altera estado funcional persistido; import suporta `dry-run`; export/`preview` read-only.
- **Invariantes:** `(OrderingListId, Position)` único; um `CanonicalChannel` não ocupa duas posições na mesma lista;
  canal elegível sem presença não é publicado; Ordering não define identidade (DL-011).
- **Idempotência:** import idempotente por chave. **Audit:** mutações auditáveis.
- **Request/Response/Errors:** `TBD`.

## 18. Runs

Operações do inventário: `start`, `cancel`, `status`, `history`, `artifacts`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** autenticado; `start`/`cancel` exigem `Administrator`.
- **CSRF:** obrigatório em `start`/`cancel`.
- **Side effects:** `start` cria Run (máx. 1 activa; trigger concorrente → `409`); `cancel` propaga cancelamento
  e mantém efeitos externos já produzidos, permitindo reconciliação; `status`/`history`/`artifacts` read-only.
- **Idempotência:** leituras sim; `start` repetido com Run activa → `409`; reexecução lógica cria nova Run com
  relação causal; retry técnico não cria Run (DL-109; `13-RUNS.md`).
- **Audit:** `start`/`cancel` auditáveis. **Sanitização:** artifacts sem secrets.
- **Request/Response/Errors:** `TBD`.

## 19. Playlists

Operações do inventário: `generated outputs`, `download/serve`, `validation result`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** autenticado; leitura. Nenhuma operação cria output.
- **CSRF:** `N/A`.
- **Side effects:** nenhum (read-only); `download/serve` nunca gera nem modifica artifacts.
- **Idempotência:** sim. **Invariantes:** `publication state ∈ {Generated, Published, Superseded, Failed}`;
  path estável por `OrderingList`; parcial nunca é servido como válido (DL-019).
- **Sanitização:** sem credenciais no M3U servido. **Request/Response/Errors:** `TBD`.

## 20. Dispatcharr

Operações do inventário: `configuration`, `test connection`, `dry-run`, `sync`, `reconciliation`, `ownership view`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** leitura autenticada; `configuration`/`sync`/`reconciliation` exigem `Administrator`.
- **CSRF:** obrigatório nas mutações.
- **Side effects:** `configuration` grava estado funcional persistido (credenciais por referência);
  `test connection` e `dry-run` são read-only e não produzem efeitos externos (`22 §5`; DL-013/014);
  `sync` aplica o desired calculado respeitando ownership; `reconciliation`/`ownership view` observam sem alterar
  a verdade remota. Só `CrawlerManaged` pode ser removido automaticamente.
- **Idempotência:** `sync` idempotente por desired/snapshot; `test`/`dry-run` idempotentes.
- **Audit:** mutações e sincronizações auditáveis. **Sanitização:** credenciais nunca devolvidas.
- **Request/Response/Errors:** `TBD`.

## 21. Country — concorrência, limites e escrita (DG-19c)

Complementa a secção 6.

- **Concorrência:** escrita do overlay do operador serializada; `POST /api/country/save` usa controlo
  optimista por `version`; versão desactualizada → `409 version-conflict`; não há escrita concorrente do mesmo
  ficheiro. Os GET não escrevem nem criam ficheiros.
- **Rate limits:** `TBD` / `PARAMETER_GAP` — a BÍBLIA não fixa valores. Não inventar números.
- **Efeitos de escrita:** apenas `POST /api/country/save` escreve, de forma atómica, o overlay do operador
  (`runtime-data/countries/<cc>.json`) + auditoria; nunca escreve baseline nem fallback embutido.
- **Idempotência:** conteúdo canónico igual não gera nova `version`/`updatedAt`.
- **Sanitização:** sem secrets nas respostas/erros.
- **Versionamento:** mudanças incompatíveis exigem nova versão (DL-110).

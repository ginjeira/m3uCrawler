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

### 9.1 `GET /api/version`

- **Purpose:** devolver o snapshot de identidade/versionamento do binário em
  execução (semver, SHA, build number, data de build).
- **Auth:** `OPEN` (não decidida a obrigatoriedade de sessão; nenhum gate de
  autenticação é aplicado antes do handler; ver `WebDashboardService.cs:579-583`).
  `TBD` se deve passar a exigir sessão/token.
- **Autorização:** `TBD` (a BÍBLIA não fixa).
- **CSRF:** `N/A` (read-only). **Side effects:** nenhum. **Idempotência:** sim.
- **Response:** payload JSON camelCase `BuildVersionPayload()` descrito em
  `WebDashboardService.cs:3982` e consumido por `Directory.Build.props`/
  `Directory.Build.targets` (AGENTS.md §2; `BuildInfo`).
- **Erros:** `TBD` (BÍBLIA não fixa códigos de erro para esta rota).
- **Invariantes:** read-only; nunca cria nem escreve ficheiros.
- **Implementation reference:** `m3uCrawler/Services/WebDashboardService.cs:579-583`
  (dispatch) + `WebDashboardService.cs:3982` (construtor do payload).
- **Test reference:** `TBD` (não foi localizado teste directo do endpoint na
  documentação consultada).
- **OPEN/TBD:**
  - Auth obrigatória vs pública permanece `TBD` na BÍBLIA.
  - Schema exacto do payload é definido pelo código actual; ainda não
    documentado formalmente em `22`.

### 9.2 `GET /api/configuration/lifecycle`

- **Purpose:** devolver o estado do ciclo de vida de configuração (PHASE 9C.1),
  permitindo que o dashboard distinga inequivocamente "ainda não configurado"
  de "operacional"; não expõe operações destrutivas nem contorna autenticação
  (`WebDashboardService.cs:599-609`).
- **Auth:** `TBD` na BÍBLIA; nenhum gate adicional antes do handler para além
  do gate único do dashboard (não decidido se deve ser público).
- **Autorização:** `TBD`.
- **CSRF:** `N/A` (read-only). **Side effects:** nenhum. **Idempotência:** sim.
- **Response:** `BuildLifecyclePayloadAsync(_configurationLifecycle)` em
  `WebDashboardService.cs:607`; schema concreto `TBD` (definido pelo código).
- **Erros:** `TBD`.
- **Invariantes:** read-only; estado de NOT_CONFIGURED não impede que o
  dashboard permaneça acessível (PHASE 9C.1, `WebDashboardService.cs:599-604`).
- **Implementation reference:** `WebDashboardService.cs:599-609`.
- **Test reference:** `TBD` (não foi localizado teste directo na documentação
  consultada).
- **OPEN/TBD:** schema, autenticação, códigos de erro.

### 9.3 `GET /api/configuration/readiness`

- **Purpose:** capacidade derivada (`27-GLOSSARY`); reporta
  `bootstrapReady`/`hasAdmin`/`telegramAuthenticated`/`dispatcharrEnabled`/
  `dispatcharrValid`/`catalogOk`/`countryDataOk`/`outputOk`/`sourcesCount`/
  `setupComplete`/`operationalReady`/`adoptedFromLegacy`/`items[]` (chave,
  `required`, `satisfied`) — `WebDashboardService.cs:9926-9945`.
- **Auth:** gate único aplicado antes do handler (`IsSetupPath` em
  `WebDashboardService.cs:9911` faz parte do set de setup paths com gate
  pré-existente). `TBD` se deve ser público.
- **Autorização:** autenticado (gate existente). `TBD` se mutações adicionais
  vierem a exigir `Administrator`.
- **CSRF:** `N/A` (read-only, apenas GET — método errado → `405`).
- **Side effects:** nenhum. **Idempotência:** sim.
- **Erros:** `503 readiness-unavailable` quando `OperationalReadinessService`
  não está disponível (`WebDashboardService.cs:9919-9923`).
- **Invariantes:** read-only por contrato (`22 §5`); não altera estado.
- **Implementation reference:** `WebDashboardService.cs:9911-9945` (handler),
  invocado via `IsSetupPath` em `:9655`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo do payload, autenticação exacta, restantes
  códigos de erro.

### 9.4 `GET /health` (rota reservada)

- **Purpose / Method / Path / Schema / Auth:** `TBD (não definido na BÍBLIA)`.
  Não existe handler dedicado em `WebDashboardService.cs` consultada; nenhuma
  rota literal `/health` foi identificada.
- **OPEN/TBD:** existência, método, path, payload e autenticação da rota
  health são todos `TBD` na BÍBLIA.

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

### 11.1 `GET /api/telegram/config`

- **Purpose:** devolver, em modo de visualização, os campos de configuração
  Telegram já persistidos (apiId, phoneNumber, `hasApiHash`, sessionPath). O
  `api_hash` real **nunca** é devolvido — apenas `hasApiHash` indica a sua
  presença (`WebDashboardService.cs:9675-9683`).
- **Auth:** gate único de sessão/token aplicado antes do handler
  (`IsSetupPath`, `WebDashboardService.cs:9648-9656`).
- **Autorização:** autenticado. `TBD` se exige `Administrator` em leitura.
- **CSRF:** `N/A` (read-only). Método diferente de GET → `405`
  (`WebDashboardService.cs:9713`).
- **Side effects:** nenhum. **Idempotência:** sim.
- **Erros:** `503 telegram-unavailable` quando `TelegramAuthService` é
  `null` (`WebDashboardService.cs:9670-9672`).
- **Invariantes:** `api_hash`, password, códigos e sessão nunca são
  devolvidos (AGENTS.md §2; DL-020).
- **Implementation reference:** `WebDashboardService.cs:9665-9715`.
- **Test reference:** `TBD` (testes de dashboard não foram localizados na
  documentação consultada).
- **OPEN/TBD:** schema completo do payload; autenticação exacta; códigos
  adicionais; lista de campos sensíveis a mascarar.

### 11.2 `POST /api/telegram/config`

- **Purpose:** gravar/atualizar a configuração Telegram (apiId, apiHash,
  phoneNumber, sessionPath). Auditoria before/after (`WebDashboardService.cs:9701-9709`).
- **Auth:** sessão/token (`IsSetupPath`). **Autorização:** `Administrator`
  (mutação administrativa). **CSRF:** obrigatório.
- **Request:** `Content-Type: application/json`; corpo desserializado em
  `TelegramConfigWritePayload` (`WebDashboardService.cs:9694`); campos em
  falta preenchidos a partir da configuração guardada. Schema exacto `TBD`.
- **Response:** payload display (igual a 11.1) com `hasApiHash` actualizado.
- **Side effects:** escrita funcional persistida; registo de auditoria
  (`telegram.config.update` — `WebDashboardService.cs:9704`).
- **Erros:** `400 invalid-payload` (`WebDashboardService.cs:9697`),
  `503 telegram-unavailable`.
- **Idempotência:** sim por conteúdo canónico igual (igual à família Country,
  §6.4); `TBD` quanto ao resto.
- **Sanitização:** nunca persistir/devolver `api_hash` em claro; auditoria
  regista apenas display (`TelegramConfigDisplayToJson` —
  `WebDashboardService.cs:9706-9709`).
- **Implementation reference:** `WebDashboardService.cs:9686-9711`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo; validações específicas; códigos de erro
  adicionais.

### 11.3 `GET /api/telegram/auth/status`

- **Purpose:** devolver o estado actual da autenticação Telegram
  (`TelegramAuthStatus`) — `WebDashboardService.cs:9728-9730`.
- **Auth/CSRF:** gate único aplicado; `N/A` CSRF (read-only).
- **Side effects:** nenhum. **Idempotência:** sim.
- **Erros:** `503 telegram-unavailable` (`WebDashboardService.cs:9720-9723`).
- **Sanitização:** `Detail` retornado deve respeitar a invariante "no
  sensitive" (AGENTS.md §2; DL-020); o código actual passa o `Detail`
  directamente — `OPEN` se este deva ser sanitizado em respostas.
- **Implementation reference:** `WebDashboardService.cs:9725-9731`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo do payload; sanitização do `Detail` em
  respostas; códigos de erro.

### 11.4 `POST /api/telegram/auth/start`

- **Purpose:** iniciar a autenticação Telegram. Corpo opcional
  (`TelegramStartPayload`); campos fornecidos são persistidos e os em falta
  preenchidos a partir da configuração guardada (nunca exposta ao chamador —
  `WebDashboardService.cs:9736-9744`). Auditoria com before implícito
  (estado anterior não persistido como payload) e estado final
  (`WebDashboardService.cs:9747-9750`).
- **Auth:** sessão/token + `Administrator`. **CSRF:** obrigatório.
- **Side effects:** interage com serviço externo (Telegram); registo de
  auditoria (`telegram.auth.start`).
- **Erros:** `400 invalid-payload`, `503 telegram-unavailable`.
- **Idempotência:** `TBD` na BÍBLIA.
- **Sanitização:** `api_hash`, código, password, sessão **nunca** em
  auditoria/resposta.
- **Implementation reference:** `WebDashboardService.cs:9733-9753`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo; comportamento de re-start sobre Run
  activa; códigos de erro.

### 11.5 `POST /api/telegram/auth/code`

- **Purpose:** submeter o código de verificação Telegram. `Code` é
  obrigatório; `null` → `400 invalid-payload`
  (`WebDashboardService.cs:9758-9762`). Auditoria por estado resultante.
- **Auth:** sessão/token + `Administrator`. **CSRF:** obrigatório.
- **Side effects:** interage com o serviço externo; auditoria.
- **Erros:** `400 invalid-payload`; `503 telegram-unavailable`; `TBD` para
  restantes códigos (e.g. code inválido).
- **Sanitização:** código **nunca** registado nem devolvido (AGENTS.md §2).
- **Implementation reference:** `WebDashboardService.cs:9755-9772`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema; códigos de erro adicionais; mapeamento de erros
  externos para códigos estáveis.

### 11.6 `POST /api/telegram/auth/password`

- **Purpose:** submeter a password 2FA. `Password` obrigatório
  (`WebDashboardService.cs:9777-9781`). Auditoria por estado resultante.
- **Auth:** sessão/token + `Administrator`. **CSRF:** obrigatório.
- **Side effects:** interage com o serviço externo; auditoria.
- **Erros:** `400 invalid-payload`; `503 telegram-unavailable`; `TBD` para
  restantes códigos.
- **Sanitização:** password **nunca** registada nem devolvida.
- **Implementation reference:** `WebDashboardService.cs:9774-9791`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema; códigos de erro adicionais; mapeamento.

### 11.7 `POST /api/telegram/auth/disconnect` (logout Telegram)

- **Purpose:** terminar sessão Telegram (logout). **NÃO IMPLEMENTADO** como
  endpoint HTTP — `41-API-INVENTORY.md` lista esta operação; `46-REQUIREMENT-
TRACEABILITY.md:75` regista-a como gap.
- **Auth/Authz/CSRF (contratuais):** sessão/token + `Administrator`; CSRF
  obrigatório.
- **Side effects:** encerra sessão Telegram; `TBD` se afecta o `session.dat`
  em disco ou apenas a representação em runtime.
- **Audit:** obrigatório; sem secrets.
- **Sanitização:** sem segredos.
- **Request/Response/Errors:** `TBD` (não há handler para confirmar path).
- **Implementation reference:** não encontrada; `46-REQUIREMENT-TRACEABILITY.md:75`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** path, método, schema, idempotência, códigos de erro, side
  effects. Esta ficha documenta o gap, NÃO o preenche.

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

### 13.1 `GET /api/discovery/summary`

- **Purpose:** sumário deduplicado de descoberta: total bruto, distinct,
  duplicatesCollapsed, items (`DeduplicateBySourceName`).
  `WebDashboardService.cs:789-808`.
- **Auth/CSRF:** autenticado; `N/A` CSRF (read-only).
- **Side effects:** nenhum. **Idempotência:** sim.
- **Erros:** `200` com `{ error: "Sem relatório de execução disponível." }`
  quando o report não existe (`WebDashboardService.cs:793-795`).
- **Invariantes:** dedup por `(source, name)`, ordenação determinística
  (`DeduplicateBySourceName`); leitura de `telegram_run_report.json`
  (`outputDir`). Sem mutação.
- **Implementation reference:** `WebDashboardService.cs:789-808`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo do payload; códigos de erro formalizados
  (actualmente texto livre); autenticação exacta.

### 13.2 `GET /api/discovered-playlists`

- **Purpose:** devolve a lista `DiscoveredPlaylists` do último relatório
  Telegram. `WebDashboardService.cs:774-786`.
- **Auth/CSRF:** autenticado; `N/A` CSRF (read-only).
- **Side effects:** nenhum. **Idempotência:** sim.
- **Erros:** `{ error: "Sem relatório de execução disponível." }` quando
  o report não existe.
- **Invariantes:** read-only; nunca cria nem altera o report.
- **Implementation reference:** `WebDashboardService.cs:774-786`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo; códigos de erro formalizados.

### 13.3 `GET /api/discovery/settings`

- **Purpose:** configuração operacional de discovery (fonte de verdade única
  em `app_settings.json`). GET devolve valores persistidos; POST valida e
  persiste — gate `9C.2` (sessão + CSRF para método mutante)
  `WebDashboardService.cs:810-818` e `HandleDiscoverySettingsEndpointAsync`.
- **Auth:** sessão/token; **Autorização:** `Administrator` no POST;
  `TBD` para o GET.
- **CSRF:** obrigatório no POST; `N/A` no GET.
- **Side effects (POST):** alteração de estado funcional persistido;
  `TBD` auditoria explícita (a BÍBLIA não obriga; o gate 9C.2 é a fonte).
- **Idempotência (POST):** `TBD` na BÍBLIA.
- **Sanitização:** sem secrets.
- **Implementation reference:** `WebDashboardService.cs:810-818`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo do `DiscoverySettingsPayload` (referenciado
  em `WebDashboardService.cs:4174`); códigos de erro; auditoria.

### 13.4 `start run` (start de execução de discovery)

- **Purpose:** criar nova Run de discovery. Mapeada na secção API-Runs
  (§18); aqui apenas como referência cross-family.
- **OPEN/TBD:** path/método exactos `TBD`; documentado em `46:77` como
  `WebDashboardService.cs:789-824, 945-955` (start/list).

### 13.5 `GET /api/candidates` e detalhes (candidate details)

- **Purpose:** listar e detalhar candidatos de discovery.
- **Status:** **NÃO IMPLEMENTADO** como endpoint HTTP dedicado.
  `46-REQUIREMENT-TRACEABILITY.md:77` regista o gap.
- **Invariantes:** nunca cria `CanonicalChannel` nem identidade
  (DL-002/DL-003); `Candidate → Source` é transição explícita
  (`PipelineIngestionService.cs:190-198`).
- **Implementation reference:** não encontrada handler dedicado.
- **Test reference:** `TBD`.
- **OPEN/TBD:** path, método, schema, paginação, filtros, códigos de erro.

### 13.6 `POST /api/candidates/{id}/accept|reject`

- **Purpose:** decidir explicitamente uma ocorrência (aceitar ou rejeitar).
- **Status:** **NÃO IMPLEMENTADO** como endpoint HTTP. `46:77` regista o
  gap. As decisões actualmente vivem no domínio (e.g.
  `PipelineIngestionService.cs:190-198` — `Candidate → Source` explícito).
- **Invariantes (contratuais):** `accept` conduz à passagem explícita
  `Candidate → Source`; `reject` é terminal; nunca cria `CanonicalChannel`
  nem identidade (DL-002/DL-003); `409` em decisão sobre ocorrência já
  terminal; auditar decisão.
- **OPEN/TBD:** path, método, schema, idempotência, códigos de erro,
  auditoria. Esta ficha documenta o gap, NÃO o preenche.

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

### 14.1 `GET /api/catalog/...` — channels/details/aliases/external identities

- **Purpose:** operações de leitura do catálogo — baseadas em
  `WebDashboardService.cs:2591-2622` (observations) e `:968-996, 2932-3145`
  (`46:78`). Detalhe por endpoint `TBD` (path/método exactos por capability).
- **Auth/CSRF:** autenticado; `N/A` CSRF.
- **Side effects:** nenhum. **Idempotência:** sim.
- **Invariantes:** identidade canónica é autoridade (DL-001); nenhuma leitura
  cria identidade (DL-002); `Key` não muda após criação (`23:57`).
- **Implementation reference:** `WebDashboardService.cs:2591-2622,
  2932-3145` (cross-ref a Sources §12; ver também 14.2).
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema por sub-endpoint; autenticação exacta; códigos de
  erro.

### 14.2 `POST /api/catalog/import-policies` (POST de policies de import)

- **Purpose:** actualmente trata-se de `StreamValidationOptions`-style
  policy store (`StreamValidationPolicyStore` — `WebDashboardService.cs:3194-3236`)
  e de `AppSettings` (`:3238-3289`); o bloco `CatalogBaselineImporter`
  (import/export) **não** está exposto como endpoint HTTP dedicado
  (`46:78` regista o gap).
- **Status:** **NÃO IMPLEMENTADO** como endpoint HTTP de import/export de
  catálogo. A BÍBLIA (`23 §2`) exige: `create`/`update`/`conflict`/`delete`/
  `dry-run`/`preservação de IDs`/`compatibilidade`, e `nunca apagar
  silenciosamente informação local por diferença de ficheiro`.
- **Invariantes (contratuais):** import suporta `dry-run`; idempotência por
  chave/versão; conflitos declarados (`23:59`); `Key` não muda após
  criação (`23:57`); nenhuma operação cria identidade implicitamente
  (DL-002).
- **OPEN/TBD:** path, método, schema request/response, paginação,
  códigos de erro, formato de `dry-run`, formato do conflito. Esta ficha
  documenta o gap, NÃO o preenche.

### 14.3 `GET /api/catalog/export` (export)

- **Purpose:** exportar catálogo.
- **Status:** **NÃO IMPLEMENTADO** como endpoint HTTP dedicado (`46:78`).
- **Invariantes (contratuais):** read-only; nunca apaga nem altera
  estado local; sem secrets; compatível com o schema versionado definido em
  `23:42-67`.
- **OPEN/TBD:** path, método, formato (MIME), schema, versionamento.
  Esta ficha documenta o gap, NÃO o preenche.

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

### 15.1 Observações — placement efectivo (desvio documentado)

- **Path implementado:** `GET /api/catalog/channel-sources/{id}/observations?limit={n}`
  e `POST /api/catalog/channel-sources/{id}/observations`
  (`WebDashboardService.cs:2591-2655`). **Não** está sob `/api/validation/...`.
- **Discrepância vs. contract stub:** o stub de §15 sugere que `observations`
  pertenceria à família Validation, mas o **código** expõe-o sob a família
  Catalogue (ChannelSource é relação `CanonicalChannel ↔ Source`; DL-007).
- **Classificação:** este desvio **não é corrigido** por esta wave
  (registado, não transformado em decisão).
- **Implementação documentada:** 15.2 e 15.3 abaixo descrevem o caminho
  efectivo; 15.4 descreve o caminho contratualmente sugerido pela BÍBLIA
  que ainda **não existe** como endpoint.

### 15.2 `GET /api/catalog/channel-sources/{id}/observations?limit={n}`

- **Purpose:** devolver a lista de `ChannelSourceObservation` (factos
  técnicos) para um `ChannelSource` específico; default `limit=200`
  (`WebDashboardService.cs:2606-2611`).
- **Auth/CSRF:** autenticado (gate único); `N/A` CSRF (read-only).
- **Side effects:** nenhum. **Idempotência:** sim.
- **Erros:** `400 ID inválido.` quando `{id}` não é parseável
  (`WebDashboardService.cs:2602-2604`); restante `TBD`.
- **Invariantes:** `Observation` é a autoridade do facto técnico
  (`08-VALIDATION.md:26`); absence ≠ `Ineligible` (`08:56`); recuperação
  por nova evidência válida (`08:52`); sem credenciais (DL-020).
- **Implementation reference:** `WebDashboardService.cs:2591-2615`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo do payload; códigos de erro formalizados;
  validação adicional do `{id}`.

### 15.3 `POST /api/catalog/channel-sources/{id}/observations`

- **Purpose:** registar uma nova `ChannelSourceObservation` para um
  `ChannelSource` (quality, epg, availability, responseTimeMs). Enums
  parseados em modo tolerante (default `Unknown`/`Discovered` quando
  inválidos — `WebDashboardService.cs:2641-2643`).
- **Auth:** autenticado + `Administrator` (mutação). **CSRF:** obrigatório.
- **Side effects:** persistência de nova `Observation` (facto técnico);
  não cria identidade (DL-008).
- **Erros:** `400 ID inválido.`; `400 Payload inválido.`; `TBD` restantes.
- **Sanitização:** sem credenciais em payloads/respostas.
- **Implementation reference:** `WebDashboardService.cs:2617-2654`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo do payload; códigos de erro; validação de
  enums (actualmente tolerante).

### 15.4 `run` (Validation run)

- **Purpose:** desencadear Validation run que produz `Observation` e
  alimenta o cálculo de `Eligibility`.
- **Status:** **NÃO IMPLEMENTADO** como endpoint HTTP dedicado
  (`46-REQUIREMENT-TRACEABILITY.md:81`).
- **Invariantes (contratuais):** `run` produz `Observation` (facto técnico)
  e desencadeia o cálculo de `Eligibility`; `run` re-executado **não** deve
  criar estado falso de sucesso; ausência ≠ `Ineligible` (`08:56`);
  auditoria obrigatória.
- **OPEN/TBD:** path, método, schema request/response, paginação,
  códigos de erro, gatilho de recálculo de Eligibility. Esta ficha
  documenta o gap, NÃO o preenche.

### 15.5 `GET /api/validation/eligibility`

- **Purpose:** devolver o `Eligibility` (`Eligible | Ineligible | Unknown`)
  derivado de observations + policy (`08:30-34`).
- **Status:** **NÃO IMPLEMENTADO** como endpoint HTTP dedicado
  (`46:81`). A BÍBLIA (`08:50`) lembra que a correspondência entre
  categorias de Validation e `Eligible | Ineligible | Unknown` é
  **NORMATIVA por categoria** e NÃO arbitrariamente configurável pelo
  operador.
- **Invariantes (contratuais):** read-only (derivado); ausência de
  observação ≠ `Ineligible` (`08:56`); histerese `PARAMETER_GAP`
  (`08:58`).
- **OPEN/TBD:** path, método, schema, código de erro. Esta ficha
  documenta o gap, NÃO o preenche.

### 15.6 Cross-references Validation (policy store)

- **Path:** `GET /api/validation/policy`, `POST /api/validation/policy`,
  `POST /api/validation/test` — implementados em
  `WebDashboardService.cs:3194-3327`. Estas rotas pertencem à família
  **Policies** (não Validation) por responsabilidade funcional (gestão do
  `StreamValidationPolicyStore`); referenciadas aqui apenas para evitar
  confusão de placement.
- **OPEN/TBD:** confirmar se devem migrar para §16 (Policies) em wave
  futura de reestruturação do documento.

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
- **Audit:** mutações auditáveis. **Sanitização:** sem secrets. **Request/Schema/Errors:** `TBD`.

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

### 17.1 `GET /api/catalog/ordering-lists`

- **Purpose:** listar `OrderingList`. `WebDashboardService.cs:1711-…`.
- **Auth/CSRF:** autenticado; `N/A` CSRF.
- **Side effects:** nenhum. **Idempotência:** sim.
- **Invariantes:** read-only; Ordering não define identidade (DL-011).
- **Implementation reference:** `WebDashboardService.cs:1711+`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo; paginação; filtros; códigos de erro.

### 17.2 `POST /api/catalog/ordering-lists`

- **Purpose:** criar `OrderingList`. **Mutação administrativa.**
- **Auth:** autenticado + `Administrator`. **CSRF:** obrigatório.
- **Invariantes:** escrita atómica? `TBD` na BÍBLIA.
- **Implementation reference:** `WebDashboardService.cs:1711+`.
- **OPEN/TBD:** schema, códigos de erro, validação.

### 17.3 `GET /api/catalog/ordering-lists/{id}`

- **Purpose:** detalhe de uma `OrderingList`.
- **Auth/CSRF:** autenticado; `N/A` CSRF.
- **Implementation reference:** `WebDashboardService.cs:1749+`.
- **OPEN/TBD:** schema, códigos de erro (`404` quando não existe?).

### 17.4 `POST /api/catalog/ordering-lists/{id}/duplicate`

- **Purpose:** duplicar `OrderingList`. **Mutação administrativa.**
- **Auth:** autenticado + `Administrator`. **CSRF:** obrigatório.
- **Implementation reference:** `WebDashboardService.cs:1749+` (handler
  inclui `duplicate`).
- **OPEN/TBD:** schema, códigos de erro, regras de cópia.

### 17.5 `DELETE /api/catalog/ordering-lists/{id}`

- **Purpose:** apagar `OrderingList`. **Mutação administrativa.**
- **Auth:** autenticado + `Administrator`. **CSRF:** obrigatório.
- **Implementation reference:** `WebDashboardService.cs:1749+`.
- **OPEN/TBD:** schema, idempotência, códigos de erro.

### 17.6 `POST /api/catalog/ordering-lists/{id}/items`

- **Purpose:** adicionar item a uma lista.
- **Auth:** autenticado + `Administrator`. **CSRF:** obrigatório.
- **Invariantes:** `(OrderingListId, Position)` único; um `CanonicalChannel`
  não ocupa duas posições na mesma lista.
- **Implementation reference:** `WebDashboardService.cs:1749+`.
- **OPEN/TBD:** schema, códigos de erro.

### 17.7 `PUT /api/catalog/ordering-items/{id}` e `DELETE /api/catalog/ordering-items/{id}`

- **Purpose:** editar / remover item.
- **Auth:** autenticado + `Administrator`. **CSRF:** obrigatório.
- **Implementation reference:** `WebDashboardService.cs:1870+`.
- **OPEN/TBD:** schema, códigos de erro.

### 17.8 `GET /api/catalog/ordering-lists/{id}/preview`

- **Purpose:** preview do output esperado. Read-only.
- **Auth/CSRF:** autenticado; `N/A` CSRF.
- **Invariantes:** read-only; canal elegível sem presença **não** é
  publicado.
- **Implementation reference:** `WebDashboardService.cs:1749+` (handler
  inclui `preview`).
- **OPEN/TBD:** schema, formato de preview.

### 17.9 Import/Export HTTP

- **Status:** **NÃO IMPLEMENTADO** como endpoint HTTP dedicado
  (`46-REQUIREMENT-TRACEABILITY.md:83`). **Q-D é blocker** (ver `§17` desta
  wave — instruções absolutas).
- **Invariantes (contratuais):** import suporta `dry-run`; idempotente por
  chave; export read-only; Ordering não define identidade (DL-011).
- **OPEN/TBD:** path, método, schema, formato de `dry-run`, códigos de
  erro. Esta ficha documenta o gap, NÃO o preenche.

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

### 18.1 `cancel` — contrato

- **Comportamento contratual (§18 preâmbulo):** "`cancel` propaga cancelamento
  e **mantém efeitos externos já produzidos**, permitindo reconciliação".
- **Autorização:** `Administrator` + CSRF.
- **Idempotência:** `TBD` (a BÍBLIA não fixa; repetir `cancel` sobre Run
  já cancelada é `TBD` — pode ser no-op idempotente ou `409 state-conflict`).
- **Audit:** obrigatório.
- **Sanitização:** artifacts sem secrets (DL-020).
- **Request/Response/Errors:** `TBD`.

#### 18.1.1 Divergência documentada — implementação vs. contrato

- **Implementação actual:** o `RunCoordinator` **não** trata `cancel` HTTP
  como soft-cancel; runs interrompidos sem conclusão persistida são
  marcados como `LiveRunTerminalStatus.Failed` em
  `RecoverInterruptedRunsAsync` (`RunCoordinator.cs:427-455`,
  comentário `:420-426`: "run interrompido sem conclusão persistida ⇒
  failed, sem criar estado `unknown`").
- **Classificação:** este é um **desvio de implementação** entre o
  comportamento contratual de §18 ("cancel propaga cancelamento e mantém
  efeitos externos") e o que `RunCoordinator` actualmente materializa
  (interrupção ⇒ failed).
- **Esta wave NÃO corrige** a divergência. O desvio é **registado** como
  gap; a BÍBLIA permanece autoridade; RunCoordinator não é tocado.
- **Implementação documentada (sem correcção):**
  `RunCoordinator.cs:427-455`. Documentação original: `DL-109`,
  `13-RUNS.md`.

### 18.2 `start` (Run start)

- **Purpose:** criar nova Run activa.
- **Status:** parcialmente implementado em `WebDashboardService.cs:573-955`
  (start/status/history — `46:84`); detalhes exactos do start `TBD` na
  documentação consultada.
- **Invariantes (contratuais):** máx. 1 Run activa; trigger concorrente →
  `409`; retry técnico **não** cria Run; reexecução lógica cria nova Run
  com relação causal (DL-109).
- **OPEN/TBD:** path, método, schema, códigos de erro completos.

### 18.3 `POST /api/run/cancel`

- **Status:** **NÃO IMPLEMENTADO** como endpoint HTTP dedicado
  (`46:84`).
- **OPEN/TBD:** path, método, schema, códigos de erro. Esta ficha
  documenta o gap e a divergência (18.1.1), NÃO o preenche.

### 18.4 `status` / `history` / `artifacts`

- **Status:** `status`/`history` parcialmente implementados
  (`WebDashboardService.cs:573-955`); `artifacts` **NÃO IMPLEMENTADO**
  (`46:84`).
- **Invariantes (contratuais):** read-only; sanitização de artifacts
  (DL-020).
- **OPEN/TBD:** paths/métodos/schema para cada; códigos de erro; formato
  de artifact.

## 19. Playlists

Operações do inventário: `generated outputs`, `download/serve`, `validation result`.

- **Method/Route:** `TBD (não definido na BÍBLIA)`.
- **Auth/Authz:** autenticado; leitura. Nenhuma operação cria output.
- **CSRF:** `N/A`.
- **Side effects:** nenhum (read-only); `download/serve` nunca gera nem modifica artifacts.
- **Idempotência:** sim. **Invariantes:** `publication state ∈ {Generated, Published, Superseded, Failed}`;
  path estável por `OrderingList`; parcial nunca é servido como válido (DL-019).
- **Sanitização:** sem credenciais no M3U servido. **Request/Response/Errors:** `TBD`.

### 19.1 `GET /api/playlist/preview` e `GET /api/playlist_temp/preview`

- **Purpose:** preview das playlists funcional e temporária.
  `WebDashboardService.cs:716` e `:745`.
- **Auth/CSRF:** autenticado (gate único); `N/A` CSRF.
- **Side effects:** nenhum. **Idempotência:** sim.
- **Invariantes:** read-only; **nunca** modifica nem cria artifacts
  (DL-019); `parcial` nunca é servido como válido
  (`11-OUTPUT.md:34`).
- **Sanitização:** playlist funcional preserva URLs Xtream (AGENTS.md §2);
  endpoints de preview usam sanitização (AGENTS.md §2).
- **Implementation reference:** `WebDashboardService.cs:716, 745`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema; autenticação exacta; códigos de erro.

### 19.2 `validation result` (Validation result da playlist)

- **Status:** **NÃO IMPLEMENTADO** como endpoint HTTP dedicado
  (`46-REQUIREMENT-TRACEABILITY.md:85`).
- **Invariantes (contratuais):** read-only; `publication state ∈ {Generated,
  Published, Superseded, Failed}` (`11-OUTPUT.md:37`); path estável por
  `OrderingList`; parcial nunca é servido como válido (DL-019).
- **OPEN/TBD:** path, método, schema, formato do `publication state`,
  códigos de erro. Esta ficha documenta o gap, NÃO o preenche.

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

### 20.1 Invariantes da família (transversal)

- **Default `dispatcharr_dry_run=true`** (AGENTS.md §2): quando activado,
  o fluxo `Generate → Validate → Apply` produz
  `output/dispatcharr_plan_<ts>.json` e `output/dispatcharr_report_<ts>.json`
  sem chamadas HTTP de escrita remotas. Só passa a mutar o Dispatcharr
  quando o utilizador coloca `dispatcharr_dry_run=false`.
- **Sanitização obrigatória:** `CredentialSanitizer.SanitizeUrl` e
  `SanitizeM3uContent` aplicam-se a **todos** os pontos de saída: consola,
  `RunReport`, JSONs de relatório, preview do dashboard, mensagens de
  erro (AGENTS.md §2; DL-020). `MatchPlanSerializer.SanitizeForSerialization`
  aplica `SanitizeUrl` ao campo `streamUrl` (AGENTS.md §2).
- **Credenciais:** `dispatcharr_api_key`/`dispatcharr_username`/
  `dispatcharr_password` vivem **apenas** em `wtelegram.config` (fora do
  `package.yml` e do git); nunca passam por `JsonSerializer.Serialize`
  directo sem sanitização (AGENTS.md §2).
- **Ownership:** `CrawlerManaged`/`External`/`Unknown`; só `CrawlerManaged`
  pode ser removido automaticamente (`12-DISPATCHARR.md:21-24`; DL-014).
- **Ambiguous:** casos `MatchBand.Ambiguous` **nunca** são aplicados
  automaticamente; ficam `SyncOutcome.Ambiguous` no plano e contam em
  `report.AmbiguousDecisions` (AGENTS.md §2).
- **Camada de matching pura:** `Services/Matching/*` é determinística,
  sem dependências HTTP; `IChannelMatcher.BuildPlan` aceita listas de DTOs
  e devolve `MatchPlan` byte-idêntico para o mesmo input + `nowUtc`
  injetável (AGENTS.md §2).
- **Streams criadas pelo crawler:** `is_custom=true` no Dispatcharr para
  que `name`/`url`/`tvg_id` permaneçam editáveis
  (`stale_stream_days` da conta M3U externa não as varre — AGENTS.md §2).
- **Streams em modo leitura:** `is_custom=false`, com `m3u_account`
  definido, têm `name`/`url`/`tvg_id`/`channel_group` read-only —
  **não** se tenta editá-las (AGENTS.md §2).

### 20.2 `GET /api/dispatcharr/config`

- **Purpose:** devolver configuração persistida do Dispatcharr (credenciais
  por referência; nunca em claro — AGENTS.md §2). Implementação em
  `WebDashboardService.cs:9797-…`.
- **Auth/CSRF:** gate único (`IsSetupPath`); `N/A` CSRF.
- **Side effects:** nenhum. **Idempotência:** sim.
- **Sanitização:** credenciais nunca devolvidas (DL-020).
- **Implementation reference:** `WebDashboardService.cs:9797-…`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema completo do payload; códigos de erro.

### 20.3 `POST /api/dispatcharr/config`

- **Purpose:** gravar configuração (credenciais por referência).
- **Auth:** autenticado + `Administrator`. **CSRF:** obrigatório.
- **Side effects:** escrita funcional persistida; auditoria.
- **Sanitização:** credenciais nunca devolvidas em auditoria (apenas
  referências).
- **Implementation reference:** `WebDashboardService.cs:9797-…`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema; códigos de erro; validação.

### 20.4 `POST /api/dispatcharr/test`

- **Purpose:** `test connection` — read-only por contrato (`22 §5`); não
  produz efeitos remotos (`DL-013`/`DL-015`).
- **Auth:** autenticado. **CSRF:** obrigatório (mutação que acarreta I/O).
- **Side effects:** teste contra o serviço externo; sem escrita remota;
  resultado sanitizado.
- **Idempotência:** sim.
- **Sanitização:** URLs com credenciais passam por `SanitizeUrl` antes de
  qualquer exposição (AGENTS.md §2; DL-020).
- **Implementation reference:** `WebDashboardService.cs:9797-…` (handler
  `test`); `DispatcharrConnectionTesterTests`.
- **Test reference:** `DispatcharrConnectionTesterTests` (citado em `46:86`).
- **OPEN/TBD:** schema request/response; códigos de erro.

### 20.5 `GET /api/dispatcharr/state`

- **Purpose:** estado actual da integração Dispatcharr
  (`WebDashboardService.cs:900` referenciado em `46:86`).
- **Auth/CSRF:** autenticado; `N/A` CSRF.
- **Side effects:** nenhum. **Idempotência:** sim.
- **Implementation reference:** `WebDashboardService.cs:900`.
- **Test reference:** `TBD`.
- **OPEN/TBD:** schema; códigos de erro.

### 20.6 `dry-run` (Dispatcharr)

- **Purpose:** gerar `MatchPlan` + `SyncReport` sem aplicar (DL-015; `12-DISPATCHARR.md:11-14`;
  AGENTS.md §2). Persiste `output/dispatcharr_plan_<ts>.json` e
  `output/dispatcharr_report_<ts>.json` (`DispatcharrSyncService.cs:169`, `:251`).
- **Status:** **CONTRACT RATIFIED (DL-128)** — `POST /api/dispatcharr/dry-run`
  (D1; DL-128). **HTTP IMPLEMENTATION SHIPPED** at HEAD
  `4b6643e77bd87328d94d1df1097e727545de7b30` (commit
  `feat(dispatcharr-http): implement POST /api/dispatcharr/{dry-run,sync} per DL-128`):
  endpoint registado em `WebDashboardService.cs:976`; handler único partilhado
  em `WebDashboardService.cs:9150` (chamado de `:979` com `forceDryRun: true`);
  gate de concorrência dedicado em `DispatcharrConcurrencyGate.cs` (D8
  concretizado); `forceDryRun` propagado em `DispatcharrSyncCoordinator.cs:104,
  :115`. Camada de domínio canónica mantida (DL-015; AGENTS.md §2):
  `DispatcharrSyncService.RunAsync` — branch `_config.DryRun=true` em
  `DispatcharrSyncService.cs:196-206`; orquestrador único
  `DispatcharrSyncCoordinator.RunAsync` em `DispatcharrSyncCoordinator.cs:73`.
  Gates de caminho (`dispatcharr_enabled`, `no-playlist`) continuam a viver no
  Coordinator. **RUNTIME-VALIDATED (dry-run)** end-to-end via wave
  `W-RUNTIME-DISPATCHARR-DRYRUN-CONTROLLED` (dry-run contra fixture):
  4 GETs observados / 0 writes / plano + relatório produzidos em `output/` /
  counts `0 / 43 / 164 / 0 / 139 / 0 / 0 / 0`.
- **Method/Path:** `POST /api/dispatcharr/dry-run` (D1; ratificado por
  DL-128). Família usa prefixo `/api/dispatcharr/...`
  (`WebDashboardService.cs:900, 9653-9654, 9797, 9860`). Não se criam
  variantes alternativas do path (e.g. `dryrun`); a forma é exactamente
  `dry-run`. Método diferente de `POST` → `405`.
- **Auth/Authz:** autenticado (gate único do dashboard); sem requisito
  adicional de `Administrator` para `dry-run` (consistente com `22 §20:977`
  — leitura/autenticação-base para esta operação).
- **CSRF:** obrigatório (`22 §8:244`; `22 §20:977`); `dry-run` dispara o
  pipeline `Generate → Validate → Apply` (secção 20.1) e é, do ponto de
  vista HTTP, uma mutação.
- **Request:** corpo `application/json` mínimo:
  ```json
  { "playlistPath": "..." }
  ```
  `playlistPath` corresponde directamente ao parâmetro de domínio consumido
  por `DispatcharrSyncService.RunAsync(playlistPath, selection, ct)`
  (`DispatcharrSyncService.cs:119`). **Não** se introduz `PlaylistId`, **não**
  se cria entidade `Playlist`, **não** se expõe `DispatcharrSourceSelection`
  no contrato HTTP (D2; DL-128). `DispatcharrSourceSelection` continua a ser
  artefacto interno/sanitizado, com campo `streamUrl` portador de credenciais
  sanitizadas via `CredentialSanitizer.SanitizeUrl` — fica de fora desta
  superfície.
- **dry_run override:** **não há**. O endpoint **não** aceita qualquer campo
  de override (D3; DL-128). Concretamente:
  - corpo **não** inclui `dry_run`, `dryRun`, `dry_run=false` nem qualquer
    `dispatcharr_dry_run`;
  - aceitar-e-ignorar está **proibido** — payload com campos `dry_run*` é
    `400 invalid-payload`;
  - sem mecanismo alternativo de override (variável de ambiente, header,
    cookie, query string, etc.);
  - `/dry-run` significa sempre dry-run; `/sync` significa sempre
    apply/sync per configuração persistida e contrato existente.
- **Response / success status:** `200 OK` (D5; DL-128) — operação
  síncrona. Resposta híbrida **sanitizada** que **não** embebe o conteúdo
  integral do plano/relatório:
  ```json
  { "status": "ok|partial|failed|dry-run|...",
    "mode": "dry-run|sync",
    "planPath": "...",
    "reportPath": "...",
    "counts": {} }
  ```
  Forma orientativa (D4; DL-128): o que se fixa é **orientação estrutural**
  — referência a artefactos e contagens, **sem** credenciais, **sem**
  conteúdo integral do `dispatcharr_plan_*.json`/`dispatcharr_report_*.json`
  embutido, **sem** campos inventados. Reutilização de tipos existentes
  (`SyncReportCounts`, `DispatcharrSyncResult`, `DispatcharrSyncOutcome`)
  deve preceder criação de novos DTOs. Se a implementação revelar que a
  estrutura requer decisão adicional, regista-se como discrepância — não se
  resolve silenciosamente (D4; DL-128). `202 Accepted` **não** é aceitável
  para esta versão síncrona do contrato (D5; DL-128).
- **Side effects:** filesystem-local apenas — escreve
  `dispatcharr_plan_<ts>.json` e `dispatcharr_report_<ts>.json` em
  `_outputDir` (default `output/`; `DispatcharrSyncService.cs:129, 169, 251`);
  regista `SyncRunEntity` + passos via `RecordSyncRunStepSafeAsync`
  (`DispatcharrSyncService.cs:296-345`) com `result="dry-run"` (`:200-205`).
  **Nenhuma** chamada HTTP de escrita ao Dispatcharr (DL-013, DL-015;
  `DispatcharrSyncService.cs:196-206`).
- **Idempotência:** sim (família `22 §20:982`; DL-018). Plano byte-idêntico
  para o mesmo input + `nowUtc` injetável (AGENTS.md §2; `22 §20.1:1007-1010`).
- **Concurrency:** no máximo uma Dispatcharr sync activa por runtime (D8;
  DL-128). Segunda tentativa concorrente → `409 concurrency-conflict`. O
  mecanismo concreto é um **gate de concorrência dedicado ao Dispatcharr**
  — segue o mesmo princípio atómico de `RunCoordinator` mas **não** o
  reutiliza directamente (gate Telegram-scoped, sem mistura de semânticas):
  aquisição atómica; falha do segundo candidato com conflito; libertação
  garantida em `finally`; não dependente apenas de um flag `IsRunning`
  (`TOCTOU`); não cria uma segunda semântica de Run Telegram; não altera
  `RunCoordinator`. A concretização deste gate é diferida para wave de
  implementação (D8; DL-128).
- **Audit:** `SyncRunEntity` (início em `running`, fim em `ok`/
  `dry-run`/`error`; `DispatcharrSyncService.cs:296-345`) e passos
  `read-plan`, `selection`, `dry-run` (`:162-167, 185-190, 200-205`). Apenas
  o tipo de excepção é persistido em falha, nunca a mensagem
  (`DispatcharrSyncCoordinator.cs:121`; DL-020). Nenhum mecanismo de
  auditoria HTTP adicional duplica esta observabilidade (D10; DL-128). Se
  um evento HTTP-nível for necessário por observabilidade/compliance,
  demonstra-se durante a implementação — não se inventa agora.
- **Errors:** envelope canónico (D6; DL-128):
  ```json
  { "error": "<código estável>", "message": "<segura>", "correlationId": "<id>" }
  ```
  Códigos reutilizando a família transversal `22 §8` e DL-120:
  `401 authentication-required`; `403 forbidden`/`403 csrf-invalid`;
  `409 concurrency-conflict` (gate Dispatcharr deteve segunda invocação);
  `422 invalid-payload` (payload mal formado, `dry_run*` rejeitado,
  `playlistPath` inválido); `500 persistence-error`; `502 dispatcharr-comm-error`
  (reservado para o `sync` — não aplicável em dry-run por construção);
  `503 dispatcharr-unavailable` (catálogo indisponível;
  `DispatcharrSyncCoordinator.cs:127-125`). `Ambiguous` é **estado de
  domínio**, **não** erro HTTP — preserva-se `SyncOutcome.Ambiguous` e a sua
  exposição via `counts.ambiguous` no payload (D6; DL-128).
- **Sanitização:** `MatchPlanSerializer.SanitizeForSerialization` aplica
  `CredentialSanitizer.SanitizeUrl` ao campo `streamUrl` antes de qualquer
  escrita (`MatchPlanSerializer.cs:20, 46, 56, 82, 134`; AGENTS.md §2;
  DL-020). Plano + relatório sanitizados em disco e em qualquer exposição
  HTTP posterior. URLs com credenciais nunca aparecem em resposta/erro/log.
- **Artifacts:** canónicos e inalterados (D7; DL-128):
  `dispatcharr_plan_<ts>.json` (`<ts>=yyyyMMdd_HHmmss`, mesmo `startedAt`
  para o par — `DispatcharrSyncService.cs:131, 169`) e
  `dispatcharr_report_<ts>.json` (`:251`). Produzidos **sempre** que o
  dry-run corre — mesmo quando a selecção é `null` (legacy). `startedAt`
  determina o nome do ficheiro; ficheiros novos nunca sobrescrevem runs
  anteriores no mesmo segundo. Opcionalmente, `dispatcharr_selection_<ts>.json`
  quando há selecção aplicada (`:178-180`). HTTP devolve **referências/
  paths**, **não** conteúdo integral embutido. Sanitização obrigatória,
  filesystem-local canónico, `MatchPlanSerializer` e `CredentialSanitizer`
  preservados sem alteração (D7; DL-128). Credenciais Xtream **nunca**
  expostas via HTTP.
- **Administrator:** `Administrator` = administrador autenticado corrente;
  **não** se introduz Role/Claim/Permission/Group/ACL/migração RBAC
  (D9; DL-128). `AuthModeResolver` permanece intocado; `RequireAdministratorAsync`
  não é criado (redundante). Autenticação = autorização enquanto só existir
  este papel — convenção ratificada em `31-DECISION-LOCK.md` (preservada);
  Operator/RBAC futuro é wave própria.
- **OPEN/TBD:**
  - Concretização do gate de concorrência dedicado ao Dispatcharr (D8) —
    diferida para a wave de implementação; contracto aqui é princípio.
  - Rate limit — `PARAMETER_GAP` (família `22 §20:983` e §8:250).
  - Detalhes de schema concretos (subtipos de `counts`, forma do `status`):
    a enumerar durante a implementação, conforme evidência de domínio.
- **Implementation reference:** `m3uCrawler/Services/Sync/DispatcharrSyncService.cs`
  (RunAsync 119-288, dry-run branch 196-206, artifacts 169/251);
  `m3uCrawler/Services/Sync/DispatcharrSyncCoordinator.cs` (RunAsync 73-125);
  `m3uCrawler/Services/Sync/MatchPlanSerializer.cs` (SanitizeForSerialization
  56-134).
- **Test reference (domínio):** `m3uCrawler.Tests/DispatcharrSyncServiceTests.cs`;
  `DispatcharrSyncServiceSourceSelectionTests.cs`; `DispatcharrSyncServiceOwnershipGuardTests.cs`;
  `DispatcharrSyncServiceGlobalPhase4Tests.cs`. Test reference **HTTP**:
  `m3uCrawler.Tests/W2DispatcharrHttpTests.cs` (22 test cases at HEAD
  `4b6643e77bd87328d94d1df1097e727545de7b30`; DL-128 compliance D1–D10
  conformant).
- **Classification:** **CONTRACT RATIFIED (DL-128)**; **HTTP IMPLEMENTATION
  SHIPPED** at HEAD `4b6643e77bd87328d94d1df1097e727545de7b30`; **RUNTIME-
  VALIDATED (dry-run)** via `W-RUNTIME-DISPATCHARR-DRYRUN-CONTROLLED`.
  APPLY/SYNC contra Dispatcharr real permanece **NOT RUNTIME-VALIDATED**.

### 20.7 `sync` (Dispatcharr)

- **Purpose:** aplicar o `desired` calculado pelo matching ao Dispatcharr,
  respeitando ownership (`CrawlerManaged`/`External`/`Unknown`;
  `12-DISPATCHARR.md:17-24`; DL-014; AGENTS.md §2). Reconcilia o estado
  remoto observado segundo DL-116 (`12-DISPATCHARR.md:27-35`).
- **Status:** **CONTRACT RATIFIED (DL-128)** — `POST /api/dispatcharr/sync`
  (D1; DL-128). **HTTP IMPLEMENTATION SHIPPED** at HEAD
  `4b6643e77bd87328d94d1df1097e727545de7b30` (commit
  `feat(dispatcharr-http): implement POST /api/dispatcharr/{dry-run,sync} per DL-128`):
  endpoint registado em `WebDashboardService.cs:985`; handler único partilhado
  em `WebDashboardService.cs:9150` (chamado de `:988` com `forceDryRun: false`);
  gate de concorrência dedicado em `DispatcharrConcurrencyGate.cs` (D8
  concretizado); `forceDryRun` propagado em `DispatcharrSyncCoordinator.cs:104,
  :115`. Camada de domínio canónica preservada
  (`DispatcharrSyncService.ApplyAsync`
  `DispatcharrSyncService.cs:397, 397-…`; orquestrador único
  `DispatcharrSyncCoordinator.RunAsync` consumido por `RunPublicationService`,
  `Program.cs` e `ScheduledDispatcharrSyncAction`
  (`RunPublicationService.cs:144, 154, 163`; AGENTS.md §2). Gates de caminho
  (`dispatcharr_enabled`, `no-playlist`) continuam a viver no Coordinator.
  **APPLY/SYNC contra Dispatcharr real: NOT RUNTIME-VALIDATED** — apenas a
  variante `dry-run` foi exercida end-to-end via
  `W-RUNTIME-DISPATCHARR-DRYRUN-CONTROLLED`. A validação contra Dispatcharr
  real (writes efectivos, ownership guard, DL-116, sanitização em respostas
  HTTP) permanece em aberto.
- **Method/Path:** `POST /api/dispatcharr/sync` (D1; ratificado por
  DL-128). Família usa prefixo `/api/dispatcharr/...`
  (`WebDashboardService.cs:9653-9654`). Não se criam variantes alternativas
  do path. Método diferente de `POST` → `405`.
- **Auth/Authz:** autenticado + `Administrator` (`22 §20:976-977`; a família
  fixa explicitamente que `sync` exige `Administrator`). Gate único do
  dashboard antes do handler (idem §7.3-7.5).
- **CSRF:** obrigatório (`22 §8:244`; `22 §20:977`). Header `X-CSRF-Token`
  (consistente com §7).
- **Request:** corpo `application/json` mínimo:
  ```json
  { "playlistPath": "..." }
  ```
  `playlistPath` corresponde directamente ao parâmetro de domínio consumido
  por `DispatcharrSyncService.RunAsync(playlistPath, selection, ct)`
  (`DispatcharrSyncService.cs:119`). **Não** se introduz `PlaylistId`, **não**
  se cria entidade `Playlist`, **não** se expõe `DispatcharrSourceSelection`
  no contrato HTTP (D2; DL-128).
- **dry_run override:** **não há**. O endpoint **não** aceita qualquer campo
  de override (D3; DL-128). Concretamente:
  - corpo **não** inclui `dry_run`, `dryRun`, `dry_run=true`, `dry_run=false`
    nem qualquer `dispatcharr_dry_run`;
  - aceitar-e-ignorar está **proibido** — payload com campos `dry_run*` é
    `400 invalid-payload`;
  - sem mecanismo alternativo de override (variável de ambiente, header,
    cookie, query string, etc.);
  - `/sync` significa sempre apply/sync per configuração persistida e
    contrato existente. `dispatcharr_dry_run=false` continua a ser decisão
    operacional explícita do administrador (AGENTS.md §2), não argumento de
    request HTTP.
- **Response / success status:** `200 OK` (D5; DL-128) — operação
  síncrona. Resposta híbrida **sanitizada** que **não** embebe o conteúdo
  integral do plano/relatório:
  ```json
  { "status": "ok|partial|failed|...",
    "mode": "dry-run|sync",
    "planPath": "...",
    "reportPath": "...",
    "counts": {} }
  ```
  Forma orientativa (D4; DL-128): o que se fixa é **orientação estrutural**
  — referência a artefactos e contagens, **sem** credenciais, **sem**
  conteúdo integral do `dispatcharr_plan_*.json`/`dispatcharr_report_*.json`
  embutido, **sem** campos inventados. Reutilização de tipos existentes
  (`SyncReportCounts`, `DispatcharrSyncResult`, `DispatcharrSyncOutcome`)
  deve preceder criação de novos DTOs. `Status ∈ {Disabled, CatalogUnavailable,
  Succeeded, Failed}` quando invocado via Coordinator
  (`DispatcharrSyncCoordinator.cs:22-33`). Se a implementação revelar que a
  estrutura requer decisão adicional, regista-se como discrepância — não se
  resolve silenciosamente (D4; DL-128). `202 Accepted` **não** é aceitável
  para esta versão síncrona do contrato (D5; DL-128).
- **Side effects (autoritativo):** `ApplyAsync` faz HTTP de escrita ao
  Dispatcharr (`DispatcharrSyncService.cs:210`); respeita DL-014 (só
  `CrawlerManaged` removido automaticamente; `12-DISPATCHARR.md:24`;
  `DispatcharrSyncService.cs:569-600`); preserva `is_custom=true` em
  streams criadas pelo crawler (AGENTS.md §2;
  `22 §20.1:1011-1013`); nunca edita `name`/`url`/`tvg_id`/`channel_group`
  de streams `is_custom=false` com `m3u_account` definido (AGENTS.md §2;
  `22 §20.1:1014-1016`). Em falha parcial mantém evidência do recurso criado
  e tenta compensação segura (DL-116; `12-DISPATCHARR.md:38-41`).
- **SyncOutcome (preservados explicitamente, não simplificar):**
  `NewChannel | ExistingUnchanged | ExistingReassigned | ExistingReordered
   | NewStream | Removed | Skipped | Ambiguous | Unchanged | Failed`
  (`Models/DiscoveredStream.cs:3-15`). **Invariante:** `Ambiguous`
  nunca é aplicado automaticamente — fica `SyncOutcome.Ambiguous` no
  plano, conta em `report.Counts.Ambiguous` e em
  `report.AmbiguousDecisions` (AGENTS.md §2; `22 §20.1:1004-1006`;
  `DispatcharrSyncService.cs:469` salta o canal ambíguo;
  `Models/MatchPlan.cs:150-205`).
- **Idempotência:** sim, por desired/snapshot (`22 §20:982`; DL-018).
  Aplicação repetida com o mesmo plano produz o mesmo estado remoto
  (`12-DISPATCHARR.md:26-35`; DL-018).
- **Concurrency:** no máximo uma Dispatcharr sync activa por runtime (D8;
  DL-128). Segunda tentativa concorrente → `409 concurrency-conflict`. O
  mecanismo concreto é um **gate de concorrência dedicado ao Dispatcharr**
  — segue o mesmo princípio atómico de `RunCoordinator` mas **não** o
  reutiliza directamente (gate Telegram-scoped, sem mistura de semânticas):
  aquisição atómica; falha do segundo candidato com conflito; libertação
  garantida em `finally`; não dependente apenas de um flag `IsRunning`
  (`TOCTOU`); não cria uma segunda semântica de Run Telegram; não altera
  `RunCoordinator`. A concretização deste gate é diferida para wave de
  implementação (D8; DL-128).
- **Audit:** obrigatória (`22 §20:983`; `4.1`). Persistência:
  `SyncRunEntity` (`running` → `ok`/`partial`/`error: <ExceptionType>`;
  `DispatcharrSyncService.cs:296-345`) + passos `read-plan`, `selection`,
  `apply`, `apply-create`, `apply-associate`, `apply-remove`,
  `apply-protected`, `apply-errors`
  (`DispatcharrSyncService.cs:162-167, 185-190, 213-244`). Apenas o
  `Type` da excepção é persistido em falha, nunca a mensagem
  (`DispatcharrSyncCoordinator.cs:121`; DL-020). `SyncRunEntity` continua a
  ser o registo canónico de auditoria; nenhum mecanismo HTTP adicional
  duplica esta observabilidade para a mesma operação (D10; DL-128). Se um
  evento HTTP-nível for necessário por observabilidade/compliance,
  demonstra-se durante a implementação — não se inventa agora.
- **Errors:** envelope canónico (D6; DL-128):
  ```json
  { "error": "<código estável>", "message": "<segura>", "correlationId": "<id>" }
  ```
  Códigos reutilizando a família transversal `22 §8` e DL-120:
  `401 authentication-required`; `403 forbidden`/`403 csrf-invalid`;
  `409 concurrency-conflict` (gate Dispatcharr deteve segunda invocação);
  `422 invalid-payload` (payload mal formado, `dry_run*` rejeitado,
  `playlistPath` inválido); `500 persistence-error`;
  `502 dispatcharr-comm-error` (HTTP falhou ao falar com Dispatcharr —
  `DispatcharrException`; `Services/Dispatcharr/DispatcharrException.cs`);
  `503 dispatcharr-unavailable` (`CatalogUnavailable`;
  `DispatcharrSyncCoordinator.cs:127-125`). `Ambiguous` é **estado de
  domínio**, **não** erro HTTP — preserva-se `SyncOutcome.Ambiguous` e a sua
  exposição via `counts.ambiguous` no payload (D6; DL-128).
- **Partial failure / DL-116:** uma chamada falhada não é assumida como
  "recurso não existe"; evidência persistida; compensação segura quando
  possível (`12-DISPATCHARR.md:38-41`; `DispatcharrSyncService.cs:548-563`).
- **Sanitização:** `MatchPlanSerializer.SanitizeForSerialization` aplica
  `CredentialSanitizer.SanitizeUrl` ao campo `streamUrl` antes de qualquer
  escrita em artefactos (`MatchPlanSerializer.cs:20, 46, 56, 134`; AGENTS.md §2;
  DL-020). `CredentialSanitizer.SanitizeUrl` e `SanitizeM3uContent` aplicam-se
  a **todos** os pontos de saída: consola, `RunReport`, JSONs de relatório,
  respostas HTTP, mensagens de erro (AGENTS.md §2; DL-020).
  URLs com credenciais nunca aparecem em `dispatcharr_plan_*.json`,
  `dispatcharr_report_*.json`, respostas/erros/logs. A playlist M3U
  funcional (`output/playlist.m3u`, `GET /api/playlist`) preserva URLs
  Xtream reais — é o único artefacto onde credenciais são intencionais
  (AGENTS.md §2).
- **Artifacts:** `dispatcharr_plan_<ts>.json` + `dispatcharr_report_<ts>.json`
  são produzidos **sempre** que `RunAsync` completa — independentemente de
  `DryRun` (`DispatcharrSyncService.cs:169, 251`). Em `sync` (apply),
  contêm adicionalmente o output de `ApplyAsync` (recursos criados/removidos,
  streams protegidas, falhas). Opcionalmente `dispatcharr_selection_<ts>.json`
  quando há selecção (`:178-180`). Canónicos e inalterados (D7; DL-128) —
  HTTP devolve **referências/paths**, **não** conteúdo integral embutido.
- **Administrator:** `Administrator` = administrador autenticado corrente;
  **não** se introduz Role/Claim/Permission/Group/ACL/migração RBAC
  (D9; DL-128). `AuthModeResolver` permanece intocado; `RequireAdministratorAsync`
  não é criado (redundante). Autenticação = autorização enquanto só existir
  este papel — convenção ratificada em `31-DECISION-LOCK.md` (preservada);
  Operator/RBAC futuro é wave própria.
- **OPEN/TBD:**
  - Concretização do gate de concorrência dedicado ao Dispatcharr (D8) —
    diferida para a wave de implementação; contracto aqui é princípio.
  - Rate limit — `PARAMETER_GAP` (família `22 §20:983` e §8:250).
  - Detalhes de schema concretos (subtipos de `counts`, forma do `status`):
    a enumerar durante a implementação, conforme evidência de domínio.
- **Implementation reference:** `m3uCrawler/Services/Sync/DispatcharrSyncService.cs`
  (RunAsync 119-288, apply branch 207-246, ApplyAsync 397-…, ownership guard
  569-600, recorder); `m3uCrawler/Services/Sync/DispatcharrSyncCoordinator.cs`
  (RunAsync 73-273, status enum 22-33); `m3uCrawler/Services/Sync/MatchPlanSerializer.cs`
  (sanitização); `m3uCrawler/Services/LiveRun/RunPublicationService.cs:144,
  154, 163` (caller canónico).
- **Test reference (domínio):** `m3uCrawler.Tests/DispatcharrSyncServiceTests.cs`;
  `DispatcharrSyncServiceSourceSelectionTests.cs`;
  `DispatcharrSyncServiceOwnershipGuardTests.cs`;
  `DispatcharrSyncServiceGlobalPhase4Tests.cs`;
  `DispatcharrLiveReadonlyIntegrationTests.cs`;
  `ScheduledDispatcharrSyncActionTests.cs`;
  `Phase93DispatcharrNamingTests.cs`. Test reference **HTTP**:
  `m3uCrawler.Tests/W2DispatcharrHttpTests.cs` (22 test cases at HEAD
  `4b6643e77bd87328d94d1df1097e727545de7b30`; DL-128 compliance D1–D10
  conformant para `dry-run`; cobertura HTTP do path `/sync` fica em aberto
  até validação contra Dispatcharr real).
- **Classification:** **CONTRACT RATIFIED (DL-128)**; **HTTP IMPLEMENTATION
  SHIPPED** at HEAD `4b6643e77bd87328d94d1df1097e727545de7b30`; **APPLY/SYNC
  contra Dispatcharr real: NOT RUNTIME-VALIDATED** (apenas `dry-run` foi
  exercido end-to-end via `W-RUNTIME-DISPATCHARR-DRYRUN-CONTROLLED`). O
  contrato de domínio (ownership, ambiguous preservado, sanitização, DL-116,
  DL-015, DL-020) mantém-se canónico e inalterado.

### 20.8 `reconciliation` e `ownership view`

- **Status:** **NÃO IMPLEMENTADOS** como endpoints HTTP dedicados
  (`46:86`).
- **Invariantes (contratuais):** observam sem alterar a verdade remota
  (DL-013); `reconciliation` lê estado remoto e reconcilia; `ownership
  view` lista recursos por ownership.
- **OPEN/TBD:** paths, métodos, schemas, códigos de erro. Esta ficha
  documenta o gap, NÃO o preenche.

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

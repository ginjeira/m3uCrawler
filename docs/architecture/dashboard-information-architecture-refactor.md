# Dashboard — Estudo e Proposta de Refactorização da Arquitectura de Informação

> Status: proposta (não normativa) · Data: 2026-10-09 · Âmbito: dashboard (`WebDashboardService.cs`)

Este documento é um **estudo/proposta** de UI/UX. Não é normativo, não altera
contratos nem endpoints e não deve ser lido como decisão de implementação. O
comportamento actual da aplicação continua descrito em
`m3uCrawler/README.md` — aqui referenciado por cross-reference, sem duplicação.

---

## 1. Sumário executivo

O dashboard actual mistura, no mesmo ecrã, elementos **informativos**
(monitorização, estado, métricas) com elementos de **configuração**, e distribui
a configuração por vários menus e sub-menus sem uma casa canónica por domínio.
O resultado é dispersão (o mesmo domínio aparece em 2–4 locais) e ambiguidade de
nomes ("Sources" vs "Source Selection" vs "Source Priority" vs "Canais").

A proposta em uma frase: **reorganizar a navegação por natureza (INFO / AÇÃO /
DADOS / CONFIG), dar a cada domínio de configuração um único editor canónico
(com precedência global → job → run explícita) e agregar as ações one-shot numa
área "Operações"**.

O ganho: um operador encontra cada coisa no mesmo sítio independentemente da
tarefa, reduz-se a duplicação de lógica de validação no cliente e o crescimento
futuro do dashboard passa a ter regras de arrumação claras.

---

## 2. Problema

### 2.1 Dores

- **Dor A — info e config misturados.** Ecrãs como *Setup*, *Descoberta*,
  *Canais/Países*, *Validação de Streams*, *Execução ao Vivo* e *Catálogo* contêm,
  lado a lado, formulários de configuração persistida e painéis de leitura
  (estado, métricas, resultados de validação). O utilizador não distingue "o que
  edito" de "o que observo".
- **Dor B — configuração espartilhada.** O mesmo domínio de configuração vive em
  locais diferentes conforme o ecrã: Dispatcharr tem credenciais no *Setup*,
  ações/estado na vista *Dispatcharr* e factores no *Catálogo*; discovery tem
  global em *Descoberta*/*Execução ao Vivo*, por-job em *Scheduled Jobs* e por-run
  no *Run now*; "país" aparece em 4 locais. Sem casa canónica, qualquer alteração
  de regra de validação tem de ser replicada em vários pontos.

### 2.2 Tabela de incoerências (evidência)

Referências em `m3uCrawler/Services/WebDashboardService.cs` (linhas aproximadas;
confirmadas na revisão do código).

| # | Incoerência | Evidência (`file:line`) |
|---|---|---|
| I1 | Configuração do Dispatcharr no *Setup*, ações/estado na vista *Dispatcharr*, factores de matching no *Catálogo* | `:6788-6851` (config Setup) · `:5731-5746` (ações/estado) · `:6216`, `:6298`, `:6323`, `:6586` (factores) |
| I2 | Discovery em 3 sítios (global / job / run) com validação replicada cliente+servidor | global `:5646`, `:7234` · job `:6480`, `:5239` · run `:6694`, `:10830` |
| I3 | Métricas (overview/matching/degradation/audit/syncruns) dentro do guarda-chuva de CRUD `view-catalog` | `:5755-5772` |
| I4 | Ações one-shot espalhadas por vários ecrãs | Dispatcharr `:5735-5736` · Validação `:6668` · Live Run `:6691` · Setup testar `:6848` · previews `:6384`, `:8603` |
| I5 | "País" em 4 locais distintos | `:5690`, `:5828`, `:5953`, `:6637` |
| I6 | Agendamento (config) dentro do *Catálogo*, longe do runtime (*Live Run* deep-link) | `:6433-6548` (scheduled) · `:6733-6741` (deep-link) |
| I7 | Separador global de variantes (`/api/settings`) dentro do tab *Afinidades* | `:5937-5944` |
| I8 | Três vistas INFO sobre os mesmos dados de run (overview / executions / diagnostics) | `:5622`, `:5632`, `:6675` |
| I9 | *Import Policies* oculto mas vivo (endpoints e entidade mantidos) | `:5767`, `:2855` |
| I10 | Config semântica persistida como entidade SQLite (priority / source selection / import policies) | priority `:2374` · source selection `:2405` · import policies `:2879` |
| I11 | Nomes ambíguos: "Sources" vs "Source Selection" vs "Source Priority" vs "Canais/Países" vs "Canais" | nav `:5611` vs tabs `:5756-5762` |
| I12 | *Setup* agrega 3 domínios (Telegram, Dispatcharr, password) + teste de ligação | `:6788-6851` |
| I13 | Estado Dispatcharr/Publicação infiltrado na *Visão Geral* | `:6965-6971` |
| I14 | *Executions* vs *Diagnostics* com fontes próximas mas rotas distintas | `:6681` vs `:5639` · `/api/run-report` vs `/api/execution/{idx}` |

---

## 3. Estado actual (inventário)

### 3.1 Navegação de topo (11 vistas)

| Vista (`data-view`) | Etiqueta | Natureza | Nota |
|---|---|---|---|
| `setup` | Setup | CONFIG+INFO+AÇÃO | Telegram, Dispatcharr, password, teste de ligação |
| `overview` | Visão Geral | INFO | inclui estado Dispatcharr/Publicação (I13) |
| `executions` | Execuções | INFO | histórico 72h |
| `discovery` | Descoberta | CONFIG+INFO | config global + tabela de candidatos |
| `countries` | Canais / Países | CONFIG+INFO | ficheiros de país + validação da playlist |
| `playlist` | Playlist | INFO | artefactos e preview |
| `dispatcharr` | Dispatcharr | AÇÃO+INFO | dry run / sync + estado |
| `catalog` | Catálogo | MISTO | guarda-chuva de CRUD + métricas (I3) |
| `validation` | Validação de Streams | CONFIG+AÇÃO | política + testar URLs |
| `liverun` | Execução ao Vivo | AÇÃO+CONFIG+INFO | Run now + overrides + polling |
| `diagnostics` | Diagnóstico | INFO | RunReport bruto, glossário |

### 3.2 Sub-tabs do Catálogo (17)

| Sub-tab (`data-ctab`) | Etiqueta | Natureza |
|---|---|---|
| `overview` | Visão Geral | INFO |
| `channels` | Canais | DADOS/CRUD |
| `rules` | Regras | DADOS/CRUD |
| `affinity` | Afinidades | DADOS/CRUD (+CONFIG em `:5937`) |
| `sources` | Sources | DADOS/CRUD |
| `ordering` | Ordering | CONFIG |
| `priority` | Source Priority | CONFIG (persistida como entidade) |
| `sourceselection` | Source Selection | CONFIG (persistida como entidade) |
| `matching` | Matching | INFO |
| `degradation` | Degradação | INFO |
| `scheduled` | Scheduled Jobs | CONFIG (+overrides discovery) |
| `policies` | Import Policies | CONFIG (oculto, I9) |
| `groups` | Grupos | DADOS/CRUD |
| `reviews` | Reviews | DADOS/CRUD |
| `audit` | Auditoria | INFO |
| `syncruns` | Sync Runs | INFO |
| `pending` | Pending | DADOS/CRUD (ação de aprovação) |

### 3.3 Domínios de configuração e onde aparecem

| Domínio | Global | Por job | Por run | Onde aparece hoje |
|---|---|---|---|---|
| Discovery (keyword, Min/Max, maxStreams) | `app_settings.json#discovery` | `scheduled_jobs.DiscoveryJson` | overrides do Live Run | Descoberta, Scheduled Jobs, Live Run |
| Dispatcharr (enabled, base_url, creds, dry_run, threshold, auto_create_groups, provider_priority, alias_file) | `wtelegram.config` | — | — | Setup (config) + vista Dispatcharr (ações/estado) |
| Validação de streams (concorrência/TTL/early-exit) | política persistida | — | testes ad-hoc | Validação de Streams |
| País (aliases, indicadores, canais) | `runtime-data/countries/*.json` + entidades catálogo | — | validação on-demand | Canais/Países, Afinidades, Catálogo, Validação |
| Agendamento (cron, action, enabled) | — | `scheduled_jobs` | — | Catálogo → Scheduled Jobs |
| Segurança (password do dashboard) | credencial do dashboard | — | — | Setup |

---

## 4. Princípios de desenho

1. **Separação por natureza.** Cada ecrã/área declara uma natureza dominante:
   CONFIG, DADOS/CRUD, INFO/MONITOR ou AÇÃO. Não se misturam formulários de
   configuração persistida com painéis de leitura no mesmo bloco visual.
2. **Um domínio de configuração = um único editor canónico.** A escrita de um
   domínio existe num só local. Todos os restantes locais que hoje o editam
   passam a **read-only** com link para o editor canónico.
3. **Precedência explícita.** Onde há overrides, a UI mostra sempre
   `global (default) → job (override) → run (override)` e o **efetivo**.
4. **Navegação rasa.** No máximo dois níveis (área → página). Sub-tabs profundos
   só quando pertencem à mesma entidade.
5. **Nomes não ambíguos.** Eliminar colisões ("Sources" vs "Source Selection" vs
   "Source Priority"); cada nome indica o objecto que edita.
6. **Ações junto do contexto, agregadas numa área "Operações".** Ações one-shot
   (Run now, Dry Run, Sync, testar ligação, testar URLs, validar país) ficam
   acessíveis no contexto mas visíveis e agrupadas numa área **Operações**.
7. **Configuração vs dados.** Distinguir *definições* (ficheiros de configuração
   e entidades de definição/política) de *entidades de catálogo* (canais, grupos,
   regras). Não tratar políticas como dados de catálogo nem o inverso.

---

## 5. Proposta de IA (target)

### 5.1 Nova navegação de topo (6 áreas)

| Área | Natureza | Páginas / sub-navegação |
|---|---|---|
| **Painel** | INFO | Visão geral do sistema; estado de publicação e da sync Dispatcharr (sem config) |
| **Operações** | AÇÃO | Run now (Live Run), Dry Run/Sync Dispatcharr, Testar ligação, Testar URLs, Validar país, histórico recente de ações |
| **Monitorização** | INFO | Execuções, Diagnóstico, Matching, Degradação, Auditoria, Sync Runs |
| **Catálogo** | DADOS/CRUD | Canais, Regras, Afinidades, Sources, Grupos, Reviews, Pending — com filtro interno por natureza (Config \| Dados \| Monitor) se se mantiverem juntos |
| **Configuração** | CONFIG | Hub com uma página por domínio (ver 5.2) |
| **Setup** | CONFIG (inicial) | Primeira configuração + prontidão (checklist), separado da Configuração contínua |

> **Nota (Fase 3 — implementada na UI `/next`, 2026-10-09).** A variante `Next`
> ganha duas áreas de topo: **Operações** (`data-view='operations'`) e
> **Monitorização** (`data-view='monitoring'`), ambas `<section>` **sibling** de
> `main` e ocultas por omissão. A **Visão Geral** mantém-se (não foi renomeada
> para "Painel" nem alterada nesta fase).
>
> **Operações** agrega as ações one-shot — Run now (`opRunNow`), Dry Run/Sync/
> Testar ligação Dispatcharr (`opDispatcharrDryRun`/`opDispatcharrSync`/
> `opDispatcharrTest`) e Validar país (`opValidateCountry`), que navegam para o
> contexto onde ficam o estado e o feedback — e recebe o card **Testar URLs**
> (textarea `validationTestUrls` + `runValidationTest()`), movido de
> `view-validation`; no local original fica uma nota/atalho
> (`nextGotoOperationsValidation`).
>
> **Monitorização** recebe os 4 contentores INFO do Catálogo — `#ctab-matching`,
> `#ctab-degradation`, `#ctab-audit`, `#ctab-syncruns` são movidos de
> `view-catalog` e renomeados para `#mont-matching`/`#mont-degradation`/
> `#mont-audit`/`#mont-syncruns` (os ids internos das tabelas/loaders **não**
> mudam) — sob uma nova `<nav id='monitoringTabs'>` (`data-monttab`). Os
> respetivos botões `data-ctab` saem do `#catalogTabs` e `view-catalog` fica com
> uma nota/atalho (`nextGotoMonitoring`). O wiring (`montTab`/`setMontActive`) e
> os loaders exportados são injectados apenas na `Next`.
>
> **Pendente:** *aliviar a Visão Geral* do estado de sync Dispatcharr/publicação
> (I13) — **nenhum** card foi removido da Visão Geral nesta fase. A variante
> `Legacy` (`GET /`) continua **byte-idêntica**.

### 5.2 Hub de Configuração — uma página por domínio

- **Integrações** — Telegram (credenciais/sessão) e Dispatcharr (credenciais e
  parâmetros de ligação).
- **Descoberta** — configuração global + precedência explícita job/run.
- **Selecção & Prioridade de Fontes** — unifica "Source Priority" e
  "Source Selection" sob um único domínio, com nomes distintos para cada objecto.
- **Ordenação & Publicação** — Ordering Lists + Grupos de publicação.
- **Validação de Streams** — política de validação.
- **Canais & Países** — definição de canais/países e indicadores (o editor de
  definição; a validação on-demand vive em Operações).
- **Agendamento** — Scheduled Jobs (cron/action/enabled) + overrides de discovery.
- **Segurança** — password do dashboard.

> **Nota (Fase 2 — implementada na UI `/next`, 2026-10-09).** O hub
> **Configuração** já existe na variante `Next`, com sub-tabs **Descoberta /
> Validação de Streams / Publicação / Agendamento / Segurança**. Foram movidos
> para lá, como editores canónicos, o card da **configuração global de
> discovery** (`view-discovery`), a **política de validação** (`view-validation`)
> e o card de **alteração de password** (`view-setup`); nos locais originais
> ficam notas com atalhos (a tabela de candidatos e o dry-run permanecem). A
> página **Descoberta** inclui o bloco de precedência **global → job → run**
> (apresentação apenas; a validação de limites permanece no servidor).
> **Publicação** e **Agendamento** são, nesta fase, **páginas-índice
> (read-only)** que ligam aos editores actuais do Catálogo (Ordering, Selecção
> de Fontes, Prioridade de Fontes, Grupos, Scheduled Jobs) — a movimentação
> completa fica para uma fase posterior. A variante `Legacy` (`GET /`) continua
> **byte-idêntica**.

### 5.3 Workspace Dispatcharr (resolve a dispersão I1)

Um único local reúne **config + ações + estado**:

- **Config** (canónico): enabled, base_url, credenciais, dry_run,
  match_threshold, auto_create_groups, provider_priority, alias_file.
- **Ações**: Dry Run, Sync.
- **Estado**: último MatchPlan, resumo de classificação, detalhes da última sync.
- **Referências** (read-only) a source selection/priority e ordering, com link
  para os respectivos editores canónicos — sem os duplicar.

> **Nota (Fase 1 — implementada na UI `/next`, 2026-10-09).** O *workspace
> Dispatcharr* já existe na variante `Next`: o card de configuração do Dispatcharr
> foi movido do *Setup* para a vista **Dispatcharr** (junto às ações Dry Run/Sync
> e ao estado), o *Setup* passa a ter apenas um atalho ("Abrir Dispatcharr"), foi
> acrescentado um card read-only "Factores que determinam o plano" com atalhos
> para Selecção de Fontes / Prioridade de Fontes / Ordenação no Catálogo, e a
> variante `Next` injecta um `<script>` de wiring mínima (`window.loadSetup`).
> A variante `Legacy` (`GET /`) permanece **byte-idêntica**.

### 5.4 Mapeamento old → new

| Vista/sub-tab actual | Nova localização |
|---|---|
| `setup` (Telegram) | Setup → Integrações → Telegram |
| `setup` (Dispatcharr config) | Configuração → Integrações → Dispatcharr (ou workspace Dispatcharr) |
| `setup` (password) | Configuração → Segurança |
| `setup` (testar ligação) | Operações → Testar ligação |
| `overview` (INFO geral) | Painel → Visão geral |
| `overview` (estado Dispatcharr/publicação) | Painel → Estado de publicação e sync (read-only) |
| `executions` | Monitorização → Execuções |
| `discovery` (config global) | Configuração → Descoberta |
| `discovery` (tabela de candidatos) | Painel/Catálogo → Candidatos (INFO) |
| `countries` (definição) | Configuração → Canais & Países |
| `countries` (validar playlist) | Operações → Validar país |
| `playlist` | Painel → Playlist / artefactos |
| `dispatcharr` (ações/estado) | Operações + Painel; config no workspace Dispatcharr |
| `catalog → channels/rules/affinity/sources/groups/reviews/pending` | Catálogo (inalterado, filtro por natureza) |
| `catalog → ordering` | Configuração → Ordenação & Publicação |
| `catalog → priority` | Configuração → Selecção & Prioridade de Fontes |
| `catalog → sourceselection` | Configuração → Selecção & Prioridade de Fontes |
| `catalog → scheduled` | Configuração → Agendamento |
| `catalog → policies` | Configuração → (Import Policies, decisão em aberto) |
| `catalog → matching/degradation/audit/syncruns` | Monitorização |
| `validation` (política) | Configuração → Validação de Streams |
| `validation` (testar URLs) | Operações → Testar URLs |
| `liverun` (Run now) | Operações → Run now |
| `liverun` (overrides/precedência) | Configuração → Descoberta (precedência) |
| `diagnostics` | Monitorização → Diagnóstico |

### 5.5 Herança de discovery (global → job → run)

Na página **Descoberta**, cada parâmetro apresenta três estados:

1. **Default global** — valor de `app_settings.json#discovery`.
2. **Override** — valor definido no job (ou no run), quando presente.
3. **Efetivo** — o valor que será usado, derivado da cadeia de precedência.

O mesmo componente de apresentação é reutilizado nos Scheduled Jobs e no Run now,
de modo a que a regra de herança seja **uma só**, visível em todos os locais, em
vez de replicada em cada formulário.

---

## 6. UX de precedência (discovery)

- O cliente **não duplica** regras de validação. A validação de limites
  (Min ≥ 0, Max 1–1440h, Min ≤ Max, maxStreams ≥ 1) permanece no servidor; o
  cliente apenas **apresenta** o resultado (default/override/efetivo) e sinaliza
  campos inválidos por eco da resposta do servidor.
- O comportamento de normalização (ex.: inversão `Min > Max` normalizada) é
  propriedade do servidor e é refletido, não reimplementado.
- Cada campo mostra um badge de origem: `global`, `override(job)`, `override(run)`.
- Alterar um override no job/run não altera o global; remover o override volta a
  herdar o global. A UI deve tornar esta reversibilidade óbvia.
- Cross-reference: ver `m3uCrawler/README.md` § "Janela de histórico da pesquisa
  Telegram (Min/Max)" e § "Overrides de discovery por job" para a semântica
  normativa.

---

## 7. Roadmap faseado

### Fase 0 — Reagrupar sub-tabs do Catálogo + limpeza de nomes

- **Âmbito:** só front-end (HTML/CSS/JS dentro da string C#). Agrupar visualmente
  os 17 sub-tabs por natureza (Dados \| Config \| Monitor) e desambiguar nomes
  ("Sources" vs "Source Selection" vs "Source Priority").
- **Ficheiros prováveis:** `m3uCrawler/Services/WebDashboardService.cs` (nav dos
  sub-tabs `:5755-5772` e respetivo JS).
- **Testes afectados:** `WebDashboardHtmlTests`, `WebDashboardHtmlAuditTests`,
  `WaveDC5bAuditUiHtmlTests`, `DashboardJsSmokeTests`.
- **Critério de aceitação:** todos os sub-tabs continuam acessíveis e funcionais;
  nenhum endpoint muda; testes de caracterização actualizados e verdes.

### Fase 1 — Consolidar Config do Dispatcharr (workspace Dispatcharr)

- **Âmbito:** mover a config Dispatcharr do *Setup* para um **workspace
  Dispatcharr** (config + ações + estado), referenciando source
  selection/priority/ordering como read-only/links.
- **Ficheiros prováveis:** `WebDashboardService.cs` (bloco Setup `:6788-6851`,
  vista Dispatcharr `:5731-5746`, handlers JS associados).
- **Testes afectados:** `WebDashboardSetupHtmlTests`, `WebDashboardHtmlTests`,
  `WebDashboardOverviewPublicationStatusHtmlTests`, `DashboardJsSmokeTests`.
- **Critério de aceitação:** um único editor canónico da config Dispatcharr;
  endpoints preservados; nenhum segredo exposto (invariante de sanitização).
- **Estado:** **implementada na UI `/next` (2026-10-09).** `ApplyNextVariant` move
  o card de config do `view-setup` para o `view-dispatcharr` (bloco exacto,
  ids/handlers preservados), substitui-o no Setup por um atalho, acrescenta o
  card read-only dos factores do plano e injecta um `<script>` de wiring só na
  `Next`. A `Legacy` mantém-se byte-idêntica (`DashboardNextVariantTests`).

### Fase 2 — Hub Configuração (mover Settings dispersos; discovery com precedência)

- **Âmbito:** criar o hub com uma página por domínio; mover config global de
  discovery, validação de streams, ordenação/publicação, agendamento e segurança;
  implementar a UX de precedência (secção 6).
- **Ficheiros prováveis:** `WebDashboardService.cs` (secções `view-discovery`
  `:5642-5687`, `view-countries` `:5689-5709`, `view-validation`,
  `ctab-ordering/priority/sourceselection/scheduled` `:6433-6548`).
- **Testes afectados:** os de caracterização + testes de discovery se existirem
  no cliente; verificar que a validação permanece no servidor.
- **Critério de aceitação:** cada domínio com editor único; restantes locais
  read-only/links; precedência visível (default/override/efetivo) sem lógica de
  validação duplicada no cliente.
- **Estado:** **implementada na UI `/next` (2026-10-09).** `ApplyNextVariant`
  acrescenta o nav de topo **Configuração** e a secção `view-config` (sibling de
  `<main>`, cinco sub-páginas). Move o card de **discovery global**, a **política
  de validação** e a **password** (blocos exactos; ids/handlers preservados),
  deixando notas/atalhos nos locais originais, e inclui o bloco de precedência
  global → job → run. **Publicação** e **Agendamento** são, nesta fase,
  páginas-índice read-only que ligam aos editores actuais do Catálogo. O wiring e
  os loaders são injectados apenas na `Next`; a `Legacy` mantém-se byte-idêntica
  (`DashboardNextVariantTests`).

### Fase 3 — Área Operações + separar Monitorização

- **Âmbito:** agregar ações one-shot (Run now, Dry Run/Sync, testar ligação,
  testar URLs, validar país); mover métricas (matching/degradation/audit/syncruns)
  para Monitorização; aliviar a *Visão Geral* de estado de sync.
- **Ficheiros prováveis:** `WebDashboardService.cs` (blocos de ação `:5735-5736`,
  `:6668`, `:6691`, `:6848`; tabs de métricas em `view-catalog`).
- **Testes afectados:** `WebDashboardHtmlTests`, `WaveDC5bAuditUiHtmlTests`,
  `WaveDC7Dc12DashboardUiHtmlTests`, `DashboardJsSmokeTests`.
- **Critério de aceitação:** ações encontram-se no seu contexto e na área
  Operações; Monitorização sem formulários de configuração.
- **Estado:** **implementada na UI `/next` (2026-10-09).** `ApplyNextVariant`
  acrescenta o nav de topo **Operações** e **Monitorização** e duas secções
  `main > section` ocultas. **Operações** agrupa as ações one-shot (Run now, Dry
  Run/Sync/Testar ligação, Testar URLs, Validar país); o card **Testar URLs**
  (`validationTestUrls`) foi movido de `view-validation` para `#view-operations`
  (nota/atalho `nextGotoOperationsValidation` no local original, ids/handlers
  preservados). **Monitorização** recebe os 4 contentores INFO do Catálogo
  (`ctab-matching`/`ctab-degradation`/`ctab-audit`/`ctab-syncruns` renomeados
  para `mont-*`; ids internos das tabelas/loaders inalterados) sob a nova
  `#monitoringTabs`; os botões `data-ctab` respetivos saem do `#catalogTabs` e o
  `view-catalog` fica com nota/atalho `nextGotoMonitoring`. O wiring e os
  loaders exportados (`loadMatchingAudits`/`loadDegradation`/`loadCatalogAudits`/
  `loadCatalogSyncRuns`) são injectados só na `Next`. A `Legacy` mantém-se
  byte-idêntica (`DashboardNextVariantTests`).
  **Pendente (não nesta fase):** *aliviar a Visão Geral* do estado de sync
  Dispatcharr/publicação (I13) — nenhum card foi removido da Visão Geral.

### Fase 4 — Redução/cisão de `WebDashboardService.cs` (técnica; recomendação futura)

- **Âmbito:** **apenas recomendação**. O ficheiro único (~12 900 linhas) concentra
  HTML/JS e handlers. Cisão futura em parciais ou recursos por área, sem alterar
  contratos. Não é pré-requisito das fases 0–3.
- **Ficheiros prováveis:** `WebDashboardService.cs` e novos parciais/recursos.
- **Testes afectados:** todos os de dashboard (comportamento inalterado).
- **Critério de aceitação:** comportamento idêntico, 0 warnings/0 errors, testes
  verdes; sem alteração de endpoints.

---

## 8. Implementação paralela e cutover

Esta secção responde a uma questão operacional que a proposta deixa em aberto:
como levar a nova IA a produção sem arriscar a UI actual. A estratégia é
**implementar em paralelo e só decidir a substituição depois de testar**, com
uma **única string de UI** parametrizada por variante (**estratégia A**), em vez
de uma cópia integral do builder.

### 8.1 Objectivo

- Implementar a nova IA **em paralelo** com a actual, coexistindo as duas.
- Só substituir a UI actual (cutover) depois de a nova passar os testes.
- A UI actual permanece **intacta** e funciona como **fallback/rollback** durante
  toda a transição.
- Âmbito estritamente **UI**: nenhum endpoint, contrato ou lógica de domínio muda.

### 8.2 Mecanismo (evidência no código)

Tudo é servido pelo mesmo `HttpListener` em
`m3uCrawler/Services/WebDashboardService.cs`:

- `GET /` → `BuildHtmlPage(csrfToken)` (`:4633`) → `BuildDashboardHtml()` (`:5509`);
- o root tem tratamento próprio com gate de sessão (`:3746-3762`).

**Estratégia A — builder parametrizado por variante** (implementada na Fase 0):

1. **Variante na mesma string.** `BuildDashboardHtmlFor(DashboardVariant)`
   devolve a string base **tal como está** para `Legacy` e aplica
   `ApplyNextVariant(baseHtml)` para `Next`. Não há cópia da UI: existe **uma
   única string, um único ponto de manutenção**. `BuildDashboardHtml()` (sem
   argumentos, preservado para os testes por reflexão) delega em
   `BuildDashboardHtmlFor(Legacy)`; `BuildHtmlPageFor(csrfToken, variant)` reusa
   a mesma injecção de CSRF de `BuildHtmlPage`.
2. **Nova rota aditiva `GET /next`**, tratada como o `/` (mesmo gate de sessão +
   injecção de CSRF via `BuildHtmlPageFor(rootSession.CsrfToken, Next)`), sem
   tocar no comportamento do `/`.
3. **`/` = Legacy inalterado**, guardado por testes de caracterização
   (`BuildDashboardHtml()` == `BuildDashboardHtmlFor(Legacy)`, byte a byte).
4. **Cutover reversível.** Trocar a variante servida em `/` (uma alteração de UI)
   e manter `/next` (ou um alias `/legacy`) para rollback; opcionalmente uma flag
   de seleção (`dashboard_ui=legacy|next`). A flag é **opcional** e, quando
   introduzida, já **não é "só UI"** (introduz estado de configuração) —
   assinalado por isso como não preferencial.

### 8.3 Isolamento

- `ApplyNextVariant` faz **apenas transformações de navegação** (nav de topo,
  sub-tabs do Catálogo, regra CSS `.ctab-group` e link de header): os **corpos de
  vista e o JS ficam intactos e partilhados** — não há duplicação de helpers
  (`apiRequest`, `escapeHtml`, `tsLocal`, modal).
- A substituição é feita por **blocos exactos** (os blocos actuais copiados como
  literais). Se `BuildDashboardHtmlFor(Legacy)` divergir da string actual, é
  bug — coberto por teste.

### 8.4 Testes duplos

- Testes de **caracterização próprios** para a variante `Next` (HTML).
- Como o JS é **partilhado** (não duplicado), os casos do harness Jint
  (`DashboardJsSmokeTests`) cobrem as duas variantes sem alterações.
- Os testes da **legacy ficam verdes e inalterados**; o CI passa a validar as
  duas.

### 8.5 Critérios de cutover (Definition of Done do paralelo)

- **Paridade funcional** das tarefas cobertas (config, operações, catálogo,
  monitorização).
- **Smoke E2E manual** em `/next`.
- **Zero regressões** na legacy.
- Invariantes de **sanitização** preservados.
- **Endpoints inalterados.**

### 8.6 Fases 0–3 aditivas

As fases 0–3 passam a ser **aditivas**: executam-se na variante `Next`; a
variante `Legacy` (`GET /`) **permanece byte-idêntica até ao cutover** e é
servida pela mesma string, pelo que não há deriva de código entre as duas.

### 8.7 Riscos e mitigações

- O ponto de routing é uma edição aditiva num **ficheiro sensível**
  (`WebDashboardService.cs`, `AGENTS.md` §3) — manter mínima e isolada.
- **Substituições por blocos exactos em `ApplyNextVariant`**: se a string base
  mudar, uma substituição pode deixar de corresponder silenciosamente. Mitigado
  por testes que exigem os marcadores da variante `Next` (`class='ctab-group'`,
  rótulos novos) e por um teste de **identidade byte a byte** da `Legacy`.
- **Manutenção única** (sem duplicação de helpers): as duas variantes partilham
  os mesmos corpos e JS; só a navegação difere.

### 8.8 Alternativa sem rota nova

Não existe forma de servir uma segunda UI **sem um ponto de routing**; a rota
aditiva é a opção mínima que cumpre "só UI".

---

## 9. Impacto técnico e testes

- **Tudo é front-end.** O HTML/JS vive numa string C# dentro de
  `WebDashboardService.cs`; não há framework de UI. A refactorização é de
  marcação, CSS e funções JS no cliente.
- **Endpoints mantêm-se.** Nenhum contrato muda; os endpoints podem ser
  **agrupados por navegação** mas não renomeados nem removidos.
- **Testes de caracterização a actualizar:** `WebDashboardHtmlTests`,
  `WebDashboardHtmlAuditTests`, `WaveDC5bAuditUiHtmlTests`,
  `DashboardJsSmokeTests` (e, conforme as fases, `WebDashboardSetupHtmlTests`,
  `WebDashboardOverviewPublicationStatusHtmlTests`,
  `WaveDC7Dc12DashboardUiHtmlTests`). Estes testes verificam marcação/estrutura;
  alterações de IA obrigam a actualizá-los.
- **Nota sobre ficheiro único.** O tamanho de `WebDashboardService.cs` agrava o
  risco de edição e de conflitos; a Fase 4 endereça-o, mas é deliberadamente
  adiada por não ser necessária ao ganho funcional.
- **Invariantes a preservar:** sanitização de previews e relatórios
  (`CredentialSanitizer`), identidade interna por `AccountKey`/`sfp1` (nunca por
  string sanitizada), e semântica de discovery/janela. Ver `AGENTS.md`.

---

## 10. Decisões em aberto (para o utilizador decidir)

1. Manter **"Catálogo"** como guarda-chuva único ou dividir em Catálogo (dados) +
   Monitorização (métricas) desde já?
2. Onde colocar **"Grupos"** e **"Ordering"**: Configuração (por serem definição)
   ou Catálogo (por estarem junto das entidades)?
3. As **definições persistidas como entidades SQLite** (priority, source
   selection, import policies) devem migrar para ficheiro de configuração, ou
   permanecer entidades e apenas mudar de local na UI?
4. Nome da área de operações: **"Operações"**, "Ações" ou "Execução"?
5. *Import Policies* (I9): expor, remover da UI ou manter oculto mas documentado?
6. A área **Setup** deve continuar a incluir a *prontidão* (checklist) ou separar
   "primeira configuração" de "estado de prontidão"?

---

## 11. Não-objectivos

- Não alterar endpoints, rotas ou contratos JSON.
- Não migrar dados nem alterar o schema SQLite.
- Não reescrever o backend nem a lógica de domínio.
- Não introduzir framework de UI nem dependências novas.
- Não alterar a semântica de discovery, validação ou sync.

---

## 12. Critérios de aceitação da proposta

A proposta considera-se **accionável** quando:

1. Existe o **mapeamento old → new** completo (secção 5.4), sem vistas/sub-tabs
   órfãos: cada vista actual tem um destino.
2. Cada **domínio de configuração** (secção 3.3) tem um **editor canónico**
   único identificado; os restantes locais são read-only/links.
3. A **precedência** global → job → run está representada (secção 6) sem
   duplicar regras de validação no cliente.
4. As **fases 0–3** têm âmbito, ficheiros prováveis, testes afectados e critério
   de aceitação verificáveis (secção 7).
5. Nenhum endpoint/contrato é alterado e nenhum invariante de `AGENTS.md` é
   quebrado.
6. As **decisões em aberto** estão explicitadas para o utilizador (secção 10).
7. A estratégia de **implementação paralela e cutover** está definida
   (secção 8): coexistência das UIs, critérios de cutover e rollback, com a
   legacy como fallback, sem alterar endpoints nem a lógica de domínio.

---

### Cross-references

- `m3uCrawler/README.md` — comportamento actual do dashboard, discovery,
  Dispatcharr sync, ordering e validação (referência primária).
- `AGENTS.md` — invariantes de sanitização, identidade interna e
  `WebDashboardService.cs` como ficheiro sensível.
- `docs/architecture/run-observability-and-manual-trigger.md` — observabilidade e
  trigger manual (relevante para as áreas Operações/Monitorização).
- `docs/architecture/dispatcharr-source-selection.md` e
  `docs/architecture/phase-13-4-source-selection-policy.md` — domínio de selecção
  de fontes (relevante para o hub Configuração).
- `docs/architecture/channel-catalog-and-ownership.md` — entidades de catálogo
  (relevante para a área Catálogo).

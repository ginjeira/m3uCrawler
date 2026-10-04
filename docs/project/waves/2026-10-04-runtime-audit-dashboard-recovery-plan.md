# Plano de recuperação e evolução do Dashboard/pipeline — auditoria runtime 2026-10-04

> **Documento de planeamento (docs-only).** Registra a auditoria runtime de 2026-10-04 e o plano de
> waves de recuperação `W1–W7`. Subordinado à BÍBLIA (`docs/Reestructure/00-BIBLE.md:30-41`, `:105-128`);
> não redefine conceitos normativos nem implementa nada.
>
> **Estado:** nenhuma das waves `W1–W7` foi implementada. Este documento é uma intenção de trabalho.
>
> **Cross-reference:** estado corrente e prioridades em `docs/PROJECT_STATUS.md` (secção
> "Waves 2026-10 — recuperação pós-auditoria runtime") e `ROADMAP.md` (§ "Em curso"). Índice de waves
> em `docs/project/waves/README.md`.

**Legenda de classificação (usada em todo o documento):**

- **CONFIRMADO** — observado em runtime real (Playwright/API/BD) e/ou confirmado no código.
- **DECISÃO** — arquitectura/comportamento já decidido pelo proprietário; não é hipótese.
- **PENDENTE** — trabalho a executar numa wave futura; não implementado.

## 1. Contexto da auditoria

| Item | Valor |
|---|---|
| Data | 2026-10-04 |
| HEAD | `cf0e3141f447f2b26dc6cc06c31fc53a4d111a3f` |
| Runtime auditado | `m3ucrawler-first-test` em `192.168.68.142:5000` |
| Imagem | `ghcr.io/ginjeira/m3ucrawler@sha256:09204621cde12af7148eabcdffb28a49fdf724988d348cc08aa3048d97ac519c` (build de `cf0e314`) |
| Método | Playwright sobre bytes servidos (browser real) + API autenticada + leitura de BD/ficheiros/logs + código + histórico Git/legacy |

A auditoria foi **runtime-first** (não apenas leitura estática). O backend/API foi validado com
evidência HTTP + BD; a UI foi validada com um browser real.

## 2. CONFIRMADO — problemas encontrados

### 2.1 Regressão transversal de escopo JavaScript (IIFE) → UI parcialmente morta

**CONFIRMADO (runtime + código + git).** O *script* principal do Dashboard é envolvido em
`(function(){ … })();` (introduzido no commit `2311540` *"feat: redesign m3uCrawler dashboard"*,
2026-08-30). As funções ficam **locais** ao IIFE; apenas **38** foram re-exportadas para `window`.
Os *handlers* **inline** do HTML (`onclick='fn()'`, `onchange='fn()'`) resolvem os nomes no **escopo
global** → qualquer função não exportada lança `ReferenceError` e o clique **não faz nada**.

Evidência runtime (Playwright sobre os bytes servidos, `cf0e314`):

- `typeof window.showCreateChannelForm === "undefined"`; clicar em **"+ Novo Canal"** →
  `pageerror: showCreateChannelForm is not defined`; o formulário permanece `hidden`.
- Clicar no nome de um canal → `pageerror: selectChannel is not defined`.
- `<th>Display Name</th>` não tem handler nenhum (não há ordenação implementada).
- `submitCreateScheduledJob`, `submitCreateOrderingList`, `submitCreateChannel` → **`undefined`**.
- **~46** nomes de handler inline não têm export para `window` (22 observados mortos em runtime);
  contrasta com os que **funcionam** por terem sido re-exportados (`approveReview`,
  `submitAddAffinityGroup`, `loadCatalogReviews`, `startLiveRun`, `loadLiveRun`, …).

Sintomas explicados: criação/edição/eliminação de canal canónico, criação de scheduler, criação de
ordering list, e várias outras acções do Dashboard estão mortas por `ReferenceError`.

> Nota: o **backend/API funciona** — a criação de canal (201), o scheduler CRUD (200) e o ordering
> CRUD (201) responderam correctamente quando exercidos via API. A regressão é da **UI**.

### 2.2 Problemas independentes confirmados

| # | Problema | Classificação |
|---|---|---|
| P1 | **`WriteJsonAsync` mascara o status HTTP**: repõe sempre o status (default `200`), pelo que vários caminhos de erro devolvem `HTTP 200` com corpo `{"error":…}`. O JavaScript (`if (r.ok)`) interpreta como sucesso → falha silenciosa (ex.: canal sem `displayName`, enum inválido, ordering duplicado/sem campos). | CONFIRMADO (runtime + código) |
| P2 | **Rotas inexistentes podem ficar penduradas**: `PUT`/`DELETE` não correspondidos mantêm o socket aberto (HTTP 000, sem `404/405`); `GET` desconhecido devolve o HTML do Dashboard com `200` (fallback SPA). | CONFIRMADO (runtime) |
| P3 | **Ordering não tem `PUT`**: não existe edição de metadados; o endpoint devolve *hang* em vez de `404/405`. | CONFIRMADO (runtime + código) |
| P4 | **Reviews não desaparecem**: `GET /api/catalog/reviews` devolve **todos** os itens, incluindo os `Resolved` (runtime: 513 itens, 60 `Resolved`); a UI só esconde botões, não remove linhas. | CONFIRMADO (runtime) |
| P5 | **`approveReview` com UX incompleta**: usa `prompt()` em cadeia (acção, key, nome); não há *dropdown* de canais; `create-channel` só recolhe *key*+*name* (sem Display Name/país/categoria/grupo). | CONFIRMADO (runtime) |
| P6 | **Add Alias não alimenta Affinity**: `ApplyAddAliasAsync` só escreve `channel_aliases`; `affinity_groups`/`affinity_members` ficam inalterados (testado num canal de teste). Alias e Affinity são dois mecanismos paralelos sem ligação. | CONFIRMADO (runtime + BD) |
| P7 | **Dispatcharr sem UI funcional**: existem `POST /api/dispatcharr/dry-run` e `/sync`, mas nenhum botão no Dashboard (só um *badge* de estado). | CONFIRMADO (runtime) |
| P8 | **Separador Canais/Países sem CRUD de país**: edita listas de canais/aliases por país (`runtime-data/countries/*.json`) e valida a playlist; **não** cria nem elimina países. O botão "Re-validar" (`loadCountryValidation`) está morto (§2.1). | CONFIRMADO (runtime + código) |
| P9 | **Playlist canónica não é produzida pelo ciclo normal**: o ciclo `--telegram` produz `telegram_playlist_<timestamp>.m3u`; `playlist.m3u`/`playlist_temp.m3u` só são produzidos por `--telegram-maintain`. No runtime auditado esses ficheiros **não existem** → `/api/playlist` e `/api/playlist_temp` devolvem `404` e o separador Playlist mostra "não disponível". | CONFIRMADO (runtime + código) |
| P10 | **Dispatcharr consome o artefacto timestamped** em vez do produto canónico decidido. | CONFIRMADO (runtime: `sourcePlaylistPath=…/telegram_playlist_20261003_200433.m3u`, `dryRun=false`) |
| P11 | **Mutações do Scheduler não geram `audit_records`** (falta auditabilidade). | CONFIRMADO (BD) |
| P12 | **Regressão não coberta por testes**: não existe teste que garanta que cada handler inline existe no escopo esperado. | CONFIRMADO (código) |

## 3. DECISÃO — arquitectura alvo das playlists

**DECISÃO.** Não remover `playlist.m3u` nem `playlist_temp.m3u`. A arquitectura alvo é:

```
RUN
  ↓
descoberta
  ↓
streams/candidates
  ↓
normalização
  ↓
deduplicação
  ↓
playlist_temp.m3u
  ↓
filtros / validação / matching / rejeições / publicação
  ↓
playlist.m3u
  ↓
Dispatcharr
```

Definições:

- **`playlist_temp.m3u`** = conjunto **intermédio** com os streams/canais **normalizados e
  deduplicados** antes dos filtros finais.
- **`playlist.m3u`** = playlist **final publicada**, destinada ao consumo do **Dispatcharr**.
- **`telegram_playlist_<timestamp>.m3u`** = artefacto **histórico/técnico** de uma execução, se
  continuar a ser útil; **NÃO** deve ser o produto canónico do pipeline nem substituir
  `playlist.m3u`.

## 4. PENDENTE — plano de waves

A ordem abaixo é **normativa** para a execução. Cada wave tem âmbito isolado, testes, evidência,
actualização de documentação quando necessário, e **commit próprio**; não mistura alterações de
waves seguintes.

### W1 — Dashboard JS / IIFE / handlers

**Objectivo:** restaurar as funcionalidades actualmente mortas devido ao problema de escopo/IIFE (§2.1).

**Inclui:**
- handlers usados pelo HTML inline;
- criação/edição/eliminação de canonical channels;
- scheduler;
- ordering;
- source groups;
- country validation;
- outras acções demonstradas como `ReferenceError`.

**Critério:** todos os handlers referenciados pelo HTML devem existir no escopo esperado, **ou** a
implementação deve ser convertida para *event listeners* sem dependência de globals. Adicionar
**regressão** que impeça voltar a introduzir handlers HTML que não estejam disponíveis.

**NÃO resolver nesta wave** os restantes problemas funcionais descobertos (§2.2, §3).

### W2 — Pipeline de playlists / Dispatcharr

**Objectivo:** restabelecer um pipeline funcional de dados novos para o Dispatcharr.

**Implementar e validar a arquitectura**:
`RUN → normalização/deduplicação → playlist_temp.m3u → filtros/validação/matching/rejeição/publicação → playlist.m3u → Dispatcharr`.

**Requisitos:**
- `playlist_temp.m3u` deve representar o estado intermédio definido em §3;
- `playlist.m3u` deve representar o resultado final;
- o Dispatcharr deve consumir `playlist.m3u`;
- não depender de `telegram_playlist_<timestamp>.m3u` como produto principal;
- preservar, se tecnicamente útil, os artefactos timestamped como histórico;
- validar a cadeia com uma execução real controlada;
- validar que existem dados novos em `playlist.m3u`;
- validar que o Dispatcharr recebe esses dados.

**Prioridade:** esta wave é **prioritária** e deve ser executada antes das waves funcionais
restantes, porque deixar a playlist para o fim significa continuar sem alimentação normal do
Dispatcharr.

**NÃO misturar nesta wave:** scheduler; ordering; canonical CRUD; reviews; affinity; countries;
redesign geral do Dashboard.

### W3 — HTTP/API transversal

**Objectivo:** corrigir problemas transversais identificados em §2.2 P1–P2.

- `WriteJsonAsync` não deve mascarar o status HTTP;
- erros devem chegar ao browser como `4xx/5xx` adequados;
- rotas inexistentes devem responder `404/405` e **nunca** ficar penduradas;
- completar/validar contratos HTTP necessários;
- adicionar testes de regressão.

### W4 — Scheduler + Ordering + Canonical Channels

**Objectivo:** validar e tornar funcionais end-to-end.

- **Scheduler:** criar; editar; activar/desactivar; eliminar; persistência; execução; apresentação
  dos jobs; ajuda de Cron; auditabilidade (ver §2.2 P11).
- **Ordering:** criar; editar; eliminar; adicionar/remover items; preview; persistência (ver §2.2 P3).
- **Canonical Channels:** criar; editar; eliminar; todos os campos relevantes; Display Name;
  validações; UI completa.

**Critério:** não considerar suficiente o facto de a API funcionar; cada funcionalidade deve ser
**testada através do Dashboard**.

### W5 — Reviews + Alias → Affinity

**Reviews:**
- canais aprovados/revistos devem **desaparecer** da lista de reviews pendentes (§2.2 P4);
- Approve deve apresentar opções **estruturadas** (§2.2 P5);
- Create Channel deve apresentar **formulário completo**;
- Add Alias deve apresentar os canonical channels existentes para **selecção**;
- não obrigar o utilizador a escrever manualmente uma canonical key.

**Alias → Affinity** (§2.2 P6). Ao adicionar um alias a um canonical channel:
- se já existir a Affinity correspondente, adicionar o membro de forma **idempotente**;
- se não existir, criar **exactamente uma** Affinity;
- adicionar o canonical/alias member correspondente;
- **nunca** criar Affinities duplicadas;
- reflectir a alteração no separador **Afinidades**.

### W6 — Dispatcharr UI + Countries + restantes funcionalidades

**Dispatcharr** (§2.2 P7):
- disponibilizar **Dry Run**;
- disponibilizar **Sync** real;
- apresentar resultado;
- apresentar erros;
- respeitar a autenticação/configuração existente.

**Countries** (§2.2 P8): decidir e documentar se o separador deve ser
**(a)** CRUD completo de países, **ou** **(b)** apenas ferramenta de validação/configuração.
Se for mantido como entidade funcional, implementar CRUD completo.

**Corrigir também:** Revalidar; restantes botões mortos; funcionalidades sem implementação
correspondente.

### W7 — Auditoria final end-to-end

**Objectivo:** executar auditoria completa do Dashboard e pipeline contra **runtime real controlado**.

**Validar:** todas as funcionalidades visíveis; todas as rotas; todos os handlers; estados de erro;
persistência; scheduler; ordering; canonical channels; reviews; aliases; affinities; countries;
playlists; Dispatcharr; output inventory; observabilidade; documentação.

## 5. Ordem e prioridade

1. **W1** e **W2** são as prioridades imediatas.
2. **W2 é deliberadamente antecipada** (executada antes das restantes waves funcionais) para não
   deixar o Dispatcharr sem dados novos enquanto as waves de UI decorrem.
3. **W3–W7** seguem-se pela ordem indicada.

## 6. Critérios gerais

Cada wave deve:
- ter âmbito isolado;
- ter testes;
- ter evidência;
- actualizar documentação quando necessário;
- produzir **commit próprio**;
- não misturar alterações de waves seguintes.

## 7. Rastreabilidade (problema → wave)

| Problema (§2) | Wave |
|---|---|
| Regressão IIFE / handlers mortos (2.1) | **W1** |
| Playlist canónica + Dispatcharr (P9, P10) | **W2** |
| Masking de status / rotas penduradas (P1, P2) | **W3** |
| Scheduler, Ordering, Canonical (P3, P11) | **W4** |
| Reviews, Alias→Affinity (P4, P5, P6) | **W5** |
| Dispatcharr UI, Countries, botões mortos (P7, P8) | **W6** |
| Verificação global (todos) | **W7** |
| Regressão de cobertura de handlers (P12) | **W1** |

## 8. Não-objectivos

Este plano **não** implementa nada; é documentação. Não altera código, APIs, UI, playlists,
Dispatcharr nem configuração de deployment. Não reescreve documentação histórica para fazer parecer
que algo já foi implementado. Os problemas em §2 são **CONFIRMADO** (observados), a arquitectura em
§3 é **DECISÃO**, e §4 é **PENDENTE**.

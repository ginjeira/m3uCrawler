# Source Selection Policy — persistência e exposição (PHASE 13-4)

> Estado: **Wave 13-4 implementada** — política global de selecção de fontes
> persistida na BD do catálogo, resolvida em runtime e exposta no Dashboard
> (§0). As decisões deste documento foram fechadas (§13). **Os overrides por
> canal (Wave 13-4b) foram implementados subsequentemente**, de forma aditiva e
> sem alteração de schema (ver §0.1 e
> `docs/architecture/dispatcharr-source-selection.md` §10.2). **Fora de âmbito e
> não implementados:** auditoria de alterações administrativas.
>
> Base implementada:
> - **Wave 13-1** — algoritmo de selecção puro e determinístico
>   (`IChannelSourceSelector` / `ChannelSourceSelector`), sem I/O
>   (`docs/architecture/dispatcharr-source-selection.md` §1–§9).
> - **Wave 13-3** — aplicação da política ao pipeline Telegram antes da
>   publicação em `output/playlist.m3u`, com defaults em memória
>   (`docs/architecture/dispatcharr-source-selection.md` §10).
> - **Wave 13-4** — persistência global da política + resolver + Dashboard
>   (§0), com uma alteração semântica deliberada na guarda do selector (§8).
>
> Convenções deste documento:
> - **[FACT]** — comportamento verificado no código, com `ficheiro:linha`.
> - **[DECISÃO]** — escolha de arquitectura adoptada/ratificada.
> - **[PENDENTE]** — trabalho explicitamente fora desta wave (auditoria). Os
>   overrides por canal (13-4b) foram entretanto implementados (§0.1).

## 0. Estado implementado (Wave 13-4)

**[FACT]** A Wave 13-4 está implementada:

- **Entidade + schema:** `SourceSelectionPolicyEntity`
  (`m3uCrawler/Services/Catalog/CatalogEntities.cs`), mapeada para a tabela
  `source_selection_policies`
  (`m3uCrawler/Services/Catalog/ChannelCatalogDbContext.cs`) por migration
  **aditiva** `AddSourceSelectionPolicies`
  (`m3uCrawler/Services/Catalog/Migrations/`), com índice **único** em
  `ScopeKey` e **sem** Foreign Key. A identidade referencia
  `CanonicalChannelKey` (string estável), **nunca** `CanonicalChannelId`.
- **Linha global:** `ScopeKey="global"`, `CanonicalChannelKey=null`, criada
  **lazy** por `CatalogResolver.GetOrCreateGlobalSourceSelectionPolicyAsync`
  (`m3uCrawler/Services/Catalog/CatalogResolver.cs`) com defaults
  `MaxSourcesPerChannel=10`, `PreferDistinctProviders=true`,
  `MaxSourcesPerProvider=null`, `AllowFallbackToSameProvider=true`.
- **Resolução:** o novo `SourceSelectionPolicyResolver`
  (`m3uCrawler/Services/SourceSelection/`) resolve a política global efectiva.
  `SourceSelectionStage` **continua sem persistência** e recebe sempre uma
  `SourceSelectionPolicy` explícita. Os dois pontos de publicação Telegram
  passam a resolver via resolver (`m3uCrawler/Program.cs:540-541` e `:1108-1109`).
- **Dashboard:** `GET/POST /api/catalog/source-selection-policies` e cartão
  *data-driven* na área **Catálogo** (apenas global), sob o gate de
  autenticação/CSRF existente (`m3uCrawler/Services/WebDashboardService.cs`).
- **Exclusão de legacy adoption:** a tabela nova é **excluída** do
  `LegacyConfigurationEvidenceEvaluator`, tal como `source_priority_policies`
  (`m3uCrawler/Services/Configuration/LegacyConfigurationEvidenceEvaluator.cs`).
- **Alteração semântica ratificada:** `ChannelSourceSelector` aceita
  `MaxSourcesPerChannel >= 0` (`0` é válido e não selecciona nenhuma fonte);
  negativos são inválidos. `MaxSourcesPerProvider` mantém `null` = sem limite,
  com `0`/negativos inválidos (§8, §13.3).

**Não implementado nesta wave:** qualquer log de auditoria de alterações
administrativas (§10). Os overrides por canal, originalmente reservados à Wave
13-4b, estão implementados (§0.1).

### 0.1 Estado implementado (Wave 13-4b)

**[FACT]** A Wave 13-4b acrescentou os **overrides por canal** de forma aditiva,
sem alteração de schema (a coluna `ScopeKey` já acomodava
`channel:<CanonicalChannelKey>`):

- **Identidade:** `CanonicalChannel.Key` (nunca `CanonicalChannelId`). O
  `ScopeKey` é `channel:<key>` (`SourceSelectionPolicyScopes`).
- **Substituição completa:** o override é uma política completa que substitui a
  global por inteiro quando existe; não há merge campo a campo (a entidade não
  representa "campo não definido"). Resolução: override por canal → global →
  defaults (`10/true/null/true`).
- **Fachada de catálogo:** `CatalogResolver` expõe
  `GetChannelSourceSelectionPolicyAsync`,
  `UpsertChannelSourceSelectionPolicyAsync`,
  `DeleteChannelSourceSelectionPolicyAsync` e
  `ListChannelSourceSelectionPoliciesAsync`. Os overrides são FK-less; órfãos
  (canal inexistente) são inertes.
- **Resolver:** `SourceSelectionPolicyResolver` mantém `ResolveGlobalAsync` e
  acrescenta `ResolveEffectiveAsync(key)` e `LoadEffectivePoliciesAsync()`, que
  devolve um `SourceSelectionPolicySet : ISourceSelectionPolicyProvider`
  (snapshot por execução, 2 queries, sem cache entre execuções e sem N+1).
- **Estágio:** `SourceSelectionStage` ganha o overload
  `ApplyAsync(streams, ISourceSelectionPolicyProvider, ct)` e resolve a política
  efectiva por grupo de canal canónico via `CanonicalChannel.Key`; o overload
  legado de política única mantém-se e o estágio permanece **sem persistência**.
  O agrupamento continua por `CanonicalChannelId` e o selector/ranking/limites
  não mudam.
- **Dashboard:** `GET/POST /api/catalog/source-selection-policies/channels` e
  `GET/DELETE /api/catalog/source-selection-policies/channels/{key}`, sob o mesmo
  gate de auth/CSRF; a UI lista, edita e elimina overrides (texto de ajuda
  "substitui a global"; `0` mantém-se válido e negativos são rejeitados).
- **Semântica preservada:** `MaxSourcesPerChannel` `0` válido / `1..N` válido /
  negativos inválidos; `MaxSourcesPerProvider` `null` = sem limite, `0`/negativos
  inválidos.
- **Testes:** +23 casos (resolver, persistência por canal, endpoints e
  integração runtime); suite serial 1845 passed / 0 failed / 1 skipped. A
  identidade sobrevive a apagar/recriar o canal com a mesma `Key`.

## 1. Estado antes da 13-4 (contexto factual pós-13-3)

**[FACT]** O algoritmo da política existe desde a 13-1 e não tem I/O:

- `SourceSelectionPolicy` é um `record` com quatro campos —
  `MaxSourcesPerChannel`, `PreferDistinctProviders`, `MaxSourcesPerProvider`
  (opcional) e `AllowFallbackToSameProvider`
  (`m3uCrawler/Services/SourceSelection/SourceSelectionModels.cs:108-112`).
- O algoritmo (`ChannelSourceSelector`) é determinístico e **sem limites
  hardcoded**; o chamador fornece sempre a política
  (`docs/architecture/dispatcharr-source-selection.md` §4–§9).

**[FACT]** A 13-3 aplicava a política ao pipeline Telegram com um valor
**constante em código**, então não configurável (a 13-4 substitui-o pela
resolução persistida, §0):

- `SourceSelectionDefaults.DefaultPolicy` =
  `MaxSourcesPerChannel: 10`, `PreferDistinctProviders: true`,
  `MaxSourcesPerProvider: null`, `AllowFallbackToSameProvider: true`
  (`m3uCrawler/Services/SourceSelection/SourceSelectionStage.cs:16-23`).
- O comentário da própria classe declara explicitamente "Defaults da política
  de selecção para a Wave 13-3 (**sem persistência**)"
  (`SourceSelectionStage.cs:11-15`).
- Os dois pontos de publicação usavam a constante:
  - single-cycle Telegram —
    `Program.cs:540-541`
    (`new SourceSelectionStage(...).ApplyAsync(workingStreams, SourceSelectionDefaults.DefaultPolicy, ...)`);
  - ciclo de manutenção Telegram —
    `Program.cs:1108-1109`.
- `SourceSelectionStage` **não escreve** ficheiros nem persiste nada; agrupa
  por `ChannelSource.CanonicalChannelId` e publica as seleccionadas
  (`SourceSelectionStage.cs:162-164`; ver também
  `dispatcharr-source-selection.md` §10).

**[FACT] (estado pré-13-4)** Não existia superfície de configuração para a
política, lacuna que a Wave 13-4 fechou (§0):

- Nenhum endpoint de dashboard expunha a política de selecção de fontes
  (entretanto adicionado `GET/POST /api/catalog/source-selection-policies`, §0).
- Nenhum ficheiro JSON de runtime a continha; os stores existentes são
  `stream_validation_policy.json`, `app_settings.json` e
  `configuration_lifecycle.json` (§2).
- Não havia entidade, `DbSet` ou migration associada à selecção de fontes
  (entretanto adicionados `SourceSelectionPolicyEntity` e
  `AddSourceSelectionPolicies`, §0).

**[FACT]** O diagnóstico da 13-3 é apenas agregado:
`RunReport.SourceSelection` (`SourceSelectionReport`) transporta contagens,
nunca URLs/credenciais (`dispatcharr-source-selection.md` §10, "Diagnóstico").

**Conclusão.** Antes da 13-4 a política era imutável em runtime (alterá-la
exigia recompilar). A Wave 13-4 tornou-a **configurável e persistente** (§0),
mantendo o algoritmo e o estágio de aplicação inalterados.

## 2. Existing Configuration Architecture

### 2.1 Padrões de persistência encontrados

**[FACT]** O projecto tem **dois** padrões de configuração coexistentes:

**A. BD do catálogo (SQLite + EF Core)** — usada pelo domínio do catálogo e
por políticas com escopo por canal:

- `ChannelCatalogDbContext` em
  `m3uCrawler/Services/Catalog/ChannelCatalogDbContext.cs:7`, com
  **26 `DbSet`s** (`:14-39`).
- O schema evolui por **EF Core migrations** e `context.Database.MigrateAsync()`
  em `ChannelCatalogBootstrapper.cs:95`. A pasta
  `m3uCrawler/Services/Catalog/Migrations/` contém **14 migrations**
  (`InitialCatalog` … `AddLiveRuns`), sem qualquer `InsertData` (nenhum seed
  em migrations). **[FACT]**
- O bootstrap faz *pre-migration backup* (`ChannelCatalogBootstrapper.cs:219-243`)
  e usa um ficheiro `.lock` exclusivo para serializar migrations
  (`:62-73`). Ambos os mecanismos já existem e cobrem qualquer migration nova.
- O analógico mais próximo da política pretendida é
  `SourcePriorityPolicyEntity`
  (`m3uCrawler/Services/Catalog/CatalogEntities.cs:956-976`), mapeada para a
  tabela `source_priority_policies`
  (`ChannelCatalogDbContext.cs:324-338`), com **índice único apenas sobre
  `Scope`** (`:337`) — um defeito conhecido (ver §5.5).
- O acesso passa por uma fachada (`CatalogResolver`):
  `GetOrCreateGlobalPriorityPolicyAsync`
  (`CatalogResolver.cs:1701-1722`, cria a linha `Scope="global"` com defaults
  **lazy**), `GetChannelPriorityPolicyAsync` (`:1724-1731`) e
  `UpsertPriorityPolicyAsync` (`:1733-1776`).
- O consumo tem *fallback* por canal → global em
  `PlaylistComposerService.ComposeAsync`
  (`m3uCrawler/Services/Catalog/PlaylistComposerService.cs:87-93` e
  `:117-118`). **[FACT]**
- Exposição no dashboard: `GET/POST /api/catalog/priority-policies`
  (`WebDashboardService.cs:1456-1500`), com `PriorityPolicyPayload`
  (`:2965-2972`) e projecção `PriorityPolicyToJson` (`:3183-3196`).
- **[FACT] Exclusão deliberada do legacy adoption:** `source_priority_policies`
  **não** conta como evidência de instalação legacy precisamente porque a linha
  global é criada lazily
  (`m3uCrawler/Services/Configuration/LegacyConfigurationEvidenceEvaluator.cs:35-37`;
  ver `configuration-lifecycle.md` §"Critério de legacy adoption").

**B. Ficheiros JSON em `runtime-data`** — usada para estado operacional
transversal, não ligado ao catálogo:

- `StreamValidationPolicyStore`
  (`m3uCrawler/Services/Validation/StreamValidationPolicyStore.cs:10-78`),
  path `<cwd>/runtime-data/stream_validation_policy.json` (`:26`); em falta ou
  corrompido **auto-repara** com defaults (`:35-60`); **não é atómico**
  (`File.WriteAllText`, `:72`); a normalização é feita por
  `StreamValidationOptions.Sanitize()` com `Math.Clamp`
  (`StreamValidationOptions.cs:81-95`).
- `AppSettingsStore` (`m3uCrawler/Services/Configuration/AppSettingsStore.cs:47-115`),
  path `<runtime-data>/app_settings.json` (`:63`), também auto-reparador e não
  atómico.
- `ConfigurationLifecycleStore`
  (`m3uCrawler/Services/Configuration/ConfigurationLifecycleStore.cs`),
  `configuration_lifecycle.json` (`:26`), este **sim atómico**:
  ficheiro temporário + `File.Move(..., overwrite: true)` (`:139-142`).
- Endpoint análogo de política JSON:
  `GET/POST /api/validation/policy`
  (`WebDashboardService.cs:2345-2381`).

**[FACT]** Não existe `appsettings.json` nem uso de
`Microsoft.Extensions.Configuration.IConfiguration` / `IOptions<T>` no código
da aplicação. As ocorrências com nomes parecidos são artefactos do projecto:
`IConfigurationGate` (gate de lifecycle, interface própria,
`Services/Configuration/ConfigurationGate.cs:9`) e a função JavaScript
`loadAppSettings()` do dashboard. Nenhuma destas é o sistema de configuração
do .NET.

### 2.2 Dashboard (superfície de exposição)

**[FACT]** O dashboard é um único `HttpListener` com uma cadeia linear de
`if` dentro de `HandleRequestAsync`
(`WebDashboardService.cs:275`). Propriedades relevantes:

- Serialização JSON partilhada em camelCase
  (`JsonOptions`, `WebDashboardService.cs:2522-2527`) e helper
  `WriteJsonAsync` (`:2592`).
- **Um único gate global** de autenticação + CSRF antes dos handlers
  (`:334-378`): sessão humana ou `--web-token` de máquina; métodos mutantes
  exigem `X-CSRF-Token` e são rejeitados com `csrf-invalid` caso falhem
  (`:364-376`). Não há autenticação por handler.
- **[FACT] Não existe log de auditoria de alterações administrativas.** Não há
  tabela, ficheiro ou endpoint de auditoria para mutações de configuração
  (priorities, settings, validação). Esta lacuna é transversal, não específica
  da selecção de fontes.

### 2.3 "Scope da policy"

Antes de escolher o modelo de dados, é necessário fixar o **escopo** da
política. As três opções, avaliadas de forma factual (sem eleger "melhor"):

| Dimensão | Global only | Global + override por canal | Per-channel only |
|---|---|---|---|
| Onde é persistido | 1 linha `ScopeKey="global"` | N linhas (`global` + 1 por canal com override) | N linhas (1 por canal) |
| Impacto no schema | mínimo: 1 tabela + índice único em `ScopeKey` | idêntico (mesma tabela; `CanonicalChannelKey` opcional) | idêntico |
| Impacto em runtime | 1 leitura determinística | leitura do global + *lookup* por canal com fallback | *lookup* por canal; sem fallback exige linha por canal |
| Lookup | constante | global + dicionário por chave | por chave; canais sem linha ficam sem política |
| Sem override | comportamento uniforme (13-3 preservado) | cai no global (13-3 preservado) | **[risco]** canal sem linha não tem política: ou erro ou default implícito |
| Impacto no dashboard | 1 formulário | 1 formulário + editor por canal | editor por canal (mais complexo) |
| Impacto em testes | baixo | médio (fallback por canal) | alto (cobrir canal sem linha) |
| Impacto futuro no Composer/Discovery | política uniforme | espelha exactamente `source_priority_policies` (per-channel → global) | divergente do análogo existente |

**[FACT]** O análogo directo (`source_priority_policies`) usa "global +
override por canal com fallback" (`PlaylistComposerService.cs:117-118`).
**[FACT]** A identidade canónica estável já existe: `CanonicalChannelEntity.Key`
(`CatalogEntities.cs:16-21`), imutável após criação
(`CatalogResolver.UpdateCanonicalChannelAsync`, `CatalogResolver.cs:912-938`),
e a `AffinityGroupEntity` já migrou de `CanonicalChannelId` para
`CanonicalChannelKey` (`CatalogEntities.cs:158-161`). **[FACT]** O roadmap
determina explicitamente usar a chave lógica estável em vez de
`CanonicalChannelId` (`docs/IMPLEMENTATION_ROADMAP.md:3788-3791`).

O *trade-off* central: "global only" é a menor mudança com menor risco, mas
não prepara o modelo de dados para overrides futuros; "global + per-channel"
tem custo de implementação marginalmente maior mas alinha com o análogo e com
o roadmap; "per-channel only" é a opção com maior risco de regressão silenciosa
(canais sem linha). A **decisão** é tomada na §4 — esta subsecção apenas
constata.

## 3. Policy Persistence Options

Comparação factual das quatro famílias de opção. Veredicto: §4.

| Critério | (A) Entidade EF na BD do catálogo | (B) Store JSON dedicado | (C) Merge em `app_settings.json` | (D) Sem persistência (status quo) |
|---|---|---|---|---|
| Analogia existente | `SourcePriorityPolicyEntity` (`CatalogEntities.cs:956-976`) | `StreamValidationPolicyStore` / `ConfigurationLifecycleStore` | `AppSettingsStore` (`AppSettingsStore.cs:47-115`) | 13-3 (`SourceSelectionStage.cs:16-23`) |
| Escopo global | sim | sim | sim | sim (constante) |
| Escopo por canal | **sim, transactionalmente**, com *unique* e FK lógica por `Key` | possível, mas sem integridade referencial | desalinhado: `app_settings` é transversal, não por canal | não configurável |
| Migração/backup/lock | reutiliza `MigrateAsync` + backup + `.lock` (`ChannelCatalogBootstrapper.cs:95,219-243,62-73`) | nenhum destes mecanismos se aplica | idem | n/a |
| Atomicidade | transacção EF | depende do store (o de validação **não** é atómico; o de lifecycle **é**) | não atómico (`AppSettingsStore.cs:109`) | n/a |
| Dois "roots" de dados | não (um só caminho, `/data`) | **sim** — `runtime-data` pode ser `<cwd>/runtime-data` ou bind mount `/data` (ambiguidade observada em `WebDashboardService.cs:2347-2348` e `:2388-2389`) | idem (B) | n/a |
| Consistência com o catálogo | alta (mesma transacção que `ChannelSource`) | baixa (playlist e política em mundos separados) | baixa | n/a |
| Exclusão do legacy adoption | exige excluir a tabela (como `:35-37`) | não interfere com `LegacyConfigurationEvidenceEvaluator` | não interfere | não interfere |
| Testes de BD | padrão canónico já existe (`ChannelCatalogIntegrationTests.cs:569-588`) | padrão de store JSON já existe | padrão de store JSON já existe | n/a |
| Reversibilidade do schema | suportada por `Phase93MigrationReversibilityTests` | ficheiro pode ser apagado | ficheiro pode ser apagado | n/a |
| Mudança de contrato | migration aditiva | novo ficheiro de runtime | alarga estrutura existente | nenhuma |

**[FACT]** O `app_settings.json` é hoje um contentor de definições
transversais (UID/GID, path de playlist, etc.) exposto em `GET/POST /api/settings`
(`WebDashboardService.cs:2383-2401`, `AppSettingsStore.cs:47-115`) — não é um
repositório de políticas por canal.

## 4. Arquitectura adoptada (DECISÃO ratificada)

> **[DECISÃO ratificada e implementada]** A Wave 13-4 persiste a
> `SourceSelectionPolicy` como uma nova entidade EF Core na BD do catálogo —
> `SourceSelectionPolicyEntity`, tabela
> `source_selection_policies` — criada por uma migration **aditiva**
> `AddSourceSelectionPolicies`, resolvida por um novo
> `SourceSelectionPolicyResolver`, criada lazily por
> `CatalogResolver.GetOrCreateGlobalSourceSelectionPolicyAsync`, e exposta em
> `GET/POST /api/catalog/source-selection-policies`.
>
> A 13-4 implementou a **linha global** e o resolver; o schema e o resolver
> foram desenhados para que o override por canal fosse **puramente aditivo**, e a
> Wave 13-4b implementou-o sem alteração de schema (§0.1).

Justificação (derivada dos factos de §2–§3, não uma re-derivação da política):

1. **Espelha o análogo mais próximo.** `SourcePriorityPolicyEntity` já resolve
   exactamente o mesmo problema de escopo (global + override por canal com
   fallback), com resolver, endpoint, payload e projecção
   (`CatalogEntities.cs:956-976`, `CatalogResolver.cs:1701-1776`,
   `WebDashboardService.cs:1456-1500,2965-2972,3183-3196`). A 13-4 segue o
   padrão em vez de inventar um novo.
2. **Reutiliza infraestrutura de migration já validada.** `MigrateAsync`,
   backup pré-migration e `.lock` exclusivo já existem e cobrem a nova tabela
   sem código novo (`ChannelCatalogBootstrapper.cs:95,219-243,62-73`).
3. **Suporta per-channel de forma transaccional.** O override por canal é uma
   linha na mesma tabela, na mesma transacção que o resto do catálogo —
   implementado sem esforço adicional na Wave 13-4b, ao contrário do que
   sucederia num ficheiro JSON.
4. **Evita a ambiguidade de dois "roots".** Os stores JSON vivem em
   `<cwd>/runtime-data`, que pode divergir do bind mount `/data`
   (`WebDashboardService.cs:2347-2348` vs `:2388-2389`). A BD do catálogo
   tem um caminho único e é a autoridade do domínio.
5. **Preserva o princípio "não duplicar a política".** A pura JSON store
   criaria uma segunda política paralela, fora da transacção do catálogo,
   divergente do caminho `ChannelSource` que a 13-3 já lê.
6. **Distingue-se deliberadamente de `source_priority_policies` num ponto:**
   a chave de unicidade é `ScopeKey` (uma string já canónica), não só `Scope`;
   isto corrige o defeito de índice conhecido (§5.5) sem tocar na tabela
   existente (que **não** é alterada nesta wave).

**Não é decisão desta wave** a integração com o `PlaylistComposerService`/Discovery
nem alterações ao `MatchPlan`/`DispatcharrSyncService` (ver §13 e §14).

## 5. Data Model

> **Nota:** modelo implementado (§0). A migration `AddSourceSelectionPolicies`
> é aditiva e reversível (§12).

### 5.1 Entidade

`SourceSelectionPolicyEntity` (mesmo namespace do catálogo,
`m3uCrawler/Services/Catalog/`):

| Campo | Tipo | Regras | Notas |
|---|---|---|---|
| `Id` | `long` | PK, `ValueGeneratedOnAdd` | espelha `SourcePriorityPolicyEntity.Id` |
| `ScopeKey` | `string` | **obrigatório**, `HasMaxLength(160)`, **índice único** | `"global"` ou `"channel:<CanonicalChannelKey>"` |
| `CanonicalChannelKey` | `string?` | `null` para a linha global; `<=120` (igual a `CanonicalChannelEntity.Key`) | identidade lógica estável |
| `MaxSourcesPerChannel` | `int` | obrigatório, `>= 0` (`0` = válido, não selecciona nada; negativos inválidos) | default `10` |
| `PreferDistinctProviders` | `bool` | obrigatório | default `true` |
| `MaxSourcesPerProvider` | `int?` | `null` = sem limite; se não-nulo, `>= 1` (`0`/negativos inválidos) | default `null` |
| `AllowFallbackToSameProvider` | `bool` | obrigatório | default `true` |
| `CreatedAtUtc` | `DateTime` | obrigatório | |
| `UpdatedAtUtc` | `DateTime` | obrigatório | |

### 5.2 Escopo

**[DECISÃO]** O escopo é codificado **numa única coluna** `ScopeKey`:

- `"global"` — linha por defeito do sistema;
- `"channel:<CanonicalChannelKey>"` — override por canal (implementado na 13-4b,
  política completa que substitui a global).

O `ScopeKey` é a chave de unicidade. `CanonicalChannelKey` é mantida como
coluna derivada legível (e para o resolver validar consistência), mas **não**
é a chave de unicidade.

### 5.3 Relação com o canal canónico

**[DECISÃO]** Não há Foreign Key. A referência é por
`CanonicalChannelKey` (string estável), não por `CanonicalChannelId`.

Fundamento **[FACT]**:
- `CanonicalChannelEntity.Key` é única e imutável após criação
  (`CatalogEntities.cs:16-21,58`; `CatalogResolver.cs:912-938`);
- `Id` é autoincrement e **dependente do ambiente**
  (`ChannelCatalogDbContext.cs:46-48`), pelo que não é estável entre
  instalações/importações;
- o roadmap determina usar a chave lógica estável
  (`docs/IMPLEMENTATION_ROADMAP.md:3788-3791`);
- `AffinityGroupEntity` já fez essa migração (`CatalogEntities.cs:158-161`);
- `MatchPlan.ChannelDecision.CanonicalChannelId` está explicitamente marcado
  como "transitório" (`m3uCrawler/Models/MatchPlan.cs:96-101`).

**[FACT]** Em contraste, `ChannelSourceEntity` ainda usa `CanonicalChannelId`
+ nav (`CatalogEntities.cs:699-731`) e `SourceSelectionStage` agrupa por
`CanonicalChannelId` (`SourceSelectionStage.cs:162-164`). Isto é uma
**inconsistência existente do domínio**, não resolvida pela 13-4 — a política
apenas evita propagá-la no seu próprio schema. Ver §13.1.

### 5.4 Defaults

**[DECISÃO]** Os defaults da entidade são exactamente os da 13-3:
`MaxSourcesPerChannel=10`, `PreferDistinctProviders=true`,
`MaxSourcesPerProvider=null`, `AllowFallbackToSameProvider=true`
(`SourceSelectionStage.cs:16-23`) — ver §9.

### 5.5 Constraints e índices

| Constraint | Definição implementada |
|---|---|
| PK | `Id` |
| Unique | `ScopeKey` (único) |
| Check | `MaxSourcesPerChannel >= 0` |
| Check | `MaxSourcesPerProvider IS NULL OR MaxSourcesPerProvider >= 1` |
| Check | `ScopeKey = 'global'` ⟺ `CanonicalChannelKey IS NULL` (invariante lógica; check opcional) |

**[FACT]** O defeito conhecido do análogo é o índice único **apenas** em
`Scope` (`ChannelCatalogDbContext.cs:337`), que impediria dois overrides por
canal distintos se `Scope` fosse reutilizado. A 13-4 evita-o usando `ScopeKey`
canónico como chave única. A tabela existente
`source_priority_policies` **não** é corrigida nesta wave (fora de escopo).

## 6. Runtime Resolution

Fluxo implementado:

```text
BD do catálogo (source_selection_policies)
   │  linha "global" (criada lazily),
   │  overrides "channel:<Key>" (13-4b)
   ▼
SourceSelectionPolicyResolver.LoadEffectivePoliciesAsync()
   │  snapshot por execução (2 queries; sem cache entre execuções)
   │  Resolve(key): 1. override por canal → se existir, usa-o
   │                 2. senão, linha global
   │                 3. senão, SourceSelectionDefaults.DefaultPolicy (fallback final)
   ▼
SourceSelectionPolicySet (ISourceSelectionPolicyProvider)
   │
   ▼
SourceSelectionStage.ApplyAsync(streams, provider)
   │  (permanece persistence-free; resolve por CanonicalChannel.Key)
   ▼
output/playlist.m3u
```

`ResolveEffectiveAsync(key)` (e `ResolveGlobalAsync()`) mantêm-se para resolução
pontual; o caminho de publicação usa a leitura em lote.

Pontos de integração:

- **[DECISÃO implementada]** Serviço `SourceSelectionPolicyResolver`
  (`m3uCrawler/Services/SourceSelection/SourceSelectionPolicyResolver.cs`),
  devolvendo a política efectiva (global ou override por canal). O *fallback*
  final é `SourceSelectionDefaults.DefaultPolicy`
  (`SourceSelectionStage.cs:16-23`). A leitura em lote é
  `LoadEffectivePoliciesAsync()` (2 queries; `SourceSelectionPolicySet`).
- **[DECISÃO implementada]** `SourceSelectionStage` **não** ganha persistência:
  continua a ser o componente de aplicação, recebendo a política (ou o
  `ISourceSelectionPolicyProvider`, na 13-4b) como argumento
  (`ISourceSelectionStage.ApplyAsync(..., policy|provider, ...)`,
  `SourceSelectionStage.cs:30-36`).
- **[DECISÃO implementada]** `Program.cs:540-541` e `Program.cs:1108-1109`
  chamam o resolver em vez da constante. Como o resolver devolve os defaults
  quando não existe linha, o comportamento 13-3 mantém-se idêntico (§9).
- **[DECISÃO implementada]** O resolver lê a linha global através do
  `CatalogResolver` (`GetOrCreateGlobalSourceSelectionPolicyAsync`, espelhando
  `GetOrCreateGlobalPriorityPolicyAsync`), mantendo o acesso à BD na fachada
  existente.
- **[DECISÃO implementada]** A criação da linha global é **lazy**, como no análogo
  (`CatalogResolver.cs:1701-1722`) — uma instalação que nunca configure a
  política não ganha linha nenhuma até ser necessário.

## 7. Dashboard Integration

### 7.1 Endpoint

**[DECISÃO implementada]** Rota
`GET/POST /api/catalog/source-selection-policies`, inserida na cadeia linear
de `HandleRequestAsync` (`WebDashboardService.cs:275`) **depois** do gate
global, exactamente ao lado de `/api/catalog/priority-policies`
(`:1456-1500`):

- `GET` → devolve a linha global (cria-a lazily via resolver) projectada por
  um novo `SourceSelectionPolicyToJson`, simétrico a
  `PriorityPolicyToJson` (`:3183-3196`).
- `POST` → desserializa um novo `SourceSelectionPolicyPayload` com
  `[JsonPropertyName]` camelCase, simétrico a `PriorityPolicyPayload`
  (`:2965-2972`), valida (§8), faz *upsert* via resolver e devolve a projecção.
- Serialização com o `JsonOptions` partilhado (`:2522-2527`) e helper
  `WriteJsonAsync` (`:2592`).
- Os endpoints acima gerem a **linha global**. A Wave 13-4b acrescentou
  `GET/POST /api/catalog/source-selection-policies/channels` e
  `GET/DELETE /api/catalog/source-selection-policies/channels/{key}` para os
  overrides por canal (substituição completa; identidade = chave canónica;
  mesmo gate de auth/CSRF) — ver §0.1.

### 7.2 Payload

`SourceSelectionPolicyPayload` (camelCase, com `[JsonPropertyName]`):
`maxSourcesPerChannel`, `preferDistinctProviders`, `maxSourcesPerProvider`
(nullable), `allowFallbackToSameProvider` e (13-4b) `scopeKey`/chave canónica.

### 7.3 UI

**[DECISÃO implementada]** Um cartão *data-driven* na área **Catálogo**,
espelhando o formulário *data-driven* já usado para a Stream Validation
(`GET/POST /api/validation/policy`,
`WebDashboardService.cs:2345-2381`). O cartão expõe os quatro campos globais; a
Wave 13-4b acrescentou a gestão de overrides por canal (lista/edita/elimina,
com texto de ajuda de substituição completa e `0` válido).

**[FACT]** Não existe hoje qualquer componente de UI para
`/api/catalog/priority-policies` que possa ser copiado linha-a-linha; o padrão
de UI mais próximo é o formulário de validação de streams.

## 8. Validation Contract

Contrato derivado do algoritmo existente
(`docs/architecture/dispatcharr-source-selection.md` §4, §6, §8) — não inventa
semântica nova.

| Campo | Regra | Justificação |
|---|---|---|
| `MaxSourcesPerChannel` | **obrigatório**, inteiro `>= 0` | é o tecto absoluto do algoritmo (§6). **Alteração ratificada na 13-4:** `0` é válido e selecciona zero fontes; negativos são inválidos |
| `MaxSourcesPerProvider` | `null` = sem limite; se não-nulo, inteiro `>= 1` | §2 e §6: limite opcional aplicado em ambas as fases; `0`/negativos inválidos |
| `PreferDistinctProviders` | booleano | activa/desactiva a Fase A (§6) |
| `AllowFallbackToSameProvider` | booleano | controla a Fase B (§6) |

Interacções documentadas:

- **[FACT]** Se `MaxSourcesPerProvider >= MaxSourcesPerChannel`, o limite por
  fornecedor é **inerte** (o tecto por canal domina, §6). Isto **não** é erro:
  deve ser aceite.
- **Risco de sub-preenchimento.** Com `AllowFallbackToSameProvider=false` e
  mais lugares do que fornecedores distintos, a Fase B não preenche os lugares
  restantes (§6) — o canal publica menos de `MaxSourcesPerChannel` fontes. Isto
  é comportamento correcto do algoritmo; a UI pode avisar, mas o servidor
  **não** deve transformar isto em erro.
- `ProviderIdentity.Unknown` colapsa todos os fornecedores desconhecidos numa
  única identidade (§7), pelo que diversidade/limite os tratam como o mesmo
  fornecedor.

Regras de valores inválidos (decididas na Wave 13-4):

- `MaxSourcesPerChannel`: `>= 0`; **`0` é válido** e produz zero selecções;
  negativos são inválidos.
- `MaxSourcesPerProvider`: `null` = sem limite; se não-nulo, `>= 1`; `0` e
  negativos são inválidos.
- **Sem limite superior** imposto a `MaxSourcesPerChannel`: o algoritmo não
  define tecto (`SourceSelectionStage.cs:13-14`) e a 13-4 não lhe acrescentou
  nenhum (§13.2).

## 9. Backwards Compatibility

**[DECISÃO]** Cenário de upgrade e garantias:

| Cenário | Comportamento |
|---|---|
| BD existente, tabela nova vazia | Resolver devolve `SourceSelectionDefaults.DefaultPolicy` = `10/null/true/true` → **idêntico à 13-3** |
| Criação lazy da linha global | Escreve exactamente `10/null/true/true` → **sem alteração silenciosa** |
| Migration `AddSourceSelectionPolicies` | Aditiva: cria tabela + índice; não altera/remove tabelas ou dados existentes |
| Instalação nova | Linha global criada apenas quando resolvida/consultada; sem efeito operacional |
| Legacy adoption | A tabela nova é **excluída** do `LegacyConfigurationEvidenceEvaluator`, tal como `source_priority_policies` (`LegacyConfigurationEvidenceEvaluator.cs:35-37`) — não cria falsa evidência de instalação antiga |

**[FACT]** A migration é coberta pelo backup pré-migration automático
(`ChannelCatalogBootstrapper.cs:219-243`), que gera `.pre-migration-<ts>.db`
para qualquer migration pendente.

**[DECISÃO]** Não há redução de funcionalidade: nenhum caminho perde
capacidade; apenas se acrescenta configurabilidade com defaults iguais aos
actuais.

## 10. Security / Authorization

**[DECISÃO]** Reutilizar integralmente o gate existente, sem novo modelo de
permissões:

- O endpoint herda o **único** gate global (sessão humana ou `--web-token`);
  métodos mutantes exigem CSRF (`WebDashboardService.cs:334-378`, `:364-376`).
  Não há autenticação por handler.
- A credencial de máquina `--web-token` continua a usar comparação em tempo
  constante (`FixedTimeEquals`), conforme `configuration-lifecycle.md`
  §"Precedência `--web-token` vs sessão humana".
- A política **não contém credenciais** (só inteiros/booleanos), pelo que não
  há risco de exposição de secrets nem necessidade de
  `CredentialSanitizer`. Deve, ainda assim, respeitar a regra transversal de
  "nunca logar credenciais".

**[FACT]** **Não existe log de auditoria de alterações administrativas.**
Alterar `MaxSourcesPerChannel` (ou qualquer outra configuração) não deixa
registo de quem/o quê/quando. **[PENDENTE]** A Wave 13-4 **não** introduz
qualquer auditoria: a lacuna mantém-se e fica para uma wave transversal (§13.5).

## 11. Testing Strategy

Estratégia de testes registada para a Wave 13-4.

| Nível | Testes previstos | Padrão de referência |
|---|---|---|
| Unidade (resolver) | fallback: sem linha → `DefaultPolicy`; com linha global → valores persistidos; override por canal → global → default (implementado na 13-4b) | — |
| Persistência EF | *get-or-create* idempotente (2 chamadas → 1 linha); *upsert* actualiza `UpdatedAtUtc`; unicidade de `ScopeKey` (2.ª linha global → violação); round-trip dos 4 valores | `TestDbContextFactory` + `ChannelCatalogBootstrapper(path).InitializeAsync()` + `new CatalogResolver(factory, path)` (`ChannelCatalogIntegrationTests.cs:569-588`) |
| Migration | migration aditiva: tabela existe e está vazia após `MigrateAsync`; dados pré-existentes intactos; reversibilidade (`IMigrator` + `__EFMigrationsHistory`) | `Phase93MigrationReversibilityTests.cs` |
| Dashboard HTTP | `GET` devolve defaults; `POST` válido persiste; `POST` inválido → `400`; mutante sem CSRF → `403` `csrf-invalid`; sem sessão → `401` | `DashboardHarness` (`DashboardBootstrapEndpointTests.cs:723-817`), `[Collection("DashboardStaticState")]` (`DashboardStaticStateCollection.cs:13`), padrão CSRF em `:278-289` |
| Regressão | BD legacy sem linha → resolver devolve `10/null/true/true`; `SourceSelectionStage` comporta-se como na 13-3 com a política resolvida | `SourceSelectionStageTests.cs` |

**[FACT] Lacuna conhecida nos testes:** `SourceSelectionStageTests.cs` cria
BDs de teste com prefixo `source-selection-stage-`
(`SourceSelectionStageTests.cs:31`), que **não** está em
`TestTempDb.KnownTestPrefixes` (`TestTempDb.cs:83`), pelo que esses ficheiros
temporários não são varridos pelo sweeper. O registo dos prefixos usados pelos
testes em `KnownTestPrefixes` ou `KnownTestPrefixesAltSeparator`
(`TestTempDb.cs:83,113`) é uma questão de higiene da suite.

**[DECISÃO]** Os testes de UI (se existirem) seguem o padrão *data-driven* da
Stream Validation; não há teste automatizado de rendering da página para além
do que já existe.

## 12. Migration Strategy

Implementada na Wave 13-4:

1. **Nome:** migration `AddSourceSelectionPolicies`
   (ficheiros em `m3uCrawler/Services/Catalog/Migrations/`), adicionada ao
   final da cadeia actual de 14 migrations.
2. **Conteúdo:** `CreateTable("source_selection_policies")` com PK `Id`
   autoincrement, colunas de §5.1 e índice único em `ScopeKey`; `Down` faz
   `DropTable` (reversível).
3. **Sem seed:** nenhuma `InsertData` — a linha global é criada lazily pelo
   resolver, evitando que a tabela se torne evidência de instalação legacy
   (§9). **[FACT]** Nenhuma das 14 migrations actuais usa `InsertData`.
4. **Backup/lock:** já garantidos pelo `ChannelCatalogBootstrapper`
   (`:219-243`, `:62-73`); não é necessário código novo.
5. **Não destrutiva:** não altera `source_priority_policies` nem qualquer
   tabela existente.
6. **Reversibilidade:** testada no estilo de
   `Phase93MigrationReversibilityTests` (`IMigrator` + `__EFMigrationsHistory`).
7. **Ordem operacional:** aplicar primeiro em cópia isolada do `runtime-data`
   (prática já estabelecida em `AGENTS.md` §9 e `configuration-lifecycle.md`
   §"Upgrade em produção") antes de qualquer instalação real.

## 13. Decisões (fechadas na Wave 13-4)

### 13.1 Chave canónica: `Key` vs `Id`

**[DECISÃO ratificada]** A identidade é por `CanonicalChannelKey` (string
estável e imutável), **não** por `CanonicalChannelId`; o `ScopeKey` é a chave de
unicidade da tabela. A Wave 13-4b implementou os overrides por canal já com
identidade por `Key`. A inconsistência existente de
`ChannelSourceEntity`/`SourceSelectionStage` a usar `CanonicalChannelId` no
agrupamento (`CatalogEntities.cs:699-731`, `SourceSelectionStage.cs:162-164`)
permanece **fora de escopo**: a migração completa `Id → Key` do domínio **não**
foi feita.

### 13.2 Limite superior de `MaxSourcesPerChannel`

**[DECISÃO ratificada]** **Nenhum limite superior é imposto** pela 13-4. O
algoritmo não define tecto (`SourceSelectionStage.cs:13-14`) e a política aceita
qualquer inteiro `>= 0`.

### 13.3 Semântica de zero/negativos

**[DECISÃO ratificada]**

- `MaxSourcesPerChannel`: `>= 0`. **`0` é válido e selecciona zero fontes**;
  negativos são inválidos. É uma **alteração semântica deliberada** do selector
  congelado (o contrato anterior era `>= 1`), decidida por instrução explícita
  de produto.
- `MaxSourcesPerProvider`: `null` = sem limite; se não-nulo, `>= 1`. `0` e
  negativos são inválidos.

### 13.4 Override por canal: 13-4 ou 13-4b

**[DECISÃO ratificada e implementada na Wave 13-4b]** Os overrides por canal
**não** fizeram parte da 13-4; foram implementados na Wave 13-4b, de forma
aditiva e sem alteração de schema (§0.1). A 13-4 manteve-se na linha global e no
respectivo UI.

### 13.5 Auditoria

**[PENDENTE]** A Wave 13-4 **não** introduz auditoria de mutações
administrativas; a lacuna (§10) mantém-se e fica para uma wave transversal de
auditoria.

### 13.6 Rota e colocação na UI

**[DECISÃO ratificada e implementada]** `GET/POST
/api/catalog/source-selection-policies` e cartão *data-driven* na área
**Catálogo** (apenas global), sob o gate único de autenticação/CSRF (§0, §7).

### 13.7 EF vs JSON (ratificação)

**[DECISÃO ratificada]** Persistência em **EF Core na BD do catálogo**
(`source_selection_policies`), pelas razões factuais de §3. A alternativa JSON
não foi adoptada.

## 14. Wave 13-4 Implementation Plan (executado)

Passos executados. **Não incluídos** (e assim permanecem): auditoria,
Composer/Discovery, alterações a `MatchPlan`/`DispatcharrSyncService`. Os
overrides por canal foram implementados na Wave 13-4b (§0.1).

1. **Entidade:** `SourceSelectionPolicyEntity` em
   `m3uCrawler/Services/Catalog/CatalogEntities.cs` (§5.1).
2. **Mapeamento:** `DbSet<SourceSelectionPolicyEntity>` + `OnModelCreating`
   (tabela, colunas, índice único em `ScopeKey`) em `ChannelCatalogDbContext.cs`.
3. **Migration:** `AddSourceSelectionPolicies`, aditiva e reversível (§12).
4. **Resolver de catálogo:** `CatalogResolver.GetOrCreateGlobalSourceSelectionPolicyAsync`
   (lazy; defaults `10/true/null/true`).
5. **Serviço de resolução:** `SourceSelectionPolicyResolver` com *fallback*
   final `SourceSelectionDefaults.DefaultPolicy` (§6).
6. **Wiring:** resolução da política nos dois pontos de publicação
   (`Program.cs:540-541`, `:1108-1109`).
7. **Exclusão legacy:** tabela excluída do
   `LegacyConfigurationEvidenceEvaluator` (§9).
8. **Endpoint:** `GET/POST /api/catalog/source-selection-policies` com payload e
   projecção simétricos aos de prioridades (§7).
9. **UI:** cartão *data-driven* na área Catálogo, apenas global (§7.3).
10. **Validação:** contrato de §8, incluindo `MaxSourcesPerChannel >= 0` com `0`
    válido (alteração semântica §13.3).
11. **Testes:** unidade do resolver, persistência EF, migration (aditiva +
    reversível), endpoint HTTP (auth/CSRF/validação) e regressão da política
    resolvida.
12. **Documentação:** `docs/architecture/dispatcharr-source-selection.md`
    (persistência implementada), `docs/IMPLEMENTATION_ROADMAP.md` e
    `CHANGELOG.md` `[Unreleased]`.
13. **Verificação:** `dotnet build m3uCrawler.sln --configuration Release`
    (0 warnings, 0 errors) e
    `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo`.

Critérios de aceitação: defaults idênticos à 13-3 quando não há linha; migration
aditiva e reversível; endpoint sob o gate global (CSRF em mutações); `0` válido
e negativos inválidos; sem regressão nos testes existentes da 13-1/13-3;
documentação actualizada.

Follow-up explícito: auditoria administrativa (wave transversal). Os overrides
por canal (Wave 13-4b) foram entretanto implementados (§0.1).

## Ficheiros de referência

- `m3uCrawler/Services/SourceSelection/SourceSelectionPolicyResolver.cs`
- `m3uCrawler/Services/SourceSelection/SourceSelectionPolicySet.cs`
- `m3uCrawler/Services/SourceSelection/SourceSelectionModels.cs`
- `m3uCrawler/Services/SourceSelection/SourceSelectionStage.cs`
- `m3uCrawler/Services/Catalog/SourceSelectionPolicyScopes.cs`
- `m3uCrawler/Services/Catalog/ChannelCatalogDbContext.cs`
- `m3uCrawler/Services/Catalog/CatalogEntities.cs`
- `m3uCrawler/Services/Catalog/CatalogResolver.cs`
- `m3uCrawler/Services/Catalog/ChannelCatalogBootstrapper.cs`
- `m3uCrawler/Services/Catalog/Migrations/`
- `m3uCrawler/Services/Configuration/LegacyConfigurationEvidenceEvaluator.cs`
- `m3uCrawler/Services/Validation/StreamValidationPolicyStore.cs`
- `m3uCrawler/Services/Configuration/AppSettingsStore.cs`
- `m3uCrawler/Services/Configuration/ConfigurationLifecycleStore.cs`
- `m3uCrawler/Services/WebDashboardService.cs`
- `m3uCrawler/Program.cs`
- `docs/architecture/dispatcharr-source-selection.md`
- `docs/architecture/configuration-lifecycle.md`
- `docs/IMPLEMENTATION_ROADMAP.md:3788-3791`

## Testes de referência

- `m3uCrawler.Tests/ChannelSourceSelectorTests.cs`
- `m3uCrawler.Tests/SourceSelectionStageTests.cs`
- `m3uCrawler.Tests/SourceSelectionPolicyResolverTests.cs`
- `m3uCrawler.Tests/SourceSelectionPolicyChannelPersistenceTests.cs`
- `m3uCrawler.Tests/SourceSelectionPolicyChannelEndpointTests.cs`
- `m3uCrawler.Tests/SourceSelectionPolicyRuntimeIntegrationTests.cs`
- `m3uCrawler.Tests/ChannelCatalogIntegrationTests.cs` (`:569-588`)
- `m3uCrawler.Tests/Phase93MigrationReversibilityTests.cs`
- `m3uCrawler.Tests/DashboardBootstrapEndpointTests.cs` (`:723-817`, `:278-289`)
- `m3uCrawler.Tests/DashboardStaticStateCollection.cs`
- `m3uCrawler.Tests/TestTempDb.cs`

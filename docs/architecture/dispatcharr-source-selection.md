# Dispatcharr Source Selection (PHASE 13)

> Estado: **Waves 13-1, 13-3, 13-4, 13-4b e 13-5 implementadas.** 13-1: política pura,
> determinística, sem I/O. 13-3: aplicação da política ao pipeline Telegram antes
> da publicação em `output/playlist.m3u` (§10). 13-4: persistência **global** da
> política na BD do catálogo, resolver e exposição no Dashboard (§10.1;
> `docs/architecture/phase-13-4-source-selection-policy.md`). 13-4b: **overrides
> por canal** — política completa chaveada por `CanonicalChannel.Key` que
> substitui a global, resolvida em lote por execução e gerida no Dashboard
> (§10.2). 13-5: **preview/dry-run read-only + métricas** sobre o catálogo, com
> endpoint `GET` e cartão no Dashboard, a correr o **mesmo** `SourceSelectionStage`
> da produção, sem publicação nem escrita (§10.3). **Não** implementados: os
> produtores de Quality/EPG, a correcção do reset de `Source.Priority` e a
> integração no composer/`MatchPlan`/`DispatcharrSyncService`
> (ver `docs/IMPLEMENTATION_ROADMAP.md` §32.19). A **Wave 13-6**
> (`MatchPlan` + `DispatcharrSourceSelection`, cleanup selectivo e teste 100→10)
> permanece **não implementada e fora de âmbito da 13-5**.

## 1. Finalidade

A PHASE 13 introduz controlo sobre **quantas** das fontes descobertas de um
canal são publicadas no Dispatcharr. Actualmente um canal pode receber todas as
fontes descobertas (dezenas/centenas). A Wave 13-1 implementa apenas a
**unidade algorítmica central**:

```text
Candidates + Policy
        ↓
SourceSelectionResult
```

sem filesystem, base de dados, HTTP, Dispatcharr, scheduler, Telegram ou
Dashboard. Isto permite validar o algoritmo isoladamente antes de alterar
contratos de produção.

Distinção de domínio: **Channel ≠ ChannelSource**. Um canal tem várias fontes;
esta política decide quais dessas fontes (associações) são seleccionadas para a
publicação — não limita o canal canónico nem o catálogo interno.

## 2. Tipos

- `SelectionCandidate` (`Services/SourceSelection/SourceSelectionModels.cs`) —
  projecção em memória de um `ChannelSource`: `StreamUrl`, `SourceId`,
  `SourcePriority`, `Quality`, `Epg`, `Availability`, `LastResponseTimeMs`,
  `ExternalStreamId`, `Provider` (`ProviderIdentity`), `IsWorking`.
  Reutiliza os enums de domínio `StreamQuality`, `EpgState` e `AvailabilityState`.
- `SourceSelectionPolicy` — `MaxSourcesPerChannel` (`>= 0`; `0` é válido e não
  selecciona nenhuma fonte, negativos inválidos), `PreferDistinctProviders`,
  `MaxSourcesPerProvider` (opcional; `null` = sem limite, `0`/negativos
  inválidos), `AllowFallbackToSameProvider`. **Sem limites hardcoded**; o
  chamador fornece os valores (ex.: 10 nos testes).
- `IChannelSourceSelector` / `ChannelSourceSelector` — a política.
- `SourceSelectionResult` — `Selected` (candidato + `Rank` + motivo),
  `Rejected` (candidato + motivo) e `TotalCandidates`.
- `SelectionReasons` — vocabulário estável dos motivos.
- `ProviderIdentity` — identidade normalizada de fornecedor para
  diversidade/deduplicação.

## 3. Elegibilidade

Elegível quando:

- URL é absoluta `http`/`https` (vazia/inválida/outro scheme → `invalid-url`);
- `IsWorking` é verdadeiro (`not-working`);
- `Availability` não é terminal — `Dead`/`Unreachable` (`unavailable`).

`Discovered`, `Reachable`, `Validated` e `Timeout` são elegíveis. Valores
desconhecidos (quality/EPG/response-time) **não** excluem.

## 4. Ranking (determinístico)

Critérios já existentes no domínio, por ordem:

1. `Availability`: `Validated` > `Reachable` > `Discovered` > `Timeout`;
2. `SourcePriority` descendente;
3. `Quality` descendente (`FourK > UHD > FHD > HD > SD > Unknown`);
4. `Epg`: `Available` > `Unknown` > `Unavailable`;
5. `LastResponseTimeMs` ascendente (`0` = desconhecido → fim);
6. desempate estável: URL normalizada (`Ordinal`) → `SourceId` → `ExternalStreamId` (`Ordinal`) → **identidade total do candidato** (Provider, URL ordinal, `SourceId`, `ExternalStreamId`, prioridade, qualidade, EPG, disponibilidade, response time, `IsWorking`).

O último critério é uma ordem total sobre todos os campos do candidato e
elimina qualquer dependência da ordem de entrada em empates extremos, sem
usar hashes, referências de objecto, relógio ou aleatoriedade. A URL
**ordinal** só intervém depois da normalizada e apenas distingue candidatos
que esta não distingue (ex.: diferenças de capitalização no path).

Os critérios do roadmap sem campo no candidato (histórico de qualidade,
recência de validação) ficam para waves seguintes. Esta wave **não** inventa
sinais externos nem consulta a rede.

## 5. Deduplicação

Antes do ranking, nada é removido; a dedup é feita sobre a lista já ordenada,
preservando o melhor rankeado de cada URL normalizada. A chave de identidade
(`ChannelSourceSelector.NormalizeUrl`, interna):

- exige `http`/`https` absoluto;
- minúsculas em scheme/host; remove porta por omissão; remove fragmento;
- **preserva** path, query e userinfo (distinguem streams legítimas);
- nunca é apresentada/logada (não é uma URL de apresentação).

Ocorrências duplicadas ficam em `Rejected` com `duplicate-url`. Graças à ordem
total (§4), o sobrevivente da deduplicação é determinístico e independente da
ordem de entrada, mesmo quando a URL normalizada coincide e só a forma original
difere.

Casos de identidade (contrato):
- fragmento (`#...`) **não** participa — `http://x/a#1` ≡ `http://x/a#2`;
- `http` e `https` são **distintos**;
- query é **preservada** — `?a=1` ≢ `?a=2`;
- userinfo é **preservado** — `user:pass@host` ≢ `host`;
- scheme/host em minúsculas e porta por omissão **normalizam** (equivalentes).

## 6. Algoritmo (duas fases)

Ranks determinísticos de 0..N-1 na ordem final.

**Fase A — diversidade** (só quando `PreferDistinctProviders`): percorre os
candidatos rankeados e selecciona o primeiro de cada fornecedor ainda não
representado, até `MaxSourcesPerChannel` ou esgotar fornecedores. Respeita
`MaxSourcesPerProvider`.

**Fase B — preenchimento**: preenche os lugares restantes com os melhores
candidatos ainda disponíveis, respeitando `MaxSourcesPerProvider`. Um
fornecedor já representado só é elegível se `AllowFallbackToSameProvider`
(→ `fallback-disabled` caso contrário).

`MaxSourcesPerChannel` é o tecto absoluto; `MaxSourcesPerProvider` aplica-se em
ambas as fases. Desde a Wave 13-4, `MaxSourcesPerChannel=0` é **válido** e
selecciona zero fontes (a guarda foi relaxada de `>= 1` para `>= 0`); valores
negativos são inválidos.

## 7. Provider desconhecido

`ProviderIdentity.Normalize` mapeia `null`/vazio e os sentinelas `unknown`,
`(unknown)` e `<unknown>` para **uma única** identidade `ProviderIdentity.Unknown`.
Como colapsam numa identidade, fontes desconhecidas **não ganham diversidade
artificial** entre si; contam como o mesmo fornecedor para
`PreferDistinctProviders` e `MaxSourcesPerProvider`. Uma identidade conhecida e
`Unknown` são fornecedores distintos.

## 8. Motivos

`diversity`, `fill`, `invalid-url`, `not-working`, `unavailable`,
`duplicate-url`, `limit-reached`, `provider-limit`, `fallback-disabled` e
`not-selected`. `Selected`/`Rejected` cobrem todos os candidatos de entrada, o
que permite, no futuro, preview/Dashboard/auditoria/métricas sem alterar o
algoritmo.

**Precedência** dos motivos de rejeição, avaliada por esta ordem:

1. `duplicate-url` (URL normalizada já considerada);
2. `invalid-url` / `not-working` / `unavailable` (inelegibilidade, independente de limites);
3. `limit-reached` (`MaxSourcesPerChannel`; domina sobre os motivos de fornecedor);
4. `provider-limit` (`MaxSourcesPerProvider`);
5. `fallback-disabled` (fornecedor representado e fallback desactivado).

Um candidato pode satisfazer mais de uma condição; é emitido o primeiro motivo
da precedência acima.

**`not-selected` é reservado e nunca emitido.** Após a Fase B, todo o candidato
elegível rejeitado cai necessariamente em `limit-reached`, `provider-limit` ou
`fallback-disabled`; o motivo existe apenas para estabilidade do vocabulário e
os consumidores não devem depender dele.

## 9. Determinismo

O mesmo conjunto de candidatos e a mesma política produzem exactamente a mesma
selecção, ordem e motivos, **independentemente da ordem de entrada**. A
ordenação usa apenas chaves estáveis (sem hash/referência/aleatoriedade) e
termina numa ordem total sobre os campos do candidato, pelo que mesmo empates
extremos são resolvidos de forma determinística e estável entre processos.
Coberto por testes que comparam a assinatura completa (Selected/rank/motivo e
Rejected/motivo) em múltiplas permutações e num dataset com empates
deliberados.

## 10. Integração no pipeline Telegram (Waves 13-3 e 13-4)

### Boundary

```
Telegram runtime: List<M3uStream>  (URL REAL, só em memória)
        │
        ▼
SourceSelectionStage.ApplyAsync(streams, policy)
   │  join: CredentialSanitizer.SanitizeUrl(realUrl) ↔ ChannelSource.StreamUrl
   │  catálogo lido via ListChannelSourcesAsync + ListSourcesAsync (READ-ONLY)
   │  provider := host normalizado da URL real
   ▼
Published = seleccionadas (rank do selector) + não correspondidas (ordem de entrada)
        │
        ▼
caller: PlaylistManagerService.SaveToM3uPlaylist(Published, path)   ← escreve a playlist
        │
        ▼
output/playlist.m3u (URLs reais)  →  DispatcharrSyncService (inalterado)
```

- **Nenhum `M3uStream` é reconstruído** a partir do catálogo; as instâncias originais
  (e as URLs reais) são preservadas.
- O estágio **não escreve ficheiros** nem persiste nada; o caller publica.

### Junção exacta

Chave = `CredentialSanitizer.SanitizeUrl(stream.Url)`, que é a chave de unicidade do
próprio catálogo (`CatalogResolver.RecordChannelSourceAsync`). Regras:

- `0` correspondências → **Unmatched** (pass-through; não conta para limites);
- `1` canal canónico → **Matched**;
- `>1` canais canónicos distintos → **Ambiguous** → pass-through;
- catálogo ausente/vazio/falha de leitura → **no-op** (tudo pass-through).

Não há matching aproximado, por título ou por host.

### Projecção

`SelectionCandidate`: `StreamUrl` = URL real (sensível, só em memória); `SourceId`,
`SourcePriority` (`Source.Priority`, actualmente 0), `ExternalStreamId`,
`Provider = ProviderIdentity.Normalize(host)`, `Availability = IsWorking ? Reachable : Dead`
(runtime), `LastResponseTimeMs` (runtime), `Quality = Unknown`, `Epg = Unknown`.

### Provider

Host normalizado: `Uri.Host`, lowercase, sem ponto final, sem prefixo `www.`, sem porta.
IPv4/IPv6 distintos. Sem resolução de aliases/CDN/proxy.

### Semântica de estados

- `ChannelSource.IsEnabled == false` → rejeitada com `source-disabled` e **não publicada**
  (não entra no selector) — alteração funcional deliberada.
- Stream sem correspondência inequívoca → pass-through.

### Defaults (13-3 → persistidos na 13-4)

`MaxSourcesPerChannel = 10`, `MaxSourcesPerProvider = null`,
`PreferDistinctProviders = true`, `AllowFallbackToSameProvider = true`
(`SourceSelectionDefaults.DefaultPolicy`). Deixaram de ser apenas in-memory: a
Wave 13-4 persiste-os como linha global e continua a usá-los como *fallback*
final quando não existe linha (§10.1).

### 10.1 Política global persistida (Wave 13-4)

- **Schema:** `SourceSelectionPolicyEntity` → tabela `source_selection_policies`
  na BD do catálogo, por migration **aditiva** `AddSourceSelectionPolicies`;
  índice único em `ScopeKey`, sem FK, identidade por `CanonicalChannelKey`.
- **Linha global:** `ScopeKey="global"` / `CanonicalChannelKey=null`, criada
  **lazy** por `CatalogResolver.GetOrCreateGlobalSourceSelectionPolicyAsync`.
- **Resolver:** `SourceSelectionPolicyResolver` resolve a política global
  efectiva; `SourceSelectionStage` mantém-se **sem persistência** e recebe a
  política explicitamente. Os dois pontos de publicação Telegram
  (`Program.cs:540-541`, `:1108-1109`) resolvem via resolver.
- **Dashboard:** `GET/POST /api/catalog/source-selection-policies` (linha
  global), sob o gate de autenticação/CSRF existente. Os endpoints de override
  por canal da Wave 13-4b estão em §10.2.
- **Semântica:** `MaxSourcesPerChannel >= 0`, com `0` válido (zero selecções) e
  negativos inválidos; `MaxSourcesPerProvider` `null` = sem limite, com
  `0`/negativos inválidos.
- **Legacy adoption:** a tabela é excluída do
  `LegacyConfigurationEvidenceEvaluator`, tal como `source_priority_policies`.

Detalhe em `docs/architecture/phase-13-4-source-selection-policy.md`.

### 10.2 Overrides por canal (Wave 13-4b)

- **Identidade:** um override é chaveado por `CanonicalChannel.Key` (string
  estável), **nunca** por `CanonicalChannelId`. O `ScopeKey` persistido é
  `channel:<CanonicalChannelKey>` (`SourceSelectionPolicyScopes.ForChannel`);
  o `ScopeKey` é a chave única da tabela (§10.1).
- **Substituição completa:** um override por canal é uma política **completa**
  que substitui a política global por inteiro quando existe. A entidade
  persistida não tem representação de "campo não definido", pelo que **não há
  merge campo a campo**.
- **Ordem de resolução:** override por canal → política global → defaults
  (`10/true/null/true`). Uma chave nula/vazia resolve directamente para a global.
- **Órfãos inertes:** os overrides não têm FK. Um override para um canal
  inexistente (ou apagado) nunca é resolvido pelo estágio; a identidade mantém-se
  válida se o canal for recriado com a mesma `Key`.
- **Semântica de valores:** inalterada — `MaxSourcesPerChannel` `0` é válido e
  publica zero fontes para esse canal, `1..N` são válidos e negativos são
  inválidos; `MaxSourcesPerProvider` `null` = sem limite, com `0`/negativos
  inválidos.
- **Resolução em lote (sem N+1):** `SourceSelectionPolicyResolver.LoadEffectivePoliciesAsync()`
  carrega a política global e todos os overrides numa leitura em lote e devolve
  um `SourceSelectionPolicySet : ISourceSelectionPolicyProvider`. São **2 queries
  por execução** (overrides + global), sem cache partilhada entre execuções. O
  `SourceSelectionStage` resolve a política efectiva **por grupo de canal
  canónico** via `CanonicalChannel.Key` através do overload
  `ApplyAsync(streams, provider, ct)`; o overload legado de política única
  mantém-se e o estágio continua **sem persistência**.
- **Selector inalterado:** o agrupamento continua a ser por `CanonicalChannelId`
  e o ranking, a diversidade e os limites mantêm a semântica da 13-1/13-4.
- **Dashboard:** `GET/POST /api/catalog/source-selection-policies/channels` e
  `GET/DELETE /api/catalog/source-selection-policies/channels/{key}`, sob o
  mesmo gate de autenticação/CSRF dos endpoints globais. A UI lista, edita e
  elimina overrides; a identidade exposta é a chave canónica.
- **Persistência:** `CatalogResolver` expõe
  `GetChannelSourceSelectionPolicyAsync`,
  `UpsertChannelSourceSelectionPolicyAsync`,
  `DeleteChannelSourceSelectionPolicyAsync` e
  `ListChannelSourceSelectionPoliciesAsync`; os métodos globais mantêm-se
  inalterados.

**Limites explícitos desta wave:** a 13-4b **não** introduziu preview/dry-run
(entregue na Wave 13-5, §10.3), produtores de Quality/EPG, métricas/auditoria
específicas da selecção, churn/estabilidade, `ProviderDefinition`,
`SelectionPolicy` separada nem integração explícita no
composer/`MatchPlan`/`DispatcharrSyncService` (ver
`docs/IMPLEMENTATION_ROADMAP.md` §32.19).

### 10.3 Preview / Dry-Run + métricas (Wave 13-5)

**Propósito.** Permitir observar *o que a política seleccionaria* — antes de a
activar em produção — sem publicar, sem escrever ficheiros e sem alterar o
catálogo. Serve de validação e de diagnóstico da configuração (global, override
por canal ou default).

**Contrato dry-run.** `SourceSelectionPreviewService.PreviewAsync(string?
canonicalChannelKey = null, CancellationToken = default)`:

- lê o catálogo (`CatalogResolver.ListCanonicalChannelsAsync` +
  `ListChannelSourcesAsync`, sempre read-only);
- sintetiza **uma `M3uStream` por `ChannelSourceEntity`** (`Url` = `StreamUrl`
  sanitizada já armazenada no catálogo; `IsWorking = Availability not in
  {Dead, Unreachable}`);
- resolve as políticas efectivas e corre o **mesmo** `SourceSelectionStage` /
  `ChannelSourceSelector` usado em produção — **não há algoritmo duplicado**;
- **não publica**, não escreve `playlist`/JSON, não muta catálogo, ownership nem
  Dispatcharr e **não cria estado persistente**.

A ausência de escrita é **estrutural** (não existe writer no caminho de preview):
o carregamento da política é read-only —
`SourceSelectionPolicyResolver.LoadEffectivePoliciesReadOnlyAsync` lê a política
global via `CatalogResolver.GetGlobalSourceSelectionPolicyAsync` (que usa
`AsNoTracking` e **nunca insere**). Se a linha global não existir, usa
`SourceSelectionDefaults.DefaultPolicy` e marca o conjunto com
`SourceSelectionPolicySet.HasExplicitGlobal = false` (em vez de a criar, como faz
o caminho de produção `LoadEffectivePoliciesAsync`). `HasExplicitGlobal` e
`HasOverride(key)` rotulam o `policyScope` de cada canal (`override` / `global` /
`default`).

**Endpoint.** `GET /api/catalog/source-selection-policies/preview[?channelKey=<CanonicalChannel.Key>]`.

- **Apenas `GET`** (outros métodos → `405`), sob o gate de autenticação
  (sessão/token; `GET` não exige CSRF) e o gate de catálogo (→ `503` "Catálogo
  não inicializado." quando o `CatalogResolver` não está disponível).
- **Filtro opcional `channelKey`**: match **exacto e case-sensitive** (`Ordinal`)
  sobre `CanonicalChannel.Key`, restringindo os canais considerados. Uma chave
  desconhecida devolve **HTTP 200** com `applied=false`, `status="channel-not-found"`
  e `channelsProcessed=0` (não é erro), registando o filtro em
  `source.channelKeyFilter`; esse campo é sanitizado
  (`CredentialSanitizer.SanitizeText`) antes da emissão.
- **Resiliência HTTP:** a rota é envolvida em `try/catch`; uma falha inesperada
  devolve `500 {"error":"preview-failed"}` e fecha sempre o response, em vez de
  deixar o cliente pendurado. O comportamento GET-only (`405`) mantém-se.
- **UI:** cartão "Preview / Dry-Run" no separador de Source Selection do
  Dashboard, com `loadSourceSelectionPreview()`.

**Contrato de `status` (Wave 13-5, MAJOR-1).** O endpoint emite um campo
top-level `status` (`SourceSelectionPreviewStatuses`), estável, distinto do
booleano `applied`, que desambigua os quatro casos antes colapsados num único
`applied=false`:

| `status` | Condição | `applied` | `channelsProcessed` |
|---|---|---|---|
| `channel-not-found` | `channelKey` não vazio sem correspondência canónica | `false` | `0` |
| `no-channels` | âmbito sem canais canónicos | `false` | `0` |
| `no-input` | há canais canónicos no âmbito mas nenhum tem `ChannelSource` | `false` | `> 0` (válido e esperado) |
| `applied` | o estágio correu | `true` | (do estágio) |

Precedência determinística: (1) `channelKey` não vazio sem match →
`channel-not-found`; (2) caso contrário, zero canais canónicos no âmbito →
`no-channels`; (3) caso contrário, canais no âmbito mas zero `ChannelSource`
(logo zero streams de entrada) → `no-input`; (4) caso contrário → `applied`.

Cada valor distingue um caso operacional diferente: (1) o **filtro não
encontrou** o canal; (2) o **catálogo não tem canais canónicos**; (3) existem
**canais no âmbito mas nenhum tem `ChannelSource`** — `applied=false` com
`channelsProcessed>0` é um estado **válido e esperado**, que não significa
"catálogo vazio"; (4) a **selecção correu**. `channelsProcessed` mantém-se como
o número de canais canónicos no âmbito e **não** foi zerado em `no-input`;
`applied` **não** foi redefinido (continua `true` apenas no caso `applied`).

**Nota de âmbito (MAJOR-2): `WriteJsonAsync` não foi alterado.** A rota de
preview passa o HTTP status explicitamente (`503` sem catálogo, `500` no
`catch`) e fecha sempre o response. O helper partilhado
`WebDashboardService.WriteJsonAsync` **não** foi modificado: o seu default
pré-existente — escrever `StatusCode = OK` quando nenhum status explícito é
passado — é dívida transversal **pré-existente**, explicitamente **fora de
âmbito** da Wave 13-5, e **não** foi estruturalmente corrigido. Os estados HTTP
do preview resultam do status passado pela própria rota, não de uma correcção
global do helper; nenhuma afirmação de que o helper foi corrigido é válida.

**Saída.** Agrupamento por canal (aditivo):
`SourceSelectionStageResult.Channels` + `SourceSelectionChannelResult`. Modelos:

- `SourceSelectionPreviewResult` — `applied`, `status`, `generatedAtUtc`,
  `inputStreamCount`, `source` e `metrics`, `channels`, `unmatched` e
  `ambiguous`; `status` é emitido também como campo top-level no JSON do
  endpoint (ver contrato acima);
- `SourceSelectionPreviewChannel` — id/key/nome canónico, `policyScope`,
  política efectiva, contagens e listas `selected`/`rejected`;
- `SourceSelectionPreviewCandidate` — candidato projectado com `rank`,
  `decision` (`selected`/`rejected`) e `reason` (vocabulário de
  `SelectionReasons`);
- `SourceSelectionPreviewUnmatched` — stream sem correspondência inequívoca,
  com `reason` `unmatched` (sem hit no catálogo) ou `ambiguous` (URL mapeada a
  mais de um canal canónico);
- `SourceSelectionPreviewMetrics`, `SourceSelectionPreviewProviderStat`,
  `SourceSelectionPreviewSourceInfo`, `SourceSelectionPreviewDecisions`
  (`selected`/`rejected`).

O endpoint emite **duas listas top-level disjuntas**: `unmatched[]` (motivo
`unmatched`) e `ambiguous[]` (motivo `ambiguous`), com a mesma forma. O
`SourceSelectionStageResult` ganhou uma lista aditiva `AmbiguousStreams`; a
semântica de produção de `Unmatched` fica **inalterada** (continua a incluir as
ambíguas, para preservar o pass-through). No preview, as duas listas são
separadas por identidade de referência (`ReferenceEqualityComparer`).

**Invariantes de contagem (Wave 13-5).** Com as listas disjuntas:

```text
inputStreamCount == candidateStreamCount + unmatchedStreamCount + ambiguousStreamCount
candidateStreamCount == selectedStreamCount + rejectedStreamCount
```

- `unmatchedStreamCount` = streams **não ambíguas** sem hit no catálogo;
- `ambiguousStreamCount` = streams cuja URL mapeia a mais de um canal canónico;
- `totalUnmatchedStreamCount = unmatchedStreamCount + ambiguousStreamCount`.

**Métricas agregadas** (`SourceSelectionPreviewMetrics`): `channelsProcessed`,
`channelsWithSources`, `candidateStreamCount`, `selectedStreamCount`,
`rejectedStreamCount`, `unmatchedStreamCount`, `ambiguousStreamCount`,
`totalUnmatchedStreamCount`, `channelsAtChannelLimit`,
`channelLimitRejectionCount`, `providerLimitRejectionCount`,
`diversitySelectionCount`, `distinctProviderCount`, `fillSelectionCount`,
`fallbackDisabledRejectionCount`, `sourceDisabledRejectionCount`,
`rejectionCounts` e `providerDistribution` (`provider`, `selectedCount`,
`channelCount`).

- `diversitySelectionCount` (correcção Wave 13-5; substitui o enganador
  `distinctProviderSelectionCount`) = número de selecções com motivo
  `diversity`, somadas por canal.
- `distinctProviderCount` (novo) = número de fornecedores distintos com pelo
  menos uma selecção (= `providerDistribution.Count`).

**Sanitização.** Todas as URLs emitidas passam por
`CredentialSanitizer.SanitizeUrl` (defensivo, mesmo que o catálogo já guarde URLs
sanitizadas) e `SourceSelectionPreviewUnmatched.Title` passa por
`CredentialSanitizer.SanitizeText`. Os enums são achatados via `ToString()`; a
origem é o rótulo curto `catalog`, sem caminhos de filesystem nem internals do
`CatalogResolver`. Nenhuma credencial é exposta.

**Limitações (honestas).**

- O input é o **catálogo** (`ChannelSourceEntity`), **não** um conjunto de
  descoberta Telegram ao vivo; a disponibilidade do catálogo é usada como proxy
  do estado de funcionamento (`IsWorking = Availability not Dead/Unreachable`).
- A ambiguidade é exposta em lista própria (`ambiguous[]`); `unmatched` e
  `ambiguous` são **listas disjuntas** por construção, com
  `unmatchedStreamCount` a contar apenas as não-ambíguas e
  `totalUnmatchedStreamCount` a somar ambas.
- `fillSelectionCount` é o proxy da Fase B do selector (motivo `fill`).
- `channelsProcessed` é o número de canais canónicos **no âmbito** (após o
  filtro `channelKey`), **incluindo** os que não têm `ChannelSource`; esses não
  aparecem em `channels`. A UI rotula o cartão como "Canais no âmbito".
- Apenas os dois pontos de publicação Telegram aplicam selecção em produção; os
  restantes pontos de publicação permanecem fora de âmbito (pré-existente).

**Limitação de paridade: ResponseTime.** O preview alimenta
`ResponseTime = ChannelSourceEntity.LastResponseTimeMs`, isto é, usa o valor
**persistido** quando presente. Na prática essa coluna é escrita como `0` no
insert e nunca é actualizada (`CatalogResolver.cs:1422`; ramo de update
`:1392-1404`), pelo que o valor está normalmente a `0`/indisponível e **não**
representa o `DurationMs` da probe ao vivo. `ChannelSourceObservationEntity` é
append-only, não tem flag de sucesso e só é escrito por um POST controlado pelo
cliente (`WebDashboardService.cs:2156`); o pipeline de ingestão
(`PipelineIngestionService.cs:250-264`) ignora `stream.ResponseTime`. O valor
real em produção é o stopwatch ao vivo `DurationMs` da probe exacta
(`M3uTesterService.cs:550,555`). Por isso o preview **não consegue** reproduzir a
ordenação de produção por `ResponseTimeKey`
(`ChannelSourceSelector.cs:128,260-261`): como o campo persistido é normalmente
`0`/indisponível, a ordenação do preview **pode divergir** da produção. É uma
limitação documentada, **não** uma garantia: quando os candidatos empatam nas
primeiras quatro chaves de ranking, a ordem — e portanto o conjunto seleccionado
— pode diferir da produção. Não se afirma paridade de ordenação com a produção.

**Fora de âmbito explícito (13-5).** O contrato `MatchPlan` +
`DispatcharrSourceSelection`, o ownership/cleanup selectivo, o teste 100→10,
`ProviderDefinition`, uma `SelectionPolicy` de ranking separada,
`MinimumValidatedSources` e churn/estabilidade pertencem à Wave 13-6 (ou
posterior) e **não** são implementados aqui. `RunReport.SourceSelection`
permanece **inalterado** (só contagens); as métricas ricas são âmbito exclusivo do
preview. A **13-6 permanece não implementada**.

### Identidade canónica no loader (Wave 9C.6)

`CatalogResolver.ListChannelSourcesAsync` passou a incluir a navegação
`CanonicalChannel` (`.Include(cs => cs.CanonicalChannel)`), pelo que
`ChannelSource.CanonicalChannel.Key` está agora acessível ao caminho de
selecção de fontes **sem** queries adicionais. Isto satisfaz o pré-requisito de
identidade para a política por canal.

O `SourceSelectionStage` **continua a agrupar por `CanonicalChannelId`**
(semântica do selector, ranking e limites inalterados). A partir da Wave 13-4b,
a pesquisa de política por canal passou a usar `CanonicalChannel.Key` (resolver e
contrato do estágio), pelo que a política persistida e a sua resolução usam a
identidade lógica estável. A migração completa `Id → Key` do domínio **não** foi
feita: continuam dependentes de `Id` as FKs de
`channel_aliases`/`channel_sources`/`ordering_items`, `source_priority_policies`
por canal, `dispatcharr_channel_ownerships`, `matching_audits`, `MatchPlan` e o
agrupamento do `SourceSelectionStage`.

### Pontos de publicação integrados

- single-cycle Telegram (`Program.cs`, `telegram_playlist_<ts>.m3u`);
- manutenção Telegram (`RunTelegramMaintenanceCycle`, após `MergeStreams`,
  `output/playlist.m3u`).

### Diagnóstico

`RunReport.SourceSelection` (`SourceSelectionReport`) transporta apenas contagens
agregadas (matched/ambiguous/selected/rejected/unmatched + contagens por motivo).
Nunca contém URLs, usernames, passwords ou tokens.

### Segurança

A URL real existe apenas em memória e no artefacto funcional (playlist). O catálogo
continua sanitizado; a projecção não escreve no catálogo. Qualquer diagnóstico passa
por contagens (sem URL) — não há novo artefacto persistente com credenciais.

### Fora de âmbito (13-3)

Correcção do reset de `Source.Priority`, produtores de Quality/EPG, persistência de
`LastResponseTimeMs`, integração no composer/discovery/validation, alterações ao
`MatchPlan`/`DispatcharrSyncService`/ownership, `BuildPlanFromCompositionAsync`,
`ProviderDefinition`, identidade de conta Xtream. Não faz `ProviderDefinition`
completa. A persistência/Dashboard da política deixou de ser fora de âmbito na
Wave 13-4 (§10.1), os **overrides por canal** na Wave 13-4b (§10.2) e o
**preview/dry-run + métricas** na Wave 13-5 (§10.3). Permanecem fora de âmbito a
Wave 13-6 (contrato `MatchPlan` + `DispatcharrSourceSelection`, cleanup
selectivo, teste 100→10, `ProviderDefinition`, `SelectionPolicy` separada,
`MinimumValidatedSources` e churn/estabilidade) e os restantes itens da §32.19.

## 11. Testes de referência

- `m3uCrawler.Tests/ChannelSourceSelectorTests.cs` (53 testes) — unidade algorítmica:
  volume, dedup e identidade de URL, fornecedores, diversidade, limites, fallback,
  edge cases, motivos/precedência, ranking e determinismo.
- `m3uCrawler.Tests/SourceSelectionStageTests.cs` (25 testes) — integração: junção
  exacta (incl. URL Xtream), projecção (provider/RT/availability/quality/EPG),
  matching (zero/ambíguo/no-op), `source-disabled`, limites/diversidade/fallback/
  dedup, ordem de publicação, determinismo e segurança de credenciais
  (URL real preservada; relatório sem URLs/credenciais).
- `m3uCrawler.Tests/SourceSelectionPolicyResolverTests.cs` — unidade do resolver:
  global, override por canal (substituição completa) e fallback para defaults.
- `m3uCrawler.Tests/SourceSelectionPolicyChannelPersistenceTests.cs` — persistência
  dos overrides por canal (upsert/delete/lista; órfãos inertes; identidade por
  `CanonicalChannel.Key` sobrevivente a delete/recreate).
- `m3uCrawler.Tests/SourceSelectionPolicyChannelEndpointTests.cs` — endpoints HTTP
  dos overrides por canal (auth/CSRF e validação de `0`/negativos).
- `m3uCrawler.Tests/SourceSelectionPolicyRuntimeIntegrationTests.cs` — resolução
  efectiva em runtime e integração do provider no estágio.

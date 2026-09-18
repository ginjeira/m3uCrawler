# Dispatcharr Source Selection (PHASE 13)

> Estado: **Waves 13-1, 13-3 e 13-4 implementadas.** 13-1: política pura,
> determinística, sem I/O. 13-3: aplicação da política ao pipeline Telegram antes
> da publicação em `output/playlist.m3u` (§10). 13-4: persistência **global** da
> política na BD do catálogo, resolver e exposição no Dashboard (§10.1;
> `docs/architecture/phase-13-4-source-selection-policy.md`). **Não**
> implementados: overrides por canal (reservados à Wave 13-4b), preview/dry-run,
> os produtores de Quality/EPG, a correcção do reset de `Source.Priority` e a
> integração no composer/`MatchPlan`/`DispatcharrSyncService`
> (ver `docs/IMPLEMENTATION_ROADMAP.md` §32.19).

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
- **Dashboard:** `GET/POST /api/catalog/source-selection-policies` (apenas
  global), sob o gate de autenticação/CSRF existente.
- **Semântica:** `MaxSourcesPerChannel >= 0`, com `0` válido (zero selecções) e
  negativos inválidos; `MaxSourcesPerProvider` `null` = sem limite, com
  `0`/negativos inválidos.
- **Legacy adoption:** a tabela é excluída do
  `LegacyConfigurationEvidenceEvaluator`, tal como `source_priority_policies`.

Detalhe em `docs/architecture/phase-13-4-source-selection-policy.md`.

### Identidade canónica no loader (Wave 9C.6)

`CatalogResolver.ListChannelSourcesAsync` passou a incluir a navegação
`CanonicalChannel` (`.Include(cs => cs.CanonicalChannel)`), pelo que
`ChannelSource.CanonicalChannel.Key` está agora acessível ao caminho de
selecção de fontes **sem** queries adicionais. Isto satisfaz o pré-requisito de
identidade para a política por canal.

O `SourceSelectionStage` **continua a agrupar por `CanonicalChannelId`**
(semântica do selector, ranking e limites inalterados), e a política
persistida mantém identidade por `CanonicalChannelKey`
(`SourceSelectionPolicyEntity`, §10.1). A Wave 13-4b — passar a pesquisa de
política por canal do estágio a usar `CanonicalChannelKey` (resolver + contrato
do estágio) — permanece **fora de âmbito e não implementada**; a migração
completa `Id → Key` **não** foi feita.

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
Wave 13-4 (§10.1); overrides por canal permanecem fora (reservados à 13-4b) e o
preview/dry-run continua por implementar.

## 11. Testes de referência

- `m3uCrawler.Tests/ChannelSourceSelectorTests.cs` (53 testes) — unidade algorítmica:
  volume, dedup e identidade de URL, fornecedores, diversidade, limites, fallback,
  edge cases, motivos/precedência, ranking e determinismo.
- `m3uCrawler.Tests/SourceSelectionStageTests.cs` (25 testes) — integração: junção
  exacta (incl. URL Xtream), projecção (provider/RT/availability/quality/EPG),
  matching (zero/ambíguo/no-op), `source-disabled`, limites/diversidade/fallback/
  dedup, ordem de publicação, determinismo e segurança de credenciais
  (URL real preservada; relatório sem URLs/credenciais).

# Dispatcharr Source Selection (PHASE 13)

> Estado: **Wave 13-1 implementada** — política pura, determinística e sem I/O.
> A integração no composer, no `MatchPlan` e no `DispatcharrSyncService`, a
> persistência da política, o Dashboard/preview e as métricas **não** estão
> implementados nesta wave (ver `docs/IMPLEMENTATION_ROADMAP.md` §32.19).

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
- `SourceSelectionPolicy` — `MaxSourcesPerChannel`, `PreferDistinctProviders`,
  `MaxSourcesPerProvider` (opcional), `AllowFallbackToSameProvider`. **Sem
  limites hardcoded**; o chamador fornece os valores (ex.: 10 nos testes).
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
ambas as fases.

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

## 10. Fora de âmbito (Wave 13-1)

Não faz: `ProviderDefinition` completa, ingestão de Quality/EPG, persistência da
política, migrations, Dashboard/preview, integração no `MatchPlan`/sync,
alterações ao `SourceSelector`/`StreamOrderingPolicy`. A integração será uma
wave seguinte, sobre esta unidade já validada.

## 11. Testes de referência

`m3uCrawler.Tests/ChannelSourceSelectorTests.cs` (53 testes): volume
(100/1, 100/10, 100/100, <10, =10, >10), dedup e identidade de URL (fragmento,
scheme, porta por omissão, query, userinfo), fornecedores (distintos, únicos,
desconhecidos, mistos), diversidade on/off, limites por fornecedor, limite do
canal vs limite por fornecedor, fallback, edge cases (URL inválida/vazia,
não-working, disponibilidade terminal, desconhecidos, empate, vazio, unitário),
motivos e precedência, ranking e determinismo (permutações + empates
deliberados) — incluindo o teste crítico **100 fontes → no máximo
`MaxSourcesPerChannel` selecções**.

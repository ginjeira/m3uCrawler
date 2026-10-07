# 07 — Provider, Account, Source e ChannelSource

## 1. Provider

Representa o fornecedor/ecossistema técnico.

## 2. ProviderAccount

Representa uma conta concreta nesse provider.

Uma conta pode expor várias Sources.

## 3. Source

Representa uma origem que o operador quer processar.

Uma Source deve ter:
- id;
- provider;
- account;
- configuração necessária;
- enabled;
- estado operacional;
- metadados;
- timestamps.

**Regra:** se uma Source está configurada e enabled, o sistema considera que o operador quer que essa origem seja detectada/processada.

## 4. ChannelSource

É a relação entre:
`CanonicalChannel ↔ Source`

Pode conter:
- stream(s) observadas;
- identidade externa;
- estado;
- última observação;
- validação;
- eligibility;
- proveniência;
- timestamps.

Não é um CanonicalChannel alternativo.

### 4.1 Proveniência — `MatchMethod` (W5.6 + W-REVIEW-02)

`MatchMethod` identifica como a relação foi estabelecida. Valores normativos (W5.6 §5, `RecognitionMatchMethods`): `ExternalIdentityExact`, `TvgIdExact`, `CanonicalExact`, `NormalizedName`, `KnownAlias`, `ExplicitHeuristic`, `Fuzzy`, `ManualReview`. Cada método tem `MatchConfidence` normativo centralizado.

Aprovação explícita de Review (W-REVIEW-02) introduz um literal adicional:

- **`"ReviewApproval"`** — originado de `ApplyReviewApprovalAsync` (AddAlias/CreateChannel) com evidência completa persistida na `ReviewItem` (`StreamUrl`, `SourceId`, `StreamFingerprint`); `MatchConfidence = 1.0`. Proveniência distinta de `ManualReview` (regra de identidade automática): a decisão aqui é do administrador sobre uma observação registada.

O literal fica centralizado em `RecognitionMatchMethods.ReviewApproval` (constante). Não é uma nova entrada da tabela normativa de `ConfidenceByMethod` — é uma semântica diferente (resolução humana, não decisão de matching). Não tem `MatchSemanticsVersion` (não é par normativo).

## 5. Vários streams

Uma ChannelSource pode possuir múltiplas streams candidatas. Selection escolhe a que deve ser usada.

### Schema-level enforcement (W-REVIEW-02B)

A identidade persistente `(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion)` é imposta ao nível do schema por um UNIQUE filtered index (migration `20260921220000_AddChannelSourceUniqueOnFingerprint`):

```sql
CREATE UNIQUE INDEX IX_channel_sources_Channel_Source_Fingerprint_Unique
ON channel_sources (CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion)
WHERE "Fingerprint" IS NOT NULL;
```

O filtro `WHERE "Fingerprint" IS NOT NULL"` preserva a coexistência de rows legacy e de streams não-fingerprintáveis (`Fingerprint=NULL`). Múltiplas streams distintas com fingerprints diferentes sob o mesmo `(CanonicalChannelId, SourceId)` continuam permitidas (D2).

A UNIQUE é a segunda linha de defesa. A deduplicação primária continua a ser application-level via lookup em `RecordChannelSourceAsync` (`Services/Catalog/CatalogResolver.cs:2964-2981`). Em aprovação (`ApplyAddAliasAsync`, `ApplyCreateChannelAsync`), uma violação do UNIQUE é detectada, a entidade `Added` que falhou é detached, a row existente é recarregada, e os writes remanescentes (alias + review-item) são commitados.

## 6. Desactivação

Desactivar uma Source impede novas execuções para essa origem, mas não deve apagar automaticamente o catálogo nem reescrever identidades históricas.

## 7. Source identity boundary (added 2026-09-23 — W2-FU-2-CLOSE)

> **Source identity boundary** (added 2026-09-23 — W2-FU-2-CLOSE)

Source identity is defined at the canonical acquisition entry-points
(Telegram keyword → `telegram-<slug>` per
`TelegramScraperService.cs:640-641`; Xtream provider account;
registered M3U source). It is **NOT** retroactively inferred for
operations that operate on merged or ad-hoc streams.

Conclusão da auditoria semântica dos 7 call sites MUST_NOT_WIRE
realizada por W2-FU-2-CLOSE. Nenhum wiring com `sourceId` foi
considerado semanticamente válido sob as decisões A/A/A ratificadas
em 2026-09-22 (Q9.1 sem peer identity, Q9.2 sem backfill, Q9.3 sem
`SourceId` improvisation). O caminho canónico elegível (Telegram
discovery run-keyword) já foi fechado por W2-FU-2A
(`docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md` §W2) — não há
scope de wiring remanescente que sobreviva às A/A/A.

### 7.1 Matriz dos 7 call sites auditados

| Call site | Classification | Reason |
| --- | --- | --- |
| `TelegramBotService /test` | `WIRE_RUNREPORT_ONLY` (deferred) | Conceptualmente Telegram mas o serviço não tem `CatalogResolver` injectado; o wiring exigiria uma nova dependência que viola a intenção A/A-A "no peer→Source bridge". Diferido para wave dedicada fora de W2-FU-2. |
| `Program.cs:898/905` (`--scan-domain`) | `DO_NOT_WIRE` | HTTP probe only; não corre tester; não existe `Source` canónica nem acquisition-failure target. |
| `Program.cs:1405/1412` (legacy tester) | `DO_NOT_WIRE` | Re-test do merged `playlist.m3u`; origens heterogéneas (Telegram + M3U + Xtream); nenhuma `Source` canónica identificável. |
| `ScheduledValidationAction` | `DO_NOT_WIRE` | Cron re-test do merged playlist; `ScheduledActionCapabilities.Output` apenas; source-agnostic por design. |
| `ScheduledM3uDiscoveryAction` | `DO_NOT_WIRE` | Web-search M3U discovery (non-Telegram); o docstring da action exclui explicitamente participação no catálogo. |
| `ScheduledAutomationHost` | `DO_NOT_WIRE` | Composition root, não é um acquisition site. |
| `/api/validation/test` | `DO_NOT_WIRE` | Operator-supplied ad-hoc URL dry-run para tuning de policy; sem Source canónica. |

### 7.2 Invariantes preservadas

- **Q9.1 — sem peer identity:** nenhum `TelegramPeerEntity`,
  nenhum `peerId`/`chatTitle`→`SourceId` derivation, nenhuma bridge
  `peer/chat → SourceId`.
- **Q9.2 — sem backfill:** nenhuma escrita retroactiva de `Source`
  a partir de Runs históricos, merged playlists, ou observações
  externas.
- **Q9.3 — sem SourceId improvisation:** `SourceId` só é
  resolvido por `sourceKey` canónico
  (`GetSourceIdByKeyAsync` em `CatalogResolver.cs`), nunca
  improvisado a partir de `peerId`, `chatTitle`,
  `CandidatePlaylist.Source`, `Source.Origin`/`Key`, heurísticas,
  queries ou `XtreamAccountInfo`.

### 7.3 Referências

- `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md` §W2 (entradas
  W2-FU-1 FECHADO, W2-FU-2A FECHADO, W2-FU-2 fechada por
  W2-FU-2-CLOSE).
- `docs/PROJECT_STATUS.md` (linha da dívida 2 e linha de
  In Progress W2-FU-2 actualizadas para CLOSED).
- `CHANGELOG.md` entrada W2-FU-2-CLOSE em `[Unreleased]`.
- `docs/architecture/channel-catalog-and-ownership.md` (invariante
  de identidade Wave B e §6 protecção contra remoção, sem
  alterações).

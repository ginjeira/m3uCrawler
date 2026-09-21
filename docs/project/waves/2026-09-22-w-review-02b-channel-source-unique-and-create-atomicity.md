# Wave W-REVIEW-02B — ChannelSource UNIQUE + CreateChannel atómico
- Data: 2026-09-22
- Estado: Concluída
- Commits: (a preencher — pre-merge SHA do commit W-REVIEW-02B)
- BÍBLIA/ADR: W-REVIEW-02 (divida de UNIQUE + risco de canonical órfão); `05-CATALOGUE.md §9.1`; `07-SOURCES.md §5`; DL-127 (`31-DECISION-LOCK.md`)

## Contexto / Objectivo

W-REVIEW-02 deixou duas dívidas documentadas em `CHANGELOG.md [Unreleased] / ### ✨ Adicionado`: (i) ausência de UNIQUE constraint ao nível do schema em `channel_sources` — a identidade persistente era apenas enforced application-level pelo lookup de `RecordChannelSourceAsync`; (ii) risco pré-existente de canonical órfão em `ApplyCreateChannelAsync` — o primeiro `SaveChangesAsync` (canonical-create) fazia commit e só depois corria o segundo `SaveChangesAsync` (alias + review + ChannelSource); se este falhasse, ficava um `CanonicalChannel` sem alias, sem review e sem `ChannelSource`. W-REVIEW-02B fecha ambas as dívidas sem tocar no modelo de domínio (`CanonicalChannelEntity` permanece sem navigation property para `ChannelSources`).

## Âmbito implementado

- **`channel_sources` — filtered UNIQUE.** Migration `20260921220000_AddChannelSourceUniqueOnFingerprint` adiciona o índice `IX_channel_sources_Channel_Source_Fingerprint_Unique` sobre `(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion) WHERE "Fingerprint" IS NOT NULL`. Linhas legacy e streams não-fingerprintáveis (`Fingerprint=NULL`) permanecem coexistentes — não há regressão de D2 (múltiplas streams distintas por `(canonical, source)` continuam suportadas).
- **`ApplyReviewApprovalAsync` — discriminação de `DbUpdateException`.** O caminho de aprovação (`ApplyAddAliasAsync` + `ApplyCreateChannelAsync`) detecta agora violação do novo UNIQUE de `channel_sources`, **detacha** a entidade `Added` que falhou, **recarrega** a row existente via lookup `(CanonicalChannelId, SourceId, Fingerprint(+Version))` (e fallback por `StreamUrl`) e re-issue `SaveChangesAsync` para os restantes writes (alias + review-item). O helper pré-existente `SaveReviewApprovalAsync` continua a traduzir a violação de `IX_channel_aliases_NormalizedAlias` para `ChannelAdministrationException(AliasConflict, ...)`. Qualquer outra `DbUpdateException` propaga — nenhum erro genérico é engolido.
- **`ApplyCreateChannelAsync` — transacção explícita.** Ambos os `SaveChangesAsync` (canonical-create + alias+review+ChannelSource) são envolvidos em `Database.BeginTransactionAsync` / `CommitAsync` / `RollbackAsync`. O risco pré-existente de canonical órfão fica eliminado. A primeira escrita captura também `DbUpdateException` na UNIQUE de `canonical_channels.Key` e traduz para `ChannelAdministrationException(DuplicateKey, ...)` (HTTP 409 via `WriteChannelAdminError`).
- **Testes.** Novo `m3uCrawler.Tests/ReviewApprovalIntegrityTests.cs` com 13 facts cobrindo filtered UNIQUE (aceita e rejeita), coexistência de legacy `Fingerprint=NULL`, reload-on-UNIQUE em aprovação, tradução de duplicate canonical key, rollback real em SQLite (provocado por FK violation no segundo save dentro de transacção), happy-path de `CreateChannel` com persistência dos quatro rows (canonical + alias + review + cs), concorrência de dois `DbContexts` independentes, gate de `MaterializeChannelSourceFromReviewAsync` sem evidência, coexistência de streams distintas, idempotência de reaprovação, e presença do índice em `sqlite_master`.

## Ficheiros/componentes principais

- `m3uCrawler/Services/Catalog/Migrations/20260921220000_AddChannelSourceUniqueOnFingerprint.cs` (+ `.Designer.cs`).
- `m3uCrawler/Services/Catalog/CatalogResolver.cs` — `ApplyReviewApprovalAsync`, `ApplyAddAliasAsync`, `ApplyCreateChannelAsync`, `SaveReviewApprovalAsync`, `RecordChannelSourceAsync:2964-2981`.
- `m3uCrawler/Services/Catalog/ChannelAdministrationException.cs` — semântica `DuplicateKey` / `AliasConflict`.
- `m3uCrawler.Tests/ReviewApprovalIntegrityTests.cs` (novo, 13 facts).

## Validação (build + suite + testes relevantes)

- Build Release: 0 errors / 52 warnings (sem warnings novos introduzidos pela wave).
- Suite completa: 2624 passed / 1 skipped / 0 failed (delta +13 face à baseline W-REVIEW-02C).
- Cobertura: filtered UNIQUE (aceita e rejeita), coexistência legacy `Fingerprint=NULL`, reload-on-UNIQUE, `DuplicateKey` translation, rollback transaccional real (FK violation em SQLite), happy-path `CreateChannel`, concorrência de `DbContext`s, gate sem evidência, coexistência de streams distintas, idempotência, presença do índice em `sqlite_master`.

## Evidência

- Commit pre-merge: (a preencher — SHA do commit W-REVIEW-02B).
- Diff stats: (a preencher — ficheiros adicionados/alterados, +linhas/-linhas).

## Divergências / dívida / follow-ups

Itens que permanecem `OPEN` e fora do escopo desta wave (não introduzidos nem fechados aqui):

- **W2-FU** — observer de acquisition não ligado em produção (`docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md`).
- **W5.5 IMPLEMENTATION GAP** — `externalIdentity`/`channelSource`/`none` continuam a devolver `422 declared-change-invalid` no endpoint `POST /api/review/resolve` (DL-120).
- **M.4 authoring** — D-M4-03..D-M4-10 permanecem `OPEN / DECISION REQUIRED` (Anexo M do `BIBLE_IMPLEMENTABILITY_GAP_MANIFEST_1.0.md`); C7 também `OPEN`.
- **DL-127** (esta wave) **não** introduz `RowVersion`/`xmin` — provider de produção é SQLite. A nova UNIQUE em `channel_sources` + as UNIQUE pré-existentes em `channel_aliases.NormalizedAlias` e `canonical_channels.Key` fecham as janelas de corrida reais para o deployment single-instance actual (DL-114). Concurrency tokens só serão reconsiderados se a topologia mudar para multi-instance.
- Sem alteração ao motor legacy (`ChannelMatcher`/`MatchScorer`/`MatchingOptions`), a `ResolveAsync`, fuzzy (W5.3), `MatchMethod`/`MatchConfidence` (W5.6), Dispatcharr, scheduler.
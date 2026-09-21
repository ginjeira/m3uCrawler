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

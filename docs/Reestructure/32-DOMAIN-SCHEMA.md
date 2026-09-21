# 32 — Dicionário de dados conceptual

Este documento especifica o contrato conceptual do modelo. A implementação pode escolher nomes físicos diferentes, mas não pode alterar sem decisão normativa: significado, tipo lógico, nullability, cardinalidade, unicidade, referências, lifecycle ou invariantes. Quando um detalhe físico for relevante para comportamento ou migração, ele deve ser definido explicitamente numa secção de schema/migration e não inferido pelo implementador.

## Provider

**Finalidade:** identificar o ecossistema técnico.

Campos conceptuais:
- `Id`
- `Key`
- `Name`
- `Type`
- `Capabilities`
- `Enabled`
- timestamps

## ProviderAccount

**Finalidade:** representar uma conta concreta.

Campos:
- `Id`
- `ProviderId`
- `AccountKey`
- `DisplayName`
- `Status`
- `CredentialsReference`
- timestamps

`CredentialsReference` nunca contém credencial em texto exposto a UI/log.

`AccountKey` é a identidade funcional canónica da conta, composta por `Provider namespace + external functional identity`. Não é apenas um identificador técnico.

## Source

Campos:
- `Id`
- `ProviderAccountId`
- `Key`
- `Name`
- `Enabled`
- `Configuration`
- `Status`
- timestamps

A combinação funcional de ProviderAccount + Source Key deve ser única.

## DiscoveryCandidate

**Finalidade:** representar uma ocorrência de descoberta associada a um `Run`.

Campos:
- `Id`
- `ProviderId`
- `ProviderAccountId`
- `ExternalIdentity`
- `Evidence`
- `NormalizedIdentity`
- `Status`
- `RunId`

O `DiscoveryCandidate` representa uma ocorrência de descoberta associada a um `Run`. Informação temporal/histórica obtém-se através do `Run` e das respectivas ocorrências/evidências/`Observation`. Não manter semântica híbrida de entidade viva.

## Playlist

Campos:
- `Id`
- `SourceId`
- `AcquisitionId/RunId`
- `ContentHash`
- `Format`
- `OriginalMetadata`
- `Status` (`Success | Partial | Failed`)
- timestamps

## Stream

Campos:
- `Id`
- `PlaylistId`
- `Fingerprint`
- `FingerprintVersion`
- `OriginalName`
- `NormalizedName`
- `OriginalTvgId`
- `OriginalGroup`
- `MediaKind`
- `EndpointReference`
- timestamps

`Fingerprint` é canónico, codificado em UTF-8, calculado com SHA-256 e versionado por `FingerprintVersion`; não depende de `CanonicalChannel`, de posição na playlist nem de qualidade. A representação canónica do URL e a política de credenciais estão fixadas em `04-PLAYLIST-STREAM.md §4.1` (versão inicial `sfp1`); o URL canónico nunca é persistido.

## CanonicalChannel

Campos:
- `Id`
- `Key`
- `Name`
- `MediaKind`
- `Country/Locale`
- `Enabled`
- metadata
- timestamps

`Key` é estável e único.

## ChannelAlias

Campos:
- `Id`
- `CanonicalChannelId`
- `Value`
- `NormalizedValue`
- `Locale`
- `Origin`
- `Enabled`

## ExternalIdentity

Campos:
- `Id`
- `CanonicalChannelId`
- `ProviderId`
- `Namespace`
- `Value`
- `Origin`
- `Confidence/Strength`

## IdentityRule

Regra explícita que converte evidência em identidade.

Deve indicar:
- tipo;
- prioridade;
- âmbito;
- condição;
- resultado;
- versão;
- enabled.

A autoridade normativa das regras é a policy por tipo; o snapshot resolvido de um `Run` é a autoridade histórica daquilo que foi decidido nesse `Run`.

## RecognitionPolicy

`RecognitionPolicy` (incluindo fuzzy threshold e afins) existe como tipo de policy cujos valores são parâmetros; fuzzy **não** está activo por defeito (`Fuzzy.Enabled = false`). Scopes `system/default | global | group | channel`, precedência `channel > group > global > default`; a policy resolvida é materializada num snapshot imutável por Run. Schema mínimo: `Enabled`, `Fuzzy.Enabled`, `Fuzzy.Threshold`, `Fuzzy.AmbiguityMargin`, `Fuzzy.Weights`; a semântica destes campos está fechada em `48-RECOGNITION-FUZZY-CONTRACT.md` (W5.3) e os valores são `PARAMETER` (threshold `0..100`; margem `>= 0`, nulo ≡ `0`; pesos JSON, nulo ≡ `1.0`).

## ReviewItem

Campos:
- `Id`
- `RunId`
- `StreamId`
- `Reason`
- `Candidates`
- `Evidence`
- `Status`
- `Decision`
- `Actor`
- timestamps.

**Scope de reconciliação (DL-119).** O lifecycle efectivo usa estados `Open | InReview | Resolved | Ignored` (`33`/DL-105). W5.4 implementa o lifecycle e altera o `ReviewItem` apenas no mínimo necessário para o suportar. Os campos conceptuais `RunId`, `StreamId`, `Actor`, `Evidence`, `Candidates` e `Decision` **não** têm contrato implementável fechado (não existe entidade `Stream` persistida nem origem definida de `Actor`); não devem ser inventados. Ficam `OPEN` e a sua reconciliação completa pertence a W5.6 (`BIBLE_IMPLEMENTABILITY_GAP_MANIFEST`, L.4/C7). `Status` é a materialização persistida dos estados do lifecycle.

**Fronteira C7 (DL-121, W5.6).** W5.6 **não** implementa estes campos enquanto não existir contrato implementável; pode documentar a fronteira, mas não inventa modelo C7 para cumprir o plano de waves. M.4 permanece `OUT` (`W5.6 ≠ M.4`).

`Resolve` é operação administrativa auditada e pode alterar explicitamente `CanonicalChannel`, `ChannelAlias`, `ExternalIdentity` e/ou `ChannelSource`, conforme a operação. Nunca cria identidade implicitamente. `Ignore` fecha o `ReviewItem`, exige motivo, não elimina `CanonicalChannel`/histórico, não apaga indiscriminadamente `Stream`/`ChannelSource`, e impede a ocorrência de ser reconhecimento válido segundo a decisão registada.

## CountryProfile

**Finalidade:** fornecer dados/classificação de país versionados.

É a autoridade dos dados de país.

Campos conceptuais:
- `Id`
- `Key`/Country code
- `schemaVersion`
- `Version`
- `Data`
- timestamps

## MediaClassification

**Finalidade:** registar o resultado explícito da classificação de media.

É a autoridade do resultado da classificação.

Campos:
- `Id`
- subject
- `MediaKind` (`Linear TV | Radio | VOD | Unknown`)
- policy version
- reason
- evidence
- evaluatedAt

## ChannelSource

Campos:
- `Id`
- `CanonicalChannelId`
- `SourceId`
- `Fingerprint`
- `FingerprintVersion`
- `ExternalIdentity`
- `Status`
- `LastSeen`
- `LastSuccessfulValidation`
- provenance

`Fingerprint` é o hash canónico versionado do URL do stream (`04-PLAYLIST-STREAM.md §4.1`); só o hash e a versão são persistidos. `Fingerprint`/`FingerprintVersion` são nulos em rows legacy ou em streams não fingerprintáveis.

Constraint funcional (reconciliada com `07-SOURCES.md §5`):
múltiplas streams por `(CanonicalChannelId, SourceId)` são suportadas. A
identidade lógica persistente é
`CanonicalChannelId + SourceId + Fingerprint + FingerprintVersion`; não é
imposta unicidade em `(CanonicalChannelId, SourceId)`.

`MatchMethod` representa o método efectivo de reconhecimento (`ExternalIdentityExact | TvgIdExact | CanonicalExact | NormalizedName | KnownAlias | ExplicitHeuristic | Fuzzy | ManualReview`). `MatchConfidence` tem domínio `0..1`; não é o score de fuzzy e valores de métodos diferentes não são directamente comparáveis sem semântica explícita. A semântica de ambos é versionada. São registo/evidência, não autoridade, e não alteram a decisão de reconhecimento.

**Clarificação W5.6 (DL-121).** `MatchConfidence` é method-specific: cada um dos 8 métodos tem regra normativa própria e não há escala global comparável (nunca ordenar métodos pelo número). Nos resultados `Unknown`/`Ambiguous`, `MatchConfidence = null`. A decisão semântica nasce em Recognition (`CatalogResolution`, que transporta `MatchMethod` e `MatchConfidence`); o pipeline apenas persiste em `ChannelSource`, sem recalcular. Especificação normativa: `49`; valores e versão ratificados em DL-122.

**Versão da semântica (DL-122).** `MatchSemanticsVersion` (`"msm1"`) é **persistida em `ChannelSource`** a par de `MatchMethod`/`MatchConfidence`. Identifica as regras/algoritmo que produziram o par (proveniência derivada do algoritmo, não propriedade arbitrária do operador); rows novas recebem `"msm1"`. É **independente** de `FingerprintVersion` e **não** é substituível por `RecognitionPolicy`. Implementado por `ChannelSourceEntity.MatchSemanticsVersion` (`ChannelCatalogDbContext`) e migration `AddMatchSemanticsVersionAndNullableMatchConfidence`; `MatchConfidence` é nullable (`null` em `Unknown`/`Ambiguous`, nunca `0`/score de fuzzy). A fronteira C7 do `ReviewItem` permanece `OPEN`/limitada (`32:163`) e nenhum campo C7 é adicionado.

**Nota histórica (conflito preservado).** A formulação anterior —
`CanonicalChannelId + SourceId` único — constava de `16:27`/`32:207` e
contradizia `07:48` (múltiplas streams candidatas por ChannelSource). A
reconciliação fecha o conflito a favor de múltiplas streams; o fingerprint
distingue as entradas.

## Observation

Campos:
- `Id`
- `RunId`
- `ChannelSource/StreamId`
- `ObservedAtUtc`
- `Result`
- `Latency`
- `Quality`
- `Epg`
- `ErrorCode`
- evidence.

É append-oriented; não substituir histórico sem política de retenção.

`Observation` é a autoridade do facto técnico observado; Validation interpreta/evalua observações segundo policy e não redefine o facto.

## Eligibility

É resultado derivado, não observação.

Campos:
- subject;
- policy version;
- decision;
- reason;
- evidence reference;
- evaluatedAt.

Eligibility é derivada por Run e pode registar o último estado `Eligible` publicável; não exige nova entidade salvo se necessário.

## SourcePriorityPolicy

**Finalidade:** definir a regra de prioridade entre origens.

É a autoridade da regra de prioridade.

Campos:
- policy `Key`
- `Version`
- `Scope`
- `Enabled`
- `Parameters`

## SourceSelectionPolicy

**Finalidade:** definir a regra de selecção de stream.

É a autoridade da regra de selecção.

Campos:
- policy `Key`
- `Version`
- `Scope`
- `Enabled`
- `Parameters`

O resultado concreto da selecção num Run é decisão derivada registada no Run/Snapshot, não substitui a policy como autoridade normativa.

Cada tipo de Policy declara o seu schema de campos. Merge: scalar/object → substituição; collection → semântica definida pelo schema; `null` explícito → valor explícito; ausência → herança; `Enabled=false` → desactivada. Precedência DL-103 mantém-se.

## OrderingList

Campos:
- `Id`
- `Key`
- `Name`
- `MediaKind`
- `Enabled`
- version/status.

## OrderingItem

Campos:
- `OrderingListId`
- `Position`
- `CanonicalChannelId`
- optional group override.

Constraint: `(OrderingListId, Position)` único; um `CanonicalChannel` não pode ocupar duas posições na mesma lista.

## CanonicalGroup

Campos:
- `Id`
- `Key`
- `Name`
- `MediaKind`
- ordering/output metadata.

## GroupMapping

Converte evidência de origem em CanonicalGroup segundo regra explícita.

## GeneratedPlaylist

Campos:
- `Id`
- `RunId`
- `OrderingListId`
- `ContentHash`
- `Path/reference`
- schema version
- publication state.

`publication state` ∈ `Generated | Published | Superseded | Failed`. `Path/reference` é estável por `OrderingList`; a substituição é atómica; o histórico reside no Run/Snapshot; não existe path primário por Run.

## OwnershipRecord

Campos:
- `ExternalSystem`
- `ExternalResourceType`
- `ExternalResourceId`
- `OwnershipState`
- `CreatedByRunId`
- timestamps.

## Run

Campos:
- `Id`
- `Trigger`
- `Status`
- `StartedAtUtc`
- `FinishedAtUtc`
- `SnapshotReference`
- counters
- failure summary.

## AuditRecord

Campos:
- actor;
- operation;
- object;
- before;
- after;
- result;
- timestamp;
- correlation id.

Secrets são sempre excluídos.

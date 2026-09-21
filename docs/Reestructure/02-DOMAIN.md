# 02 — Modelo de domínio

## 1. Entidades principais

O domínio é constituído, no mínimo, por:

- Provider
- ProviderAccount
- Source
- DiscoveryCandidate
- Playlist
- Stream
- CanonicalChannel
- ChannelAlias
- ExternalIdentity
- IdentityRule
- ReviewItem
- CountryProfile
- MediaClassification
- ChannelSource
- Observation
- Eligibility
- SourcePriorityPolicy
- SourceSelectionPolicy
- OrderingList
- OrderingItem
- CanonicalGroup
- GroupMapping
- GeneratedPlaylist
- DispatcharrResource
- OwnershipRecord
- Run
- RunSnapshot
- AuditRecord

### Autoridade

Cada entidade tem uma única autoridade, com a seguinte correspondência (detalhe normativo em `28-TRUTH-AND-TRACEABILITY.md` §1):

- `CountryProfile` → autoridade dos dados/classificação de país versionados; overlays são camadas subordinadas, não autoridades concorrentes.
- `MediaClassification` → autoridade do resultado da classificação; a policy que produz a classificação é regra separada; media não é definido por posição em `OrderingList`.
- `SourcePriorityPolicy` → autoridade da regra; valores/resoluções derivados não são segunda autoridade.
- `SourceSelectionPolicy` → autoridade da regra; o resultado concreto da selecção num `Run` é decisão derivada registada no `Run`/`Snapshot`.

`DiscoveryCandidate` é uma ocorrência de descoberta associada a um `Run`.

## 2. Separação fundamental

### Identidade
`CanonicalChannel`, `ChannelAlias`, `ExternalIdentity`, `IdentityRule`, `ReviewItem`.

### Origem
`Provider`, `ProviderAccount`, `Source`, `DiscoveryCandidate`, `Playlist`, `Stream`.

### Relação canal/origem
`ChannelSource`.

### Estado observado
`Observation`, `Eligibility`.

### Decisão
`SourcePriorityPolicy`, `SourceSelectionPolicy`, `OrderingList`.

### Apresentação
`CanonicalGroup`, `GroupMapping`, `GeneratedPlaylist`.

### Integração
`DispatcharrResource`, `OwnershipRecord`.

### Execução
`Run`, `RunSnapshot`, `AuditRecord`.

Nenhuma destas responsabilidades deve ser confundida.

## 3. Identificadores

Todo o objecto persistente DEVE possuir identificador técnico estável.

Identidade funcional:
- CanonicalChannel: `CanonicalChannel.Key`;
- ProviderAccount: identidade da conta dentro do provider;
- Source: identidade configurada da origem;
- Stream: identidade técnica própria + fingerprint de conteúdo/origem;
- ChannelSource: relação entre canal e source, não uma identidade do canal.

IDs técnicos nunca devem ser usados como substitutos de identidade funcional quando essa identidade é necessária.

Identidade funcional (`AccountKey`) é distinta de identificadores técnicos.

Identidade técnica nunca substitui identidade funcional.

## 4. Regras universais

1. Identidade canónica não depende da posição na source.
2. Identidade canónica não depende da URL da stream.
3. Qualidade não determina identidade.
4. Um desconhecido não cria implicitamente um CanonicalChannel.
5. Uma ambiguidade não pode ser resolvida silenciosamente.
6. Review é o mecanismo de resolução de incerteza.
7. Validation observa; não redefine identidade.
8. Ordering define posição; não define identidade.
9. Selection escolhe uma stream; não define se o canal existe.
10. Dispatcharr representa um destino/integrador, não a autoridade do catálogo.
11. Falhas parciais não podem produzir estado falso de sucesso.
12. Execuções devem ser determinísticas relativamente ao mesmo snapshot de entradas/políticas.

## 5. Estado e histórico

Dados de execução devem permitir distinguir:
- estado actual;
- observação de uma execução;
- decisão tomada;
- evidência que suportou a decisão.

Não se deve sobrescrever informação necessária para explicar por que razão uma execução chegou ao resultado actual.

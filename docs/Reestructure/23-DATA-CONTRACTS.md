# 23 — Contratos de dados

## 1. Catálogo

Import/export do catálogo deve ter:
- schema version;
- catalog id;
- locale/country;
- channels;
- aliases;
- external identities;
- groups quando aplicável.

## 2. Import

Import deve definir:
- create;
- update;
- conflict;
- delete;
- dry-run;
- preservação de IDs;
- compatibilidade.

Nunca apagar silenciosamente informação local por diferença de ficheiro.

## 3. M3U

Input M3U é não confiável e pode ser inconsistente.

Output M3U é produto do pipeline e deve ser validado antes da publicação.

`Playlist.Status` tem enum `Success | Partial | Failed`; `Partial` nunca equivale a sucesso pleno.

`GeneratedPlaylist.publication state` = `Generated | Published | Superseded | Failed`; `Failed` = falha de publicação/composição.
`Path/reference` é estável por `OrderingList`; a publicação usa replace atómico; o histórico pertence ao Run/Snapshot. Não existe path primário por Run.

## 4. Artifacts

Cada artifact tem schema version e origem/run identificável.

## 5. Contrato mínimo obrigatório

Cada formato persistido ou exportado deve declarar:
- schema version;
- campos obrigatórios/opcionais;
- tipos e unidades;
- nullability;
- valores/enums permitidos;
- identificadores e chaves de unicidade;
- encoding/formato;
- regras de compatibilidade;
- migração;
- tratamento de campos desconhecidos;
- tratamento de secrets.

Metadados ausentes são representados como `null` com nullability declarada; a string vazia não é substituto semântico de ausência.

O `32-DOMAIN-SCHEMA.md` define semântica de domínio; este documento define contratos de dados serializados. Quando faltar um detalhe necessário para interoperabilidade, existe `BIBLE_GAP`.

Para cada entidade persistida (JSON schema de domínio) exige-se ainda:

- **Campos essenciais:** `id` (único), `schemaVersion`, `version` (versão de conteúdo) e `timestamp` (última modificação).
- **Invariantes:** definir campos de identidade (ex.: `CanonicalChannel.key` não muda após criação) e proibir renomeações sem novo ID.
- **Versionamento:** documentar migração/actualização entre versões de schema.
- **Conflito/Merge:** especificar resolução de importação conflituosa (ex.: priorizar edições locais; mesclar listas sem duplicar).

Critério de aceitação: todos os esquemas JSON usados (canais, streams, países, …) cumprem estas regras; testes de validação JSON (unitários/integração) passam.

## 6. Compatibilidade

Schemas devem ter política de compatibilidade e migration quando necessário.

## 7. Fingerprint e identidade

O fingerprint é uma representação canónica em UTF-8, com hash SHA-256, conjunto de campos explícito e versionado; uma alteração incompatível cria nova versão (DL-108).

A versão inicial é `sfp1`: `Fingerprint = hex minúsculo de SHA-256(UTF-8("sfp1\n" + canonicalUrl))`. A representação canónica e a política de credenciais (userinfo nunca incluído; `username`/`password`/`token`/`authorization` removidos da query; credenciais de path Xtream mascaradas) estão fixadas em `04-PLAYLIST-STREAM.md §4.1`. Só o fingerprint e a versão são persistidos; o URL canónico nunca é persistido. Um fingerprint ausente é representado como `null` (rows legacy ou URLs não fingerprintáveis), nunca como string vazia.

`AccountKey` é o termo normativo da identidade funcional de uma conta.

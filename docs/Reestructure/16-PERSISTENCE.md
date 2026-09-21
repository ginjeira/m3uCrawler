# 16 — Persistência e SQLite

## 1. SQLite

A instalação normal é single-instance/single-writer lógico, salvo arquitectura futura explicitamente definida.

SQLite é a autoridade persistente do estado da aplicação.

## 2. Princípios

- foreign keys activas;
- migrations versionadas;
- índices definidos para acessos reais;
- constraints para invariantes;
- transacções explícitas;
- timestamps em UTC;
- enum/state values versionados.

## 3. Unicidade

Devem existir constraints para impedir duplicados funcionais, nomeadamente:
- CanonicalChannel.Key;
- aliases onde a combinação exigir unicidade;
- ProviderAccount por identidade funcional;
- ProviderAccount por `AccountKey`;
- Source por identidade funcional;
- ChannelSource por `(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion)` — múltiplas streams por `(CanonicalChannelId, SourceId)` são suportadas (`07-SOURCES.md §5`; o índice físico em `(CanonicalChannelId, SourceId)` não é único);
- DiscoveryCandidate por `(RunId, ProviderAccountId ou identidade funcional equivalente)` para impedir processamento equivalente duplicado no mesmo Run;
- OrderingItem por `(OrderingListId, Position)` único; um `CanonicalChannel` não pode ocupar duas posições na mesma lista;
- Ownership por recurso externo.

A forma exacta das constraints deve ser especificada no schema normativo antes da implementação.

**Nota histórica (conflito preservado).** A versão anterior indicava `ChannelSource por (CanonicalChannelId, SourceId)`, o que contradizia `07-SOURCES.md §5` (múltiplas streams candidatas por ChannelSource). A reconciliação fecha o conflito a favor de múltiplas streams: a identidade lógica persistente inclui `Fingerprint + FingerprintVersion` (`32-DOMAIN-SCHEMA.md`, ChannelSource).

## 4. Long-running work

Operações de rede longas não devem manter uma transacção DB aberta durante toda a operação.

Persistir:
- início;
- evidência;
- resultado;
- transições.

## 5. Migrações

Cada migration deve:
- ser determinística;
- ser testada;
- preservar dados;
- não destruir estado funcional existente;
- declarar incompatibilidades;
- possuir estratégia de rollback/restore quando rollback SQL não for seguro.

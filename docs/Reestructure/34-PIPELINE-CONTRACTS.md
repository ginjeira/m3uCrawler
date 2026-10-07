# 34 — Contratos do pipeline

## P0 — Discovery

**Entrada:** mecanismo configurado + snapshot.  
**Saída:** `DiscoveryCandidate[]`.  
**Não pode:** criar CanonicalChannel.

Produz ocorrências de `DiscoveryCandidate` associadas ao Run; não cria entidade persistente viva.

## P1 — Acquisition

**Entrada:** Candidate/Source.  
**Saída:** aquisição bruta + evidência.  
**Não pode:** decidir identidade.

## P2 — Parsing

**Entrada:** documento M3U/API.  
**Saída:** Playlist + Stream raw.  
**Não pode:** escolher canal canónico.

Input mínimo M3U exige `#EXTM3U` + `#EXTINF` + URL; entrada malformada é registada e não invalida automaticamente a playlist; `Playlist.Status` = Success/Partial/Failed.

## P3 — Normalization

**Entrada:** Stream raw.  
**Saída:** representação normalizada + original preservado.

Normalização por campo segundo tabela normativa; preservar original; separar Normalization/Recognition/Fingerprint.

## P4 — Media Classification

**Entrada:** stream/evidência.  
**Saída:** TV/Radio/VOD/Unknown.

## P5 — Country Gate

**Entrada:** stream/classification/country policy.  
**Saída:** pass/reject/unknown.

Não cria identidade.

## P6 — Recognition

**Entrada:** stream normalizada + catálogo/rules.  
**Saída:** Canonical / Unknown / Ambiguous / Excluded.

Não cria implicitamente catálogo.

Ordem de reconhecimento (determinística): identidade externa exacta → tvg-id/canonical/provider identity → nome normalizado → alias conhecido → heurística explícita → fuzzy (opt-in via `RecognitionPolicy.Fuzzy.Enabled`) → Review. `IdentityRule` é regra explícita (`Review`/`Excluded`), não excepção silenciosa à ordem.

`Excluded` = resultado de regra determinística de exclusão; `Unknown ≠ Excluded` e `Ambiguous ≠ Excluded`; `Rejected` não é resultado de P6.

`Ambiguous` qualifica-se pelo estágio: `Stage=Recognition` (falta de desempate de reconhecimento) ≠ `Stage=Selection` (falta de desempate de selecção).

## P7 — Review

**Entrada:** Unknown/Ambiguous.  
**Saída:** decisão administrativa + alteração explícita, se houver.

`Resolve` é operação administrativa auditada e pode alterar explicitamente `CanonicalChannel`, `ChannelAlias`, `ExternalIdentity` e/ou `ChannelSource`, conforme a operação. Nunca cria identidade implicitamente. `Ignore` fecha o `ReviewItem`, exige motivo, não elimina `CanonicalChannel`/histórico, não apaga indiscriminadamente `Stream`/`ChannelSource`, e impede a ocorrência de ser reconhecimento válido segundo a decisão registada.

## P8 — ChannelSource

Cria/actualiza a relação entre identidade reconhecida e source.

## P9 — Validation

Produz Observation.

## P10 — Eligibility

Produz decisão derivada.

## P11 — Priority

Produz preferência entre sources.

## P12 — Selection

Produz stream escolhida ou estado sem escolha.

## P13 — Ordering

Relaciona canais com posições.

## P14 — Composition

Produz GeneratedPlaylist.

## P15 — Dispatcharr

Aplica o resultado remoto respeitando ownership.

## Regra de fronteiras

Cada etapa deve receber dados suficientes para executar a sua função sem depender de efeitos laterais escondidos de uma etapa posterior.

Um shortcut é válido apenas se produzir exactamente o mesmo contrato que as etapas omitidas produziriam.

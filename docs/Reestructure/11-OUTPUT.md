# 11 — Composition e playlists geradas

## 1. Composition

Composition transforma decisões do domínio em output.

Inputs:
- CanonicalChannel;
- OrderingList;
- ChannelSource/selection;
- grupos;
- políticas de output;
- streams seleccionadas.

## 2. Determinismo

Para o mesmo RunSnapshot, output deve ser byte-a-byte ou semanticamente equivalente segundo o contrato de serialização.

Ordenações implícitas de DB são proibidas.

## 3. M3U

A playlist deve:
- respeitar sintaxe M3U;
- emitir metadados normalizados;
- usar número da OrderingList quando aplicável;
- não misturar VOD e linear;
- evitar URLs com credenciais quando a arquitectura permitir referências seguras.

## 4. Publicação

Escrever um output novo deve ser atómico: gerar temporariamente, validar e substituir.

Um output parcial nunca deve ser apresentado como output válido.

**Listas publicadas.** Todas as `OrderingList` `Enabled` são consideradas no mesmo Run. Cada lista gera o seu próprio artifact.
**Publication state.** A enumeração é `Generated`, `Published`, `Superseded`, `Failed`. `Failed` representa falha de publicação/composição.
**Path.** O path é estável por `OrderingList`. A publicação usa replace atómico. O histórico da execução pertence ao `Run`/`Snapshot` e NÃO ao nome físico do ficheiro. Não usar path diferente por Run como mecanismo primário.

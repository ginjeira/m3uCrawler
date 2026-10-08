# 10 — Ordering, groups e mappings

## 1. Ordering

Ordering define a posição apresentada ao utilizador.

Não define identidade.

Uma `OrderingList` é configurável e pode representar uma grelha base inspirada em operadores portugueses sem assumir que a grelha é contratual ou permanente.

## 2. OrderingItem

Relaciona:
`OrderingList + position → CanonicalChannel`

O mesmo canal pode existir em várias listas/posições.

`(OrderingListId, Position)` é único. Um `CanonicalChannel` NÃO pode ocupar duas posições na mesma `OrderingList`. Um canal elegível sem presença em nenhuma `OrderingList` NÃO é publicado.

## 3. Groups

`CanonicalGroup` é grupo canónico do output.

O grupo de publicação é uma propriedade do canal canónico (`GroupId` → `CanonicalGroup`); o `group-title` da Source é evidência/sugestão apenas.

`group-title` da Source é evidência, não verdade canónica.

## 4. Linear TV

TV linear usa ordering de TV.

## 5. Radio

Radio deve ter ordering/listas próprias quando publicada.

## 6. VOD

VOD não utiliza posições lineares de TV.

## 7. Gaps

Uma posição sem canal não deve ser preenchida com outra identidade apenas para eliminar um buraco.

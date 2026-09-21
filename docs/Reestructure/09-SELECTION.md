# 09 — Source Priority e Source Selection

## 1. Distinção

**Source Priority** responde:
> "Que preferência deve existir entre origens?"

**Source Selection** responde:
> "Dadas as fontes elegíveis nesta execução, qual stream concreta deve ser usada?"

Não são a mesma coisa.

## 2. Inputs

Selection deve considerar apenas dados do snapshot da execução:
- ChannelSource elegíveis;
- policy;
- priority;
- disponibilidade;
- atributos técnicos;
- override por canal quando aplicável.

## 3. Determinismo

A selecção normativa usa ordenação lexicográfica. Não existe score agregado.

Ordem base fechada:
1. override explícito do canal;
2. SourcePriority configurada;
3. Eligibility = Eligible;
4. preferência de media/qualidade definida pela policy;
5. frescura da última validação bem sucedida, quando activada pela policy;
6. fingerprint estável;
7. identificador estável.

Um critério só é usado quando estiver activo/configurado segundo o schema da policy. Se todos os critérios aplicáveis forem iguais, o identificador estável produz a ordem final.

O critério 6 usa o `Fingerprint` persistido em `ChannelSource` (`04-PLAYLIST-STREAM.md §4.1`, `32-DOMAIN-SCHEMA.md`) quando presente; para rows legacy sem fingerprint, usa a URL normalizada como fallback. A ordem fechada dos critérios não é alterada por esta substituição de sinal — apenas o valor comparado no critério 6 passa a ser a representação canónica versionada.

A ordem acima é uma decisão normativa já fechada em `31-DECISION-LOCK.md` (DL-101). Um ADR pode documentar o racional, mas não pode escolher uma ordem diferente sem alterar primeiro a BÍBLIA.

## 4. Sem surpresa

Selection não pode criar identidade, modificar catálogo ou alterar ordering.

## 5. Resultado

O resultado deve distinguir:
- selected;
- evaluated;
- no eligible source;
- ambiguous;
- not evaluated;
- error.

`not evaluated` não pode ser interpretado como `evaluated with zero`.

`SourceSelectionPolicy` é a autoridade da regra. O resultado concreto da selecção num Run é decisão derivada registada no Run/Snapshot e não substitui a policy.

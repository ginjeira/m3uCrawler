# 28 — Verdade, rastreabilidade e controlo de alterações

## 1. Definition of Truth

Para cada conceito deve existir uma única autoridade.

Exemplos:
- identidade → CanonicalChannel;
- posição → OrderingList;
- origem → Source;
- estado técnico → Observation;
- decisão de elegibilidade → Eligibility;
- escolha de stream → Selection;
- output → GeneratedPlaylist;
- recursos remotos → Dispatcharr + Ownership;
- país → `CountryProfile` (autoridade dos dados/classificação de país versionados; overlays são camadas subordinadas, não autoridades concorrentes);
- media → `MediaClassification` (autoridade do resultado da classificação; a policy que produz a classificação é regra separada; media não é definido por posição em `OrderingList`);
- prioridade → `SourcePriorityPolicy` (autoridade da regra; valores/resoluções derivados não são segunda autoridade);
- selecção → `SourceSelectionPolicy` (autoridade da regra; o resultado concreto da selecção num `Run` é decisão derivada registada no `Run`/`Snapshot`);
- validação → `Observation` (autoridade do facto técnico observado; `Validation` é o processo que interpreta/avalia observações segundo policy);
- Dispatcharr actual → sem autoridade local; o estado remoto é evidência do sistema externo, podendo ser persistido para histórico/reconciliação, mas uma cópia local nunca é autoridade do estado real (mantém DL-013);
- Dispatcharr desired → o plano calculado de desired state da execução/snapshot; não é `GeneratedPlaylist` por si só nem `DispatcharrPolicy` autónoma;
- policy → policy por tipo (autoridade das regras); o snapshot resolvido de um `Run` é a autoridade histórica do que foi decidido nesse `Run`, sem substituir a policy.

## 2. Nunca duplicar autoridade

Uma cópia pode existir para cache, performance ou export, mas deve declarar a autoridade original e estratégia de invalidação.

## 3. Traceability

Todo requisito relevante deve conseguir seguir:

`Requirement → Bible section → Domain/API → implementation → test → execution evidence`

A matriz operacional que materializa esta cadeia está em `46-REQUIREMENT-TRACEABILITY.md`.

## 4. Change control

Alteração arquitectural:
1. identificar conflito;
2. demonstrar necessidade;
3. propor alteração;
4. rever invariantes;
5. actualizar BÍBLIA;
6. actualizar ADR;
7. actualizar requisitos;
8. só depois implementar.

## 5. Revisão independente

Antes de declarar a BÍBLIA estável, uma revisão deve tentar encontrar:
- conceitos duplicados;
- autoridades múltiplas;
- estados impossíveis;
- transições sem dono;
- efeitos secundários escondidos;
- caminhos alternativos;
- decisões não determinísticas;
- falhas não especificadas;
- dados sem lifecycle;
- requisitos sem teste.

## 6. Critério de verdade

A BÍBLIA é considerada consistente quando um leitor independente consegue reconstruir:
- modelo;
- fluxo;
- contratos;
- decisões;
- erros;
- segurança;
- operação;
sem recorrer ao código histórico para preencher decisões fundamentais.

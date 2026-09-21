# 13 — Runs, pipeline e scheduler

## 1. Run

Cada execução possui:
- RunId;
- início/fim;
- trigger;
- estado;
- snapshot de configuração/políticas/catálogo relevantes;
- contadores;
- erros;
- artifacts;
- resultado.

`RunId` é opaco e único; não é obrigatório ser temporalmente ordenável.

A identidade autoritativa do `RunId` é o identificador operacional criado pelo `RunCoordinator` (DL-124). Identificadores de diagnóstico/observabilidade (por exemplo `PipelineTrace.RunId`) **não** são identidade de Run e **não** substituem o `RunId` operacional. Caminhos sem `RunCoordinator` não têm Run operacional e **não** fabricam um `RunId` apenas para satisfazer o mecanismo de snapshots (DL-124 / M.4 D-M4-01b).

## 2. RunSnapshot

Uma execução longa não deve mudar de semântica porque alguém alterou a configuração a meio.

No início deve capturar as versões/valores necessários à execução.

Alterações posteriores aplicam-se a execuções seguintes, salvo contratos explícitos de runtime.

## 3. Pipeline

Sequência normativa:

`Discovery → Acquisition → Parsing → Stream → Normalization → Media Classification → Country Gate → Recognition → Review/known → ChannelSource → Validation → Eligibility → Source Priority → Source Selection → Ordering → Composition → Publication → Dispatcharr`

Nem todas as etapas têm de executar em todos os modos, mas os atalhos não podem mudar o significado do resultado.

## 4. Idempotência

Reexecutar com as mesmas entradas e políticas não deve criar duplicados nem churn.

## 5. Scheduler

Scheduler e execução manual devem usar o mesmo RunCoordinator.

Não pode existir uma segunda implementação funcional do pipeline.

O scheduler pode usar timezone do operador para expressar agendamento, mas os instantes efectivos persistidos são UTC.

## 6. Overlap

Por defeito, duas execuções concorrentes sobre o mesmo runtime devem ser serializadas ou coordenadas por lease/lock.

Trigger manual enquanto existe Run activa é REJEITADO; não há fila implícita.

Concorrência: serialização estrita. Existe no máximo uma Run activa por instância lógica.

Lock perdido → Run `Failed`. Reinício com Run `Created`/`Running` → `Failed`. A execução interrompida não continua silenciosamente após restart.

A política de concorrência deve impedir:
- dois writers simultâneos incompatíveis;
- outputs corrompidos;
- Dispatcharr reconciliation concorrente.

## 7. Cancelamento

Cancelamento deve propagar-se por todas as operações suportadas.

Um Run cancelado não é um Run com sucesso parcial silencioso.

Cancelamento após efeitos externos: manter os efeitos já produzidos e permitir reconciliação posterior; o estado da Run reflecte cancelamento, mas nunca finge que efeitos externos não ocorreram ou foram universalmente revertidos.

## 8. Níveis de retry

**Retry técnico de operação (dentro de uma Run).** Uma operação sujeita a retry/backoff (por exemplo, rede) permanece na MESMA Run e NÃO cria novo `RunId`.

**Reexecução/retry lógico da Run.** Cria uma NOVA Run, com NOVO `RunId`, e mantém relação causal explícita com a Run anterior.

Só a reexecução/retry lógico da Run cria um novo `RunId`; o retry técnico de operação nunca cria uma Run.

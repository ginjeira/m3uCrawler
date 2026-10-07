# 33 — Máquinas de estados

As transições abaixo são semânticas. A implementação pode usar enums diferentes, mas não pode permitir transições semanticamente equivalentes a estados inválidos.

## Application lifecycle

`NOT_CONFIGURED → CONFIGURING → READY`

Pode voltar a `CONFIGURING` quando uma dependência obrigatória for removida/desconfigurada.

READY significa que os requisitos mínimos para o modo operacional seleccionado estão satisfeitos.

## Source

`Disabled → Configured → Enabled → Error`

Error não implica apagamento. Pode recuperar para Enabled.

Após esgotar os retries técnicos, a falha persistente fica registada em `Source.Status` e participa na agregação do estado da Run. Retry técnico permanece na mesma Run.

## DiscoveryCandidate

`Discovered → Normalized → Deduplicated → Accepted | Rejected | Expired`

O ciclo é por ocorrência de descoberta associada a um Run; não é um ciclo de entidade viva persistente.

## ReviewItem

`Open → InReview → Resolved`

`Open → Ignored` só quando existir uma razão administrativa explícita. `InReview` significa início de tratamento administrativo; **não é obrigatório** para um `Ignore` directo.

`Open → InReview → Ignored` também é permitido.

Reabertura: `Resolved → Open` e `Ignored → Open`, exclusivamente através de operação administrativa auditada e perante nova evidência materialmente incompatível (DL-105). É uma operação **manual**, com justificação fornecida pelo operador; a deteção automática de "evidência materialmente incompatível" (comparação automática de evidências, reabertura automática) **não** faz parte do lifecycle implementado em W5.4 e o seu critério permanece `OPEN` (DL-119).

`Resolve` é operação administrativa auditada e pode alterar explicitamente `CanonicalChannel`, `ChannelAlias`, `ExternalIdentity` e/ou `ChannelSource`, conforme a operação. Nunca cria identidade implicitamente. `Ignore` fecha o `ReviewItem`, exige motivo, não elimina `CanonicalChannel`/histórico, não apaga indiscriminadamente `Stream`/`ChannelSource`, e impede a ocorrência de ser reconhecimento válido segundo a decisão registada.

**Implementação W5.4.** A máquina vive em `Services/Catalog/ReviewLifecycle.cs` (apenas as seis transições) e é aplicada por `CatalogResolver` (`BeginReviewAsync`, `ResolveReviewAsync`, `IgnoreReviewAsync`, `ReopenReviewAsync`, auditadas; `ApproveReviewAsync`/`ExcludeReviewAsync`/`ApplyReviewApprovalAsync` mantêm-se como compatibilidade, terminando em `Resolved`/`Ignored` — o caminho legacy de aprovação compõe `Open→InReview→Resolved`, nunca um salto directo). Estados persistidos com os mesmos valores `int` (`Open=0`, `Resolved=1`, `Ignored=2`, `InReview=3`); sem migration. Evidência: `WaveW54ReviewLifecycleTests.cs`.

## ChannelSource

`Observed → Candidate | Active | Inactive`

Inactive não significa deleted.

## Eligibility

`Unknown | Eligible | Ineligible`

A transição deve ser suportada por policy + evidence.

O mapeamento categoria de Validation → `Eligible|Ineligible|Unknown` é normativo por categoria (não configurável pelo operador). Nova evidência válida pode recuperar automaticamente `Ineligible → Eligible`. 'Último estado publicável' = último estado `Eligible` efectivamente publicável na janela de histerese. Ausência de observação não é `Ineligible`.

## Run

`Created → Running → Succeeded`

ou

`Running → PartiallySucceeded`

ou

`Running → Failed`

ou

`Running → Cancelled`

Um Run terminado não deve regressar a Running.

`RunId` é opaco e único. Retry técnico de uma operação permanece na mesma Run. Reexecução/retry lógico cria uma nova Run com novo `RunId` e relação causal explícita com a Run anterior. Um trigger manual concorrente é rejeitado. Existe no máximo uma Run activa por instância lógica. Lock perdido → `Failed`. Reinício com Run `Created`/`Running` → `Failed`. Nenhum Run terminado regressa a `Running`.

## Dispatcharr reconciliation

`Planned → Applying → Applied`

ou

`Applying → Partial`

ou

`Applying → Failed`

Depois de Partial/Failed pode existir nova reconciliação.

## Ownership

`Unknown → CrawlerManaged` apenas quando a aplicação comprovar criação/ownership.

`Unknown → External` quando existir evidência externa suficiente.

Nunca:
`Unknown → CrawlerManaged` apenas por nome/semelhança.

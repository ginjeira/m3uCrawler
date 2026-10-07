# 25 — Requisitos e aceitação

Cada requisito deve ter ID estável.

Formato recomendado:

`REQ-<domínio>-<número>`

Cada requisito deve apontar para:
- secção normativa;
- implementação;
- testes;
- evidência de execução.

Exemplos:

- REQ-CAT-001 — CanonicalChannel não depende de source number.
- REQ-CAT-002 — Unknown vai para Review.
- REQ-SRC-001 — Source enabled representa intenção de processamento.
- REQ-SEL-001 — Selection é determinística.
- REQ-DSP-001 — External/Unknown nunca é apagado automaticamente.
- REQ-RUN-001 — Scheduler e manual usam o mesmo coordinator.
- REQ-SEC-001 — Secrets não aparecem em logs.
- REQ-OPS-001 — Fresh install não exige edição manual escondida.

Um requisito só está concluído quando existe teste/evidência.

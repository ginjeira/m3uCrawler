# 43 — Anti-patterns proibidos

1. Criar CanonicalChannel porque uma stream desconhecida apareceu.
2. Usar número do operador para reconhecer identidade.
3. Usar `group-title` como grupo canónico sem este ser propriedade do canal.
4. Usar qualidade para reconhecer identidade.
5. Fazer fuzzy match e escolher silenciosamente um dos empates.
6. Misturar ChannelSource com CanonicalChannel.
7. Apagar ChannelSource porque uma única validação falhou.
8. Apagar Dispatcharr resources sem ownership.
9. Fazer scheduler com regras diferentes do comando manual.
10. Fazer chamadas externas dentro de uma transacção DB longa.
11. Publicar M3U antes de validar a geração.
12. Logar URL com username/password/token.
13. Guardar secret em DTO de resposta.
14. Fazer UI decidir regra de negócio.
15. Usar estado remoto como catálogo.
16. Tratar cache como fonte de verdade.
17. Corrigir comportamento contraditório apenas no teste, sem corrigir a causa.
18. Introduzir uma entidade cuja responsabilidade já pertence a outra sem ADR.
19. Resolver uma decisão marcada A DECIDIR por iniciativa do agente.
20. Considerar "compila" como critério de conclusão.
21. Tratar um ADR `Proposed` como autoridade normativa.
22. Resolver um `TBD` arquitectural/comportamental por iniciativa do agente.
23. Executar fuzzy matching sem `RecognitionPolicy.Fuzzy.Enabled = true` (fuzzy não é opt-out).
24. Tratar o score de fuzzy como `MatchConfidence` ou assumir valores de métodos diferentes como comparáveis.
25. Colapsar o passo de nome normalizado no passo de alias, ou tratar `IdentityRule` como excepção silenciosa à ordem.
26. Tratar `Rejected` como resultado de Recognition, ou confundir `Unknown`/`Ambiguous` com `Excluded`.
27. Promover literais de threshold/margem/pesos do código a valores normativos (são `PARAMETER_GAP`).
28. Reutilizar `Ambiguous` sem qualificar o estágio (`Recognition` vs `Selection`).

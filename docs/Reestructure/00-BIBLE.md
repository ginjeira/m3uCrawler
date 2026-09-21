# m3uCrawler BÍBLIA

## 00 — Constituição da BÍBLIA

**Estado:** NORMATIVO — BASE ARQUITECTURAL
**Versão:** 1.2
**Data:** 2026-09-18

### 1. Finalidade

A BÍBLIA é a fonte normativa de verdade do projecto m3uCrawler. Define o produto, o seu comportamento, o modelo de domínio, os contratos, as invariantes, os limites, as fases de construção e os critérios pelos quais uma implementação pode ser considerada correcta.

A BÍBLIA não descreve necessariamente a implementação actualmente existente. Quando a implementação, documentação histórica ou comportamento observado divergir desta BÍBLIA, a divergência é um problema de implementação/migração, salvo decisão formal posterior que altere a própria BÍBLIA.

### 2. Autoridade

Ordem de autoridade:

1. instrução explícita actual do proprietário do projecto;
2. BÍBLIA aprovada;
3. ADRs que alterem formalmente uma decisão da BÍBLIA;
4. documentação derivada;
5. código;
6. testes;
7. comportamento observado;
8. histórico/conversa.

Um ADR não pode contradizer a BÍBLIA sem actualizar explicitamente a secção normativa afectada.

### 3. Documentação derivada

* `IMPLEMENTATION_ROADMAP.md` descreve a sequência de implementação.
* `AGENTS.md` descreve regras operacionais para agentes/programadores.
* `PROJECT_STATUS.md` regista o estado corrente da implementação do projecto, incluindo fases/waves concluídas, em curso e pendentes, validações, commits de referência, divergências conhecidas e próximos passos.
* `docs/project/waves/` contém os registos históricos das waves de implementação e respectiva evidência.
* ADRs registam racional e decisões que necessitam de histórico.
* documentação de operação explica instalação, manutenção e recuperação.

A documentação de execução (`PROJECT_STATUS.md` e `docs/project/waves/`) descreve o estado da implementação e a evidência disponível; **não constitui fonte normativa do produto e não pode redefinir silenciosamente qualquer conceito da BÍBLIA**.

Nenhum destes documentos pode redefinir silenciosamente um conceito da BÍBLIA.

### 4. Léxico normativo

* **DEVE / MUST:** requisito obrigatório.
* **NÃO DEVE / MUST NOT:** comportamento proibido.
* **DEVERIA / SHOULD:** comportamento recomendado, podendo existir excepção documentada.
* **PODE / MAY:** permitido mas não obrigatório.
* **INVARIANTE:** condição que nunca pode ser violada por uma implementação válida.
* **CONTRATO:** comportamento observável que outros componentes podem assumir.
* **DERIVADO:** informação calculada a partir de dados de autoridade.
* **HISTÓRICO:** informação sobre implementação anterior; não define o produto.

### 5. Regra de completude

Uma decisão é insuficientemente especificada se um programador razoável puder implementar duas soluções materialmente diferentes e ambas parecerem compatíveis com a documentação.

Sempre que isso possa acontecer, a BÍBLIA (ou o ADR associado) deve definir:

* significado;
* autoridade;
* entradas;
* transformação;
* saída;
* estados;
* erros;
* persistência;
* idempotência;
* segurança;
* testes;
* critérios de aceitação;
* contratos (especificação de formato de dados JSON, API, etc.) quando aplicável;
* estratégias de migração/upgrade e resolução de conflitos para dados persistidos.

**Fronteira norma/detalhe.** A implementação pode escolher nomes/tabelas físicas diferentes, mas não pode alterar sem decisão normativa significado, tipo lógico, nullability, cardinalidade, unicidade, referências, lifecycle ou invariantes. Quando um detalhe físico for material para comportamento ou migração, deve existir secção normativa explícita. `TBD` é permitido apenas para parâmetros ou detalhe físico que não altere arquitectura/comportamento normativo; uma questão arquitectural ou comportamental NÃO pode permanecer indefinida sob `TBD`.

**Aceitação.** A matriz de aceitação deve permitir rastrear `Requirement → BÍBLIA → implementação → teste → evidência → gate`.

### 6. Regra de governação

A BÍBLIA é normativa, mas não é infalível por definição. A sua garantia de verdade depende de revisão, testes e controlo de alterações. Qualquer alteração normativa deve identificar a secção afectada, o motivo, os impactos e os documentos/testes que têm de acompanhar a mudança.

Quando uma decisão da BÍBLIA é delegada a um ADR, aplica-se a mesma Regra de Completude: **o ADR deve especificar todos os itens acima**. Uma delegação não reduz o nível de detalhamento exigido; cada aspecto deve estar explicitamente fechado. Critério de aceitação: todo ADR pendente será reavaliado contra este critério; se faltar qualquer item essencial da lista acima, o ADR deve ser marcado como `BIBLE_GAP` e não aplicado.

Um ADR não é mecanismo obrigatório de decisão nem fonte de autoridade; a BÍBLIA é a autoridade normativa. ADRs registam racional e histórico e não substituem a norma.

### 7. Mapa

Este conjunto cobre:

* produto e casos de utilização;
* domínio e dados;
* discovery e aquisição;
* reconhecimento de canais;
* review;
* país/media;
* sources;
* validação;
* selecção;
* ordering;
* composição;
* Dispatcharr;
* runs/scheduler;
* configuração/first-run;
* API/dashboard/CLI;
* persistência;
* segurança;
* observabilidade;
* falhas;
* testes;
* deployment;
* migração;
* requisitos e reconstrução.

### 8. Registo da implementação

A BÍBLIA define **o que o sistema deve ser**; os registos de implementação documentam **o que foi construído e validado**.

`PROJECT_STATUS.md` é o ponto de entrada para o estado corrente do projecto. Deve permitir determinar, sem reconstruir a história da conversa, pelo menos:

* fase e wave actual;
* trabalho concluído;
* trabalho em curso;
* trabalho pendente;
* último estado validado;
* commit de referência;
* validações realizadas;
* divergências ou gaps conhecidos;
* decisões pendentes;
* próxima unidade de trabalho conhecida.

Cada wave concluída deve possuir, quando aplicável, um registo em `docs/project/waves/` que preserve o contexto da execução, o resultado, a validação e a evidência necessária para compreender o estado atingido.

Os registos de implementação devem ser consistentes com a BÍBLIA. Quando uma implementação divergir de um requisito normativo, a divergência deve ser registada como tal; o registo de implementação não pode transformar a divergência em comportamento normativo.

A ausência de um registo de implementação não altera os requisitos da BÍBLIA.

**Regra final:** se um detalhe necessário para construir ou operar o sistema não estiver aqui ou num documento normativo explicitamente referenciado, esse detalhe deve ser decidido antes de ser considerado parte do produto.

# 01 — Produto, objectivos e experiência final

## 1. O que é o m3uCrawler

O m3uCrawler é um sistema que descobre fontes de televisão/radio, adquire playlists e streams, reconhece os canais segundo um catálogo canónico, avalia as fontes disponíveis, escolhe as streams que satisfazem as políticas configuradas, constrói playlists normalizadas e, opcionalmente, reconcilia o resultado com Dispatcharr.

O sistema existe para transformar informação heterogénea de múltiplas fontes num resultado determinístico, controlável e auditável.

## 2. Resultado pretendido

O utilizador deve conseguir:

1. configurar as fontes que pretende detectar;
2. executar discovery;
3. identificar contas/playlists/streams;
4. reconhecer streams como canais canónicos;
5. resolver desconhecidos através de Review;
6. definir prioridades e políticas;
7. validar disponibilidade/qualidade;
8. obter uma playlist coerente e determinística;
9. publicar o resultado;
10. sincronizar com Dispatcharr sem assumir controlo sobre recursos externos;
11. repetir o processo sem churn desnecessário.

## 3. Princípio central

**Configurar um canal no catálogo significa que o operador quer que esse canal seja reconhecido/detectado.**

Não existe uma segunda lista de canais usada apenas para "permitir" a detecção. O catálogo é a autoridade de identidade e o conjunto de canais que o operador escolheu conhecer.

A existência de uma Source significa que o operador quer que aquela origem seja considerada pelo sistema. A existência de um CanonicalChannel significa que o operador quer reconhecer aquela identidade.

## 4. Não-objectivos

O m3uCrawler não deve:
- inventar identidades canónicas a partir de streams desconhecidas;
- usar a numeração de um operador como identidade;
- tratar group-title de origem como verdade canónica;
- decidir identidade através de qualidade;
- deixar Dispatcharr tornar-se fonte de verdade do catálogo;
- apagar recursos externos do Dispatcharr por não serem conhecidos pelo crawler;
- misturar VOD com posições de televisão linear;
- esconder ambiguidades de reconhecimento através de heurísticas silenciosas.

## 5. Casos de utilização

### UC-01 — Primeira instalação

Instalação → criação de administrador → configuração/auth Telegram → configuração de fontes → configuração Dispatcharr opcional → readiness → execução.

### UC-02 — Discovery

O sistema consulta os mecanismos de discovery autorizados, produz candidatos e deduplica candidatos que representam a mesma conta/origem.

### UC-03 — Ingestão

Candidato aceite → aquisição → parsing → streams → normalização → classificação → reconhecimento.

### UC-04 — Canal desconhecido

Stream não reconhecida inequivocamente → Review. Não é criada automaticamente uma nova identidade canónica.

### UC-05 — Selecção

Para cada canal elegível, as fontes disponíveis são avaliadas segundo políticas determinísticas e uma stream pode ser seleccionada.

### UC-06 — Publicação

Streams seleccionadas são transformadas numa playlist de saída segundo a ordering list configurada.

### UC-07 — Dispatcharr

O resultado pode ser reconciliado com Dispatcharr. Apenas recursos que o crawler reconhece como seus podem ser removidos automaticamente.

## 6. Experiência operacional

O Dashboard é um control plane. Deve permitir ao operador compreender:
- estado da instalação;
- configuração;
- readiness;
- últimas execuções;
- candidatos;
- canais;
- review;
- sources;
- validação;
- políticas;
- ordering;
- playlists;
- Dispatcharr;
- erros e evidência.

A UI não é autoridade adicional: altera ou consulta o domínio através de contratos da aplicação.

# 21 — Estratégia de testes

## 1. Pirâmide

- unit tests para regras puras;
- integration tests para DB/serviços;
- contract tests para APIs/adapters;
- end-to-end para fluxos;
- security tests;
- performance tests.

## 2. Golden fixtures

O projecto deve manter fixtures representativas de:
- playlists portuguesas;
- aliases;
- duplicados;
- HD/FHD/SD;
- VOD;
- radio;
- desconhecidos;
- ambiguidades;
- múltiplas sources;
- falhas de rede.

## 3. Propriedades

Testar propriedades:
- determinismo;
- idempotência;
- não criação implícita de CanonicalChannel;
- não uso de número como identidade;
- ownership seguro;
- ausência de secrets em output.

## 4. Failure tests

Testar:
- timeout;
- connection refused;
- invalid response;
- cancellation;
- partial Dispatcharr failure;
- restart;
- duplicate discovery;
- concurrent run.

## 5. E2E mínimo

Fresh install:
`bootstrap → admin → Telegram → source → discovery → acquisition → recognition → review → selection → output → Dispatcharr dry-run`

O E2E deve ser repetível.

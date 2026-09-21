# 35 — Modelo de segurança

## Actores

### Administrator
Pode configurar a aplicação, catálogo, policies, sources e integrações.

### Operator
Pode executar/consultar operações autorizadas segundo ACL.

### Runtime
Executa pipeline segundo configuração; não deve possuir capacidade administrativa além da necessária.

### External service
Nunca é actor confiável por defeito.

## ACL (normativa)

Administrator → controlo total das operações autorizadas. Operator → somente operações explicitamente autorizadas. Não inventar operações além das já existentes no inventário de API.

## Trust boundaries

1. browser → API;
2. API → application;
3. application → DB;
4. application → Telegram;
5. application → provider;
6. application → Dispatcharr;
7. application → filesystem.

Cada fronteira exige validação e tratamento de falhas.

## SSRF

Qualquer URL externa é potencialmente hostil. A política é incondicional para as classes núcleo:
- Protocolos permitidos: apenas `http` e `https`.
- Bloquear explicitamente, em IPv4 e IPv6, loopback, private, link-local e metadata endpoints.
- DNS: resolver, validar e fixar o endereço efectivo usado para a ligação (protecção contra DNS rebinding); a validação ocorre contra o endereço efectivo, não apenas contra o hostname textual.
- Redirects: cada salto é reclassificado pela política SSRF.
- Falha ao determinar segurança: fail-closed (a aquisição é recusada).
- Suportar IPv4 e IPv6.

CIDRs, timeouts, número de redirects e limites de resposta permanecem parâmetros operacionais.

## Dados sensíveis

Categorias:
- credentials;
- sessions/tokens;
- authenticated URLs;
- configuration secrets.

Cada categoria deve ter:
- armazenamento;
- exposição permitida;
- retenção;
- rotação;
- sanitização.

## Principle of least privilege

Um componente só recebe a capacidade necessária à sua operação.

## Audit

Operações administrativas e mutações remotas relevantes devem ser auditadas.

## Security invariants

- nenhum secret em log;
- nenhum secret em error message;
- nenhum delete remoto sem ownership;
- nenhum endpoint administrativo sem auth;
- nenhum input URL confiado automaticamente;
- nenhum parser sem limites.

## Incident evidence

Falhas de segurança devem conservar evidência suficiente para investigação sem copiar o segredo para o log.

# 17 — Segurança e threat model

## 1. Segredos

Credenciais de Telegram, Dispatcharr, providers e URLs autenticadas são dados sensíveis.

Nunca:
- logs;
- reports;
- artifacts;
- mensagens de erro;
- screenshots geradas pela aplicação;
- métricas com labels.

## 2. SSRF

Qualquer URL fornecida ou descoberta externamente deve ser tratada como potencialmente hostil.

A política é incondicional para as classes núcleo seguintes.

Devem existir:
- allow/deny policy quando aplicável;
- bloqueio de redes internas;
- controlo de redirects;
- limites de resposta;
- timeouts;
- validação de protocolos.

Regras núcleo, incondicionais:
- Protocolos permitidos: apenas `http` e `https`.
- Bloquear explicitamente, em IPv4 e IPv6, loopback, private, link-local e metadata endpoints.
- DNS: resolver, validar e fixar o endereço efectivo usado para a ligação (protecção contra DNS rebinding); a validação ocorre contra o endereço efectivo, não apenas contra o hostname textual.
- Redirects: cada salto é reclassificado pela política SSRF.
- Falha ao determinar segurança: fail-closed (a aquisição é recusada).
- Suportar IPv4 e IPv6.

CIDRs, timeouts, número de redirects e limites de resposta permanecem parâmetros operacionais.

## 3. Parser

Entradas externas são não confiáveis.

Aplicar:
- limites;
- cancelamento;
- protecção contra documentos gigantes;
- protecção contra campos gigantes;
- controlo de redirects;
- isolamento quando necessário.

## 4. Dashboard

Autenticação e autorização são obrigatórias para operações administrativas.

CSRF deve ser tratado para autenticação baseada em cookie.

## 5. Auditoria

Alterações administrativas relevantes devem produzir AuditRecord:
- actor;
- timestamp;
- operação;
- objecto;
- antes/depois quando apropriado;
- resultado.

Nunca incluir secrets no diff.

## 6. Backups

Secrets NÃO entram no backup normal. No restore devem ser re-provisionados ou recuperados através do mecanismo de secrets definido pelo sistema. Nunca armazenar credenciais em claro no backup.

Backups podem conter secrets e devem ser tratados como material confidencial.

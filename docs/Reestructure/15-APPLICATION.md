# 15 — First Run, Dashboard, API e CLI

## 1. Bootstrap

Bootstrap cria a capacidade mínima de administrar a aplicação:
- NOT_CONFIGURED;
- CONFIGURING;
- READY.

READY não significa necessariamente que todas as integrações estão configuradas.

## 2. Operational Readiness

Readiness é derivada de requisitos operacionais:
- administrador;
- sources necessárias;
- autenticação Telegram quando discovery depende dela;
- country data obrigatória;
- configuração Dispatcharr quando a sincronização estiver activada;
- demais dependências obrigatórias do modo pretendido.

O sistema deve explicar quais requisitos faltam.

## 3. Wizard

O wizard deve ser incremental, não um formulário monolítico.

Cada integração deve ter:
- configuração;
- validação;
- estado;
- erro;
- possibilidade de repetir.

## 4. Telegram

A autenticação deve ser uma operação de aplicação, não depender de `Console.ReadLine`.

Estados conceptuais:
- not configured;
- waiting code;
- waiting password;
- authenticated;
- error.

A sessão deve persistir de forma segura.

## 5. Dispatcharr

Dashboard deve permitir configurar e testar conexão de forma read-only.

Test Connection nunca deve alterar recursos remotos.

## 6. API

API deve possuir contratos explícitos, autenticação e autorização.

## 7. CLI

CLI e Dashboard devem chamar os mesmos serviços de domínio/application. Não devem constituir dois produtos com regras diferentes.

## 8. UI

UI apresenta e solicita decisões; não implementa regras de negócio que não existam no domínio.

# 14 — Configuração, secrets e precedência

## 1. Fontes

Existem duas classes diferentes.

**Configuração técnica:** defaults, ficheiro, environment e CLI.

No scheduler, timezone de agendamento é configuração técnica; timestamps persistidos permanecem UTC.

**Estado funcional persistido:** providers/accounts/sources, policies, ordering, configurações funcionais de integrações e estado administrativo.

A precedência técnica é `defaults < config file < environment < CLI`.

Estado funcional persistido não é uma camada que um restart possa sobrescrever silenciosamente. Quando CLI/env pretender alterar estado funcional persistente, deve existir uma operação explícita de aplicação/migração ou um contrato de runtime claramente definido.

Propriedades funcionais persistidas têm SQLite como autoridade. CLI/ENV NÃO podem alterar silenciosamente estado funcional no arranque. Alteração funcional através de CLI/ENV exige uma operação administrativa explícita.

## 2. Configuração operacional

Configuração funcional deve ser separada de secrets.

## 3. Writer

Writers de configuração devem:
- preservar chaves desconhecidas quando seguro;
- escrever atomicamente;
- usar permissões restritas;
- não logar secrets;
- validar antes de substituir.

## 4. Versionamento

Ficheiros de configuração devem ter versão de schema quando o formato evoluir.

## 5. Secret lifecycle

Secrets devem poder ser:
- configurados;
- substituídos;
- validados;
- revogados/limpos;
- excluídos de logs;
- protegidos em backup.

Secrets NÃO entram no backup normal. No restore devem ser re-provisionados ou recuperados através do mecanismo de secrets definido pelo sistema. Nunca armazenar credenciais em claro no backup.

O mecanismo exacto de armazenamento deve ser decidido por ADR.

## 6. Precedência e execução

O mesmo valor deve ter o mesmo significado no Dashboard, CLI e scheduler.

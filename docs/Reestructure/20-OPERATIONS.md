# 20 — Deployment, upgrade, backup e restore

## 1. Instalação

Uma instalação nova deve conseguir chegar a estado funcional sem passos manuais escondidos.

Todos os dados obrigatórios de runtime devem:
- ser incluídos na imagem/pacote;
- ser gerados pelo bootstrap;
- ou ser explicitamente provisionados pelo instalador.

## 2. Docker

A imagem deve ser reproduzível a partir de commit conhecido.

A configuração de runtime não deve depender do clone do source.

## 3. Upgrade

Upgrade deve:
1. preservar dados;
2. aplicar migrations;
3. validar readiness;
4. iniciar versão nova;
5. permitir verificar health;
6. permitir rollback por imagem + restore quando necessário.

**Ordem de upgrade (normativa).** 1. schema migration; 2. baseline upgrade. Respeitar DL-107: o baseline NUNCA apaga alterações locais; a migration NÃO deve destruir estado funcional existente.

## 4. Backup

Backup deve incluir tudo o que for necessário para reconstrução:
- DB;
- configuração;
- sessões;
- artifacts necessários;
- dados de runtime;
- informação de versão.

Secrets devem permanecer protegidos.

**Secrets.** Secrets NÃO entram no backup normal. No restore devem ser re-provisionados ou recuperados através do mecanismo de secrets definido pelo sistema. Nunca armazenar credenciais em claro no backup.

## 5. Restore

Restore deve ser testável, não apenas uma cópia de ficheiros.

Deve existir procedimento para:
`backup → restore → migration → health → functional test`.

## 6. RPO/RTO

Valores alvo devem ser definidos pelo ambiente de produção e não inventados pelo software.

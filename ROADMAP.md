# ROADMAP.md — Estado e direcção do m3uCrawler

> Este documento representa **o que está concluído, em curso, próximo e conhecido como problemático** no projecto. Não é uma lista especulativa de features, nem um guia técnico. Para procedimentos, ver `DEPLOYMENT.md` e `OPERATIONS.md`. Para a arquitectura da aplicação, ver `m3uCrawler/README.md`.

---

## Estado actual

- **Aplicação funcional** com pipeline Telegram completo: descoberta independente de keyword → detecção M3U → parser → validação por país (threshold 3, matching por tokens) → extracção e teste de streams → relatório detalhado.
- **Dashboard web** (`--web`) com gestão de listas de canais por país, diagnóstico da última execução, últimas playlists descobertas e pré-visualização sanitizada das playlists. Protecção opcional por token (`--web-token`).
- **Modo manutenção** (`--telegram-maintain`) preserva `playlist.m3u` quando não há novos candidatos.
- **Sincronização Dispatcharr** (opt-in via `dispatcharr_enabled=true` em `wtelegram.config`): pós-playlist, o pipeline pode agora gerar um `MatchPlan` (matching puro, determinístico, com normalização + aliases + fuzzy + numeric-sibling guard + source ordering por provider/qualidade/reliability) e aplicá-lo ao Dispatcharr. Default `dispatcharr_dry_run=true` — primeiro rollout escreve apenas `output/dispatcharr_plan_<ts>.json` e `output/dispatcharr_report_<ts>.json`. Ver "Limitações" abaixo.
- **Deployment** migrado para **Docker Compose** com imagem publicada em `ghcr.io/ginjeira/m3ucrawler:latest` e bind mounts absolutos para `/opt/m3ucrawler/runtime-data`. Ver `DEPLOYMENT.md`.
- **Cobertura de testes**: 314 testes unitários, 0 warnings em `dotnet build m3uCrawler.sln --configuration Release` (snapshot em 2026-08-30).
- **Sanitização de credenciais** em logs, relatórios JSON, `RunReport`, preview do dashboard e mensagens de erro. A playlist M3U funcional preserva URLs Xtream reais, como esperado. O `MatchPlanSerializer` re-aplica `CredentialSanitizer.SanitizeUrl` ao campo `streamUrl` antes de escrever.

---

## Concluído (releases recentes)

- `v1.0.0` — pipeline base de descoberta + teste (visão pré-pipeline).
- `v1.1.0` — múltiplas fontes de pesquisa (IPTV-ORG, Free-TV, DuckDuckGo, SearX, etc.).
- `v2.1.0` — limites configuráveis de streams (`--max-streams`, `--fast`), CLI completo, scripts auxiliares.
- **Pipeline Telegram** (commits `39e4216` → `d083d84`): descoberta independente de keyword, parser M3U centralizado, validação por país com matching por tokens e threshold 3, suporte Xtream Codes (URLs de servidor e de playlist), sanitização de credenciais, dashboard alinhado com o pipeline, modo manutenção sem perda de streams, `RunReport` detalhado, `--telegram-maintain` corrigido para preservar `playlist.m3u` quando não há novos candidatos.
- **Filtro per-stream por país** (`c07da69`, `d083d84`): unificação do `CountryChannelValidator` (matching por tokens, famílias canónicas, variantes colapsadas, fallback por `group-title` apenas para categorias explícitas, protecção contra falsos positivos de aliases curtos), integração no pipeline Telegram, testes dedicados.
- **Migração para Docker Compose** (lado repositório concluído): `docker-compose.yml` reescrito com imagem, comando, porta e bind mounts alinhados com a instalação real. Cutover no servidor `/opt/m3ucrawler` é estado operacional externo (ver "Estados externos ao repositório").

---

## Em curso

- **Migração para Docker Compose** (lado repositório concluído): `docker-compose.yml` reescrito com imagem (`ghcr.io/ginjeira/m3ucrawler:latest`), `container_name: m3ucrawler`, `restart: unless-stopped`, comando completo incluindo `--history-hours 360`, bind mounts absolutos para `/opt/m3ucrawler/runtime-data`. A passagem efectiva do container manual para o container Compose no servidor `/opt/m3ucrawler` é estado operacional externo e será validada durante a fase de deployment.
- **Robustez da publicação da imagem**: a tag `:latest` é mutável e re-apontada em cada push a `main`. Não existe ainda um mecanismo de pin imutável por deploy (ex.: `stable`, tag por commit, digest). O plano de migração identifica isto como follow-up.
- **Validação operacional do dashboard com `--web-token`**: o mecanismo existe e está testado, mas a configuração efectiva em produção ainda não está decidida (ver `ROADMAP.md` § Próximas evoluções).

---

## Próximas evoluções conhecidas

> Itens abaixo são intenções declaradas ou pedidos em aberto. Não há data nem承诺 de entrega — é aqui que se regista "queremos isto" sem virar em feature especulativa.

- **Tornar o pin da imagem mais robusto no `docker-compose.yml`**. Opções em estudo:
  - Tag estável (`stable`) publicada em paralelo com `latest` no workflow `docker-ghcr.yml`;
  - Pin por digest (`@sha256:...`) capturado após `docker pull` numa release;
  - Workflow `docker-ghcr.yml` com `concurrency:` para evitar race de pushes simultâneos.
- **Proteger o dashboard em produção** activando `--web-token` no comando Compose. Implicação: passar a exigir `Authorization: Bearer <token>` para `/api/playlist*`. (Hoje a porta `5000` está exposta sem autenticação.)
- **Limpar e remover documentação obsoleta** (`EXEMPLOS.md`, `STREAM_LIMIT_GUIDE.md`) que descreve o comportamento pré-pipeline. Substituir por apontadores para `m3uCrawler/README.md`.
- **Documentar formalmente a API do `RunReport`** e os endpoints do dashboard num único sítio (provavelmente uma secção adicional em `m3uCrawler/README.md` ou um `API.md` — decisão pendente).
- **Dispatcharr sync**: iterações seguintes (não no primeiro incremento) — visibilidade de canais em `ChannelProfile`, gestão de `auto_channel_sync` na conta M3U hospedada, fallback para o fluxo auto-sync quando o utilizador preferir refresh assíncrono, EPG matching.

---

## Problemas técnicos conhecidos

- **Race condition na publicação da imagem GHCR.** O workflow `docker-ghcr.yml` não declara `concurrency:`. Em pushes muito próximos, dois jobs podem correr em paralelo e o `latest` pode ficar a apontar para um commit que não é o último. Mitigação actual: o workflow é triggado por pushes manuais a `main`, o que torna a janela pequena. Mitigação alvo: adicionar `concurrency:` ao workflow.
- **`latest` pode estar dessincronizado de `main`.** Sem credencial GHCR do lado do agente, não é possível confirmar se a tag `latest` foi actualizada após `c07da69` e `d083d84`. O runbook em `DEPLOYMENT.md` § 5 inclui um passo de verificação do digest após `docker compose pull` para apanhar este caso antes do `up -d`.
- **`EXEMPLOS.md` e `STREAM_LIMIT_GUIDE.md` descrevem o comportamento v2.1 (pré-pipeline Telegram)**. Os exemplos CLI ainda mostram apenas `dotnet run -- "iptv portugal"` e fluxos de pesquisa web. Não contradizem a aplicação actual mas confundem quem entra pelo topo.
- **`README.md` (root)** — estado confirmado em 2026-08-30 com `dotnet test m3uCrawler.sln --configuration Release`: **153 testes passados, 0 warnings** em build Release. Os exemplos CLI foram actualizados para `--history-hours 360` e a referência a `m3uCrawler-main.zip` foi removida da estrutura do repositório. Inconsistências remanescentes: ver secção "Dívida documental confirmada" abaixo.
- **Sem testes de integração de rede** (Telegram, HTTP de streams). Os testes são unitários e não exercitam o pipeline ponta-a-ponta. Aceitável para o estado actual; pode tornar-se limitação à medida que o pipeline crescer.

### Dívida documental confirmada

- **`STREAM_LIMIT_GUIDE.md` e `m3uCrawler/EXEMPLOS.md`** descrevem o comportamento v2.1 (pré-pipeline Telegram). Os exemplos CLI ainda mostram apenas `dotnet run -- "iptv portugal"` e fluxos de pesquisa web. Não contradizem tecnicamente a aplicação actual mas confundem quem entra pelo topo. **Estado decidido**: não reescritos nesta fase; marcados aqui como follow-up a tratar numa iteração futura (opções: remover, redireccionar para `m3uCrawler/README.md`, ou substituir por secção mínima alinhada com o pipeline actual).
- **`CHANGELOG.md` `[Unreleased]` mistura features concluídas com afirmações numéricas datadas** (ex.: "87 testes"). A contagem actual, confirmada pela execução de `dotnet test m3uCrawler.sln --configuration Release`, é **153 testes passados**. A contagem em `m3uCrawler/README.md` (50 testes) e em `CHANGELOG.md` (87) está obsoleta. A corrigir em sincronia.

---

## Estados externos ao repositório

Os pontos abaixo **não podem ser determinados a partir do repositório** — são estado operacional do servidor `/opt/m3ucrawler` que será validado apenas durante a fase de deployment:

- **Estado real do container no servidor**: saber se o container `m3ucrawler` actualmente em execução é o manual (criado por `docker run`) ou já o Compose-managed; saber se a migração cutover já foi executada.
- **Alinhamento `ghcr.io/ginjeira/m3ucrawler:latest` ↔ `origin/main`**: se a imagem foi republicada após `c07da69` e `d083d84`. Sem credencial GHCR, não verificável.
- **Decisão operacional sobre `--web-token` em produção**: depende do administrador.
- **Permissões exactas de `/opt/m3ucrawler/runtime-data`**: depende da política local do host.

Estes pontos serão alvo de validação durante a fase de deployment (server-side runbook em `DEPLOYMENT.md` e verificação operacional em `OPERATIONS.md`).

---

## Como ler este documento

- **Quero saber o que existe** → secção "Concluído".
- **Quero saber o que está activo** → secção "Em curso".
- **Quero saber o que vai ser feito a seguir** → secção "Próximas evoluções".
- **Quero saber o que ainda está mal** → secção "Problemas técnicos conhecidos".

Para instruções passo-a-passo de deployment ou operação, ir respectivamente a `DEPLOYMENT.md` e `OPERATIONS.md`.

# OPERATIONS.md — Runbook operacional

> Este documento cobre a **operação do dia-a-dia**: como verificar o estado do deployment, ler logs, acompanhar uma execução, diagnosticar falhas, localizar relatórios e fazer backups. Não é um guia de deployment — para isso ver `DEPLOYMENT.md`. Para a arquitectura da aplicação, ver `m3uCrawler/README.md`.

---

## 1. Verificação de estado

### Container e processo

```bash
# Estado geral do Compose
cd /opt/m3uCrawler-source
docker compose ps

# Estado do container (a correr? restart count? uptime?)
docker ps --filter name=^m3ucrawler$ --format '{{.Status}}'

# Imagem efectivamente em uso (para confirmar que o update chegou)
docker inspect --format '{{.Image}} {{.Created}}' m3ucrawler
docker image inspect --format '{{index .RepoDigests 0}}' ghcr.io/ginjeira/m3ucrawler:latest

# Uso de recursos
docker stats m3ucrawler --no-stream
```

### Health superficial

```bash
# Dashboard responde?
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' http://localhost:5000/

# Bind mounts correctos dentro do container?
docker exec m3ucrawler ls /data/wtelegram.config /data/session.dat
docker exec m3ucrawler ls /opt/playlists/playlist.m3u

# Comando efectivo (verificar que --history-hours 360 está lá)
docker inspect --format '{{index .Config.Cmd}}' m3ucrawler | tr ',' '\n'
```

---

## 2. Logs

### Tail de logs

```bash
# Últimas linhas
cd /opt/m3uCrawler-source
docker compose logs --tail=200 m3ucrawler

# Follow em tempo real
docker compose logs -f m3ucrawler

# Apenas stderr
docker compose logs --tail=200 --no-log-prefix m3ucrawler | grep -v 'Console.WriteLine\|^$'
```

### Filtragem útil

```bash
# Apenas mensagens do Telegram
docker compose logs --tail=500 m3ucrawler | grep -iE 'telegram|wtelegram'

# Apenas erros / warnings
docker compose logs --tail=500 m3ucrawler | grep -iE 'error|warn|exception|fail'

# Última execução do ciclo
docker compose logs --tail=500 m3ucrawler | grep -E 'Iniciando|m3uCrawler'
```

> **Importante**: os logs são sanitizados via `CredentialSanitizer`. URLs Xtream aparecem como `http://host/live/***/***/...` — não como `user:password@`. Se vir credenciais em claro nos logs, é um bug de `CredentialSanitizer` a reportar (ver § Rotação de credenciais).

---

## 3. Acompanhar uma execução Telegram

O ciclo de manutenção corre de 24 em 24 horas (`--loop-hours 24`). Para acompanhar em tempo real:

```bash
docker compose logs -f m3ucrawler
```

Sequência típica de um ciclo bem-sucedido (log abreviado):

```
Iniciando m3uCrawler...
📂 Pasta de saída das playlists: /opt/playlists
🔍 Procurando streams M3U8 para: ...
... (descoberta de candidatos)
... (download + parsing + validação por país)
... (teste de streams)
... (gravação de playlist.m3u, telegram_run_report.json, telegram_maintain_report.json)
... (espera de --loop-hours 24)
```

Para forçar um ciclo fora do horário (sem reiniciar o container):

- A abordagem correcta é reiniciar o container. O `entrypoint` é o início de cada ciclo, não há forma de "disparar" um ciclo individual sem reiniciar.
- Em caso de necessidade operacional, parar e subir:
  ```bash
  docker compose restart m3ucrawler
  ```
  Faz o ciclo arrancar imediatamente.

---

## 4. Localização dos relatórios e artefactos

Todos os artefactos ficam em `/opt/m3ucrawler/runtime-data/output/` (no host) — dentro do container, `/data/output/`. Bind mount do Compose preserva isto.

| Ficheiro | Significado |
|---|---|
| `output/telegram_run_report.json` | `RunReport` da última execução (camelCase). É o diagnóstico principal. |
| `output/telegram_maintain_report.json` | Detalhe adicional do ciclo de manutenção. |
| `output/playlist.m3u` | Playlist consolidada após merge (modo manutenção). Persiste entre ciclos. |
| `output/playlist_temp.m3u` | Streams funcionais do ciclo actual (modo manutenção). |
| `output/telegram_playlist_<timestamp>.m3u` | Saída de uma pesquisa `--telegram` ad-hoc. |
| `output/telegram_report_<timestamp>.json` | Relatório JSON de uma pesquisa `--telegram` ad-hoc. |
| `output/import_history.json` | Histórico persistente. |

### Inspecção rápida do último `RunReport`

```bash
cat /opt/m3ucrawler/runtime-data/output/telegram_run_report.json | jq .
```

Campos principais a verificar:

- `status` — deve ser `completed`.
- `messagesAnalyzed` — número de mensagens Telegram dentro da janela.
- `candidatesFound` — número de candidatos detectados.
- `playlistsDownloaded`, `playlistsInvalid` — health do download.
- `countryMatches` — quantas playlists passaram a validação por país.
- `playlistsRejected` — quantas foram rejeitadas.
- `streamsTested`, `streamsWorking`, `streamsFailed` — health do teste de streams.
- `rejectionReasons` — lista de motivos de rejeição (sanitizados).
- `discoveredPlaylists` — resumo por playlist (sanitizado).

### Endpoint dashboard

| Endpoint | Conteúdo | Sanitizado? |
|---|---|---|
| `GET /api/run-report` | Última `RunReport` | Sim |
| `GET /api/discovered-playlists` | Playlists descobertas | Sim |
| `GET /api/country/validate?country=pt` | Validação de `playlist.m3u` | Sim |
| `GET /api/playlist` | Playlist funcional | **Não** (preserva credenciais por design) |
| `GET /api/playlist_temp` | Playlist temp funcional | **Não** |
| `GET /api/playlist/preview` | Pré-visualização | **Sim** (`SanitizeM3uContent`) |

---

## 5. Diagnóstico de falhas

### 5.1 Falha de discovery

**Sintomas**: `candidatesFound == 0` apesar de `messagesAnalyzed > 0`.

**Causas possíveis**:

- `wtelegram.config` inválido, expirado ou sem credenciais reais.
- Sessão Telegram revogada — apagar `session.dat` e reautenticar.
- Janela `--history-hours 360` insuficiente (improvável com 360h = 15 dias).
- Mensagens recentes simplesmente não têm candidatos (sem URLs `.m3u`/`.m3u8`, sem anexos, sem URLs plausíveis).
- Rate-limit do Telegram (raro, mas possível em janelas muito activas).

**Passos de diagnóstico**:

```bash
# 1. Logs de discovery
docker compose logs --tail=500 m3ucrawler | grep -iE 'candidate|detect|crawl|m3u|attachment'

# 2. Conteúdo do RunReport (campo candidatesFound)
jq '.candidatesFound, .messagesAnalyzed, .rejectionReasons' \
  /opt/m3ucrawler/runtime-data/output/telegram_run_report.json

# 3. Verificar wtelegram.config
docker exec m3ucrawler cat /data/wtelegram.config | head -10   # só estrutura, não valores

# 4. Verificar se session.dat existe e tem tamanho > 0
ls -la /opt/m3ucrawler/runtime-data/session.dat
```

### 5.2 Falha de country filtering

**Sintomas**: `countryMatches == 0`, `playlistsRejected > 0`, `rejectionReasons` contém "país PT não corresponde (canais X/3)".

**Causas possíveis**:

- Aliases do país desactualizados em `runtime-data/countries/pt.json`.
- Listas regionais presentes em `runtime-data/channel-indicators.json` mas a playlist alvo usa nomenclaturas diferentes.
- Threshold 3 demasiado alto para a playlist (raro; playlists reais costumam ter muito mais de 3 canais reconhecíveis).
- Falso positivo de tokenização: aliases curtos a colidir com palavras não relacionadas (já mitigado no código — ver `CountryChannelValidator.AnalyzePlaylist`).

**Passos de diagnóstico**:

```bash
# 1. Quantos canais foram reconhecidos?
jq '.discoveredPlaylists[] | {name, recognizedChannels: (.recognizedChannels // null), rejectionReason: (.rejectionReason // null)}' \
  /opt/m3ucrawler/runtime-data/output/telegram_run_report.json

# 2. A playlist rejeitada tem aliases candidatos no JSON?
jq '.matchedAliases' /opt/m3ucrawler/runtime-data/output/telegram_run_report.json

# 3. Lista de aliases actual
cat /opt/m3ucrawler/runtime-data/countries/pt.json | jq '.channels'

# 4. Indicadores suplementares
cat /opt/m3ucrawler/runtime-data/channel-indicators.json | jq '.indicators | length'
```

**Acção correctiva**: editar `runtime-data/countries/pt.json` ou, em runtime, usar o dashboard (`/api/country/save` POST) para actualizar a lista. O novo ficheiro é lido no próximo ciclo (a cache interna é por processo).

### 5.3 Falhas no teste dos streams

**Sintomas**: `streamsTested > 0` mas `streamsFailed == streamsTested` (todos falham), ou `streamsWorking` muito abaixo do esperado.

**Causas possíveis**:

- Streams caíram entretanto (normal — é por isso que o modo manutenção retesta).
- Problema de rede do servidor (DNS, firewall, proxy).
- Anti-bot nos servidores de origem (raro em streams directos, comum em CDNs atrás de cloudflare).
- Concorrência demasiado alta (`--fast` em host fraco).

**Passos de diagnóstico**:

```bash
# 1. Rácio working/failed
jq '{tested: .streamsTested, working: .streamsWorking, failed: .streamsFailed}' \
  /opt/m3ucrawler/runtime-data/output/telegram_run_report.json

# 2. Rede do servidor (sanity check)
docker exec m3ucrawler curl -sS -o /dev/null -w '%{http_code}\n' https://example.com

# 3. Tempo do teste (no log)
docker compose logs --tail=500 m3ucrawler | grep -iE 'tester|test.*stream|ms$|timeout'

# 4. Se o problema for persistente (>2 ciclos consecutivos), investigar manualmente um stream:
docker exec -it m3ucrawler sh -c 'cat /opt/playlists/playlist_temp.m3u | head -5'
# Pegar num URL e testar fora do container com curl.
```

**Acção correctiva**: aumentar `PlaylistManagerService` não é trivial (é código). Para reduzir falsos negativos temporariamente, considerar reduzir `--max-streams` ou desactivar `--fast` no `docker-compose.yml`.

### 5.4 Falha de autenticação Telegram

**Sintomas**: logs com "phone number", "verification code", "password" — indica que `wtelegram.config` tem valores `ask` e o container está a tentar pedir input interactivo. Como o container não tem TTY interactivo na maioria das configurações, isto falha em loop.

**Causas possíveis**:

- `wtelegram.config` substituído acidentalmente por um template com `ask`.
- Primeira execução sem `wtelegram.config` (esperado: o utilizador precisa de autenticar uma vez).

**Passos de diagnóstico**:

```bash
docker exec m3ucrawler cat /data/wtelegram.config
# Se aparecerem "ask" em vez de credenciais reais, é problema.
```

**Acção correctiva**: repor `wtelegram.config` com credenciais reais a partir de backup seguro (NUNCA versionar). Verificar permissões: `chmod 600`.

---

## 6. Backups

### 6.1 Política recomendada

- **Antes de qualquer update** do Compose: snapshot de `/opt/m3ucrawler/runtime-data/`.
- **Periodicamente** (semanal): snapshot completo.
- Retenção sugerida: 4 snapshots semanais + 1 mensal.

### 6.2 Comando de backup

```bash
# Parar o container para um snapshot consistente (opcional mas recomendado)
docker compose stop m3ucrawler

# Snapshot tar.gz com timestamp
TS=$(date +%Y%m%d_%H%M%S)
sudo tar -czf /opt/backups/m3ucrawler-runtime-data-$TS.tar.gz \
  -C /opt/m3ucrawler runtime-data

# Verificar
ls -la /opt/backups/m3ucrawler-runtime-data-$TS.tar.gz

# Reiniciar
docker compose start m3ucrawler
```

**Notas**:

- O snapshot inclui `wtelegram.config`, `session.dat`, todas as playlists e relatórios. **Tratar como dado sensível** (credenciais). Guardar com `chmod 600` num directório com permissões restritas.
- Em vez de `tar`, pode usar-se `rsync` para um directório de backup incremental.

### 6.3 Restore

```bash
# Parar o container
docker compose stop m3ucrawler

# Confirmar o estado actual (antes de sobrescrever)
ls -la /opt/m3ucrawler/runtime-data

# Extrair o backup por cima (preserva owner:group se correr como root)
sudo tar -xzf /opt/backups/m3ucrawler-runtime-data-<TS>.tar.gz \
  -C /opt/m3ucrawler

# Reiniciar
docker compose start m3ucrawler

# Verificar
docker compose logs --tail=50 m3ucrawler
```

---

## 7. Cuidados com dados persistentes

### 7.1 Nunca

- `rm -rf /opt/m3ucrawler/runtime-data`.
- `docker volume prune` (defensivo — não usamos volumes, mas é hábito perigoso).
- `docker system prune -a` sem verificar primeiro.
- Mover `wtelegram.config` ou `session.dat` enquanto o container corre.
- Commitar `wtelegram.config`, `session.dat` ou qualquer artefacto sob `/opt/m3ucrawler/runtime-data/` ao git.

### 7.2 Rotação de credenciais

Se `wtelegram.config` ou `session.dat` aparecerem acidentalmente em logs, commits, tickets, screenshots ou canais de chat:

1. **Revogar a sessão WTelegram** em https://my.telegram.org (Active sessions → Terminate).
2. Apagar `session.dat` no servidor.
3. Apagar todos os vestígios do segredo (histórico de commits, logs de CI, etc.).
4. Reautenticar com novo `wtelegram.config` (ver § 5.4).
5. Auditar quem teve acesso aos logs.

### 7.3 Espaço em disco

Os relatórios e playlists acumulam ao longo do tempo. Verificar periodicamente:

```bash
du -sh /opt/m3ucrawler/runtime-data
du -sh /opt/m3ucrawler/runtime-data/output
du -sh /opt/m3ucrawler/runtime-data/playlists
```

Limpeza segura (preserva playlists activas):

```bash
# Apaga relatórios antigos (mantém os últimos 5)
cd /opt/m3ucrawler/runtime-data/output
ls -1t telegram_report_*.json | tail -n +6 | xargs -r rm --
ls -1t telegram_playlist_*.m3u | tail -n +6 | xargs -r rm --
# Mantém telegram_run_report.json e telegram_maintain_report.json (último estado)
```

**Nunca** apagar `playlist.m3u` enquanto o modo manutenção estiver activo.

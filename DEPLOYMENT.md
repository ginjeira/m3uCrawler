# DEPLOYMENT.md — Runbook de deployment

> Este documento descreve **a arquitectura de deployment actual, como instalar, actualizar, fazer rollback e verificar** o m3uCrawler no servidor de produção. É um runbook operacional — assume que o leitor é um administrador com acesso SSH e Docker.
>
> Para a operação do dia-a-dia (logs, reports, diagnóstico), ver `OPERATIONS.md`. Para a arquitectura da aplicação, ver `m3uCrawler/README.md`.

---

## 1. Arquitectura de deployment

### Visão geral

```
┌─────────────────────────────────────────────────────────────────┐
│ Servidor                                                       │
│                                                                 │
│  /opt/m3uCrawler-source        /opt/m3ucrawler/runtime-data   │
│  (código fonte, git)           (estado vivo, fora do git)      │
│         │                                │                      │
│         │ docker compose pull/up -d      │                      │
│         ▼                                ▼                      │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ Container m3ucrawler                                     │   │
│  │   image: ghcr.io/ginjeira/m3ucrawler:latest             │   │
│  │   working_dir: /data                                     │   │
│  │   bind mount  /opt/m3ucrawler/runtime-data  → /data     │   │
│  │   bind mount  .../runtime-data/playlists   → /opt/playlists│  │
│  │   comando: --telegram portugal --telegram-maintain ...    │   │
│  │   porta: 5000:5000                                       │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

### Componentes

| Componente | Localização | Persistência |
|---|---|---|
| **Código fonte** | `/opt/m3uCrawler-source` (clone git) | Versionado em `origin/main` |
| **Imagem** | `ghcr.io/ginjeira/m3ucrawler:latest` (publicada via `.github/workflows/docker-ghcr.yml`) | Cache local no daemon Docker |
| **Runtime data** | `/opt/m3ucrawler/runtime-data/` no host | Persistente, fora do git e da imagem |
| **Source of truth do deployment** | `docker-compose.yml` na raiz do repositório | Versionado em `origin/main` |

### Distinção crítica: código fonte vs runtime data

| Aspecto | Código fonte (`/opt/m3uCrawler-source`) | Runtime data (`/opt/m3ucrawler/runtime-data`) |
|---|---|---|
| **Onde vive** | Clone git do repositório | Pasta no host, fora do repo e da imagem |
| **Versionado** | Sim (git) | **Não** |
| **Substituído em update** | Sim (`git pull`) | **Nunca é tocado** |
| **Conteúdo** | `m3uCrawler/`, `docker-compose.yml`, `Dockerfile`, workflows, testes, docs, `m3uCrawler/runtime-data/` (placeholder) | `session.dat`, `wtelegram.config`, `countries/pt.json`, `playlists/`, `output/`, `telegram_run_report.json`, `telegram_maintain_report.json`, `import_history.json`, `channel-indicators.json` (em alguns setups) |
| **Sobrevive a `docker compose down`** | Sim | **Sim** (está no host, não no container) |

**Regra de ouro**: `/opt/m3ucrawler/runtime-data` é o único local que contém estado vivo da sessão Telegram e das playlists geradas. **Nenhuma operação de deployment deve apagar, mover ou sobrescrever este directório.**

---

## 2. Conteúdo de `docker-compose.yml` (fonte de verdade)

```yaml
services:
  m3ucrawler:
    image: ghcr.io/ginjeira/m3ucrawler:latest
    container_name: m3ucrawler
    restart: unless-stopped
    working_dir: /data
    command:
      - --telegram
      - portugal
      - --telegram-maintain
      - --loop-hours
      - "24"
      - --history-hours
      - "360"
      - --max-streams
      - "500"
      - --output-dir
      - /opt/playlists
      - --web
      - --web-port
      - "5000"
    ports:
      - "5000:5000"
    volumes:
      - /opt/m3ucrawler/runtime-data:/data
      - /opt/m3ucrawler/runtime-data/playlists:/opt/playlists
```

Notas:

- **`image:`** aponta para a imagem pública em GHCR. Sem `build:` — o servidor **só faz pull**, nunca build local.
- **`container_name: m3ucrawler`** preserva o nome histórico do container. Implica que, ao fazer cutover, é necessário remover o container manual antigo antes do `up -d` (ver § 6).
- **`restart: unless-stopped`** mantém o ciclo de manutenção a correr após reboot do host.
- **`command`** é a forma long-form do mesmo comando que era passado ao `docker run` manual, **incluindo** `--history-hours 360`.
- **`volumes`** usa caminhos absolutos no host (não `./m3uCrawler/runtime-data`). Isto torna o `docker-compose.yml` independente do CWD.

---

## 3. Bind mounts e caminhos no host

| Host path | Container path | Conteúdo |
|---|---|---|
| `/opt/m3ucrawler/runtime-data` | `/data` | Tudo: sessão, config, países, playlists, output, relatórios, histórico |
| `/opt/m3ucrawler/runtime-data/playlists` | `/opt/playlists` | Sub-montagem para alinhar com `--output-dir /opt/playlists` do comando |

O duplo bind mount é intencional: `/data` é a working directory da imagem (onde a app lê `wtelegram.config`, `session.dat`, `countries/`); `/opt/playlists` é o directório de saída das playlists (que a app usa quando recebe `--output-dir /opt/playlists`). Ambos precisam de apontar para o mesmo `/opt/m3ucrawler/runtime-data` no host.

**Não substituir bind mounts por volumes Docker anónimos ou nomeados.** Os dados têm de continuar a viver em `/opt/m3ucrawler/runtime-data` para que backups e operações de ficheiros (`ls`, `cp`, `tar`) possam ser feitos directamente no host.

---

## 4. Pré-flight antes de qualquer operação

Antes de instalar, actualizar ou fazer rollback, executar **todas** as verificações abaixo. Se alguma falhar, **parar** e investigar antes de prosseguir.

```bash
# 1. Container está a correr?
docker ps --filter name=^m3ucrawler$ --format '{{.ID}} {{.Image}} {{.Status}}'

# 2. Que imagem está a ser usada (para eventual rollback)?
RUNNING_IMAGE=$(docker inspect --format '{{.Image}}' m3ucrawler)
echo "Imagem actual: $RUNNING_IMAGE"
docker image ls --format '{{.Repository}}:{{.Tag}}\t{{.ID}}\t{{.CreatedSince}}' \
  | grep -E '(m3ucrawler|ginjeira/m3ucrawler)'

# 3. Runtime data está intacto?
ls -la /opt/m3ucrawler/runtime-data
test -f /opt/m3ucrawler/runtime-data/wtelegram.config && echo "wtelegram.config: OK"
test -f /opt/m3ucrawler/runtime-data/session.dat && echo "session.dat: OK"
ls -la /opt/m3ucrawler/runtime-data/playlists | head
ls -la /opt/m3ucrawler/runtime-data/output | head

# 4. Repo git está sincronizado?
cd /opt/m3uCrawler-source
git fetch --ff-only origin main
git status
git rev-parse origin/main
git rev-parse HEAD
# Esperado: HEAD == origin/main (após git pull) e corresponde ao commit documentado na release.
```

---

## 5. Instalação inicial (caso o servidor ainda não tenha deployment)

```bash
# 1. Clonar o repositório
git clone https://github.com/ginjeira/m3uCrawler.git /opt/m3uCrawler-source
cd /opt/m3uCrawler-source

# 2. Criar o directório de runtime data com permissões correctas
sudo mkdir -p /opt/m3ucrawler/runtime-data/playlists
sudo mkdir -p /opt/m3ucrawler/runtime-data/output
# Permissões: ajustar conforme o utilizador que corre o docker daemon. Em setups típicos,
# 755 ou 775 para o grupo docker funciona; ajustar conforme política local.

# 3. Copiar wtelegram.config (a partir de backup seguro) e session.dat (se aplicável)
#    NUNCA versionar nem criar a partir de segredos conhecidos.
sudo cp /caminho/seguro/wtelegram.config /opt/m3ucrawler/runtime-data/wtelegram.config
sudo chmod 600 /opt/m3ucrawler/runtime-data/wtelegram.config
# session.dat: se existir de uma instalação anterior, copiar; senão, será criado
# na primeira autenticação Telegram.

# 4. Pull da imagem
docker compose pull

# 5. (Opcional) Tag para rollback
docker tag ghcr.io/ginjeira/m3ucrawler:latest m3ucrawler:previous

# 6. Subir o container
docker compose up -d

# 7. Verificar (ver § 7)
docker compose ps
docker compose logs --tail=200 m3ucrawler
```

---

## 6. Cutover do container manual para o Compose (primeira passagem)

Esta secção aplica-se à transição única em que o container `m3ucrawler` foi criado manualmente (via `docker run`) e precisa de passar a ser gerido por Compose.

```bash
# 0. Pré-flight (ver § 4)

# 1. Pin da imagem actual como rollback
RUNNING_IMAGE=$(docker inspect --format '{{.Image}}' m3ucrawler)
docker tag "$RUNNING_IMAGE" m3ucrawler:rollback-pre-compose
docker image ls m3ucrawler:rollback-pre-compose

# 2. Parar (mas não remover) o container antigo
docker stop m3ucrawler
docker ps -a --filter name=^m3ucrawler$ --format '{{.Status}}'   # deve mostrar "Exited"

# 3. Remover o container parado (dados estão no bind mount, não no FS do container)
docker rm m3ucrawler

# 4. Pull + verificar + subir via Compose
cd /opt/m3uCrawler-source
git pull --ff-only origin main    # apanha a versão mais recente do docker-compose.yml
docker compose pull
docker image inspect --format '{{index .RepoDigests 0}}' ghcr.io/ginjeira/m3ucrawler:latest
docker tag ghcr.io/ginjeira/m3ucrawler:latest m3ucrawler:previous
docker compose up -d

# 5. Aceitação (ver § 7)
```

---

## 7. Aceitação pós-deployment

Todas as condições abaixo devem ser verdadeiras. Se alguma falhar, ir a § 8 (Rollback).

1. **`docker compose ps`** mostra `m3ucrawler` como `running`, port mapping `5000->5000`.
2. **Bind mounts intactos dentro do container**:
   ```bash
   docker exec m3ucrawler ls /data | grep -E 'wtelegram.config|session.dat'
   # esperado: ambos os ficheiros aparecem
   docker exec m3ucrawler ls /opt/playlists
   # esperado: lista contém pelo menos uma playlist pré-existente (ex.: playlist.m3u)
   ```
3. **Dashboard responde**:
   ```bash
   curl -sS -o /dev/null -w '%{http_code}\n' http://localhost:5000/
   # esperado: 200
   ```
4. **Argumentos preservados**:
   ```bash
   docker inspect --format '{{index .Config.Cmd}}' m3ucrawler
   # esperado: contém a string "history-hours" e "360"
   ```
5. **Telegram autentica** (logs do primeiro ciclo):
   ```bash
   docker compose logs --tail=200 m3ucrawler | grep -iE 'telegram|login|wtelegram'
   # esperado: login bem-sucedido (sem prompt interactivo)
   ```
6. **Primeiro ciclo de manutenção arranca** dentro de ~24h:
   ```bash
   sleep 30 && docker compose logs --tail=50 m3ucrawler | grep -iE 'ciclo|cycle|maintain'
   ```

---

## 8. Update normal

O fluxo de update padrão depois do cutover é:

```bash
cd /opt/m3uCrawler-source
git pull --ff-only origin main
docker compose pull
docker compose up -d
```

**Importante**: o `docker compose up -d` respeita a config do ficheiro. Se o `docker-compose.yml` foi alterado no pull (por exemplo, nova flag ou novo bind mount), o `up -d` reconcilia o container existente com a nova config — não recria do zero a menos que algo estrutural mude.

Para confirmar que a nova imagem está em uso:

```bash
docker compose pull
docker image inspect --format '{{index .RepoDigests 0}}' ghcr.io/ginjeira/m3ucrawler:latest
docker compose up -d
docker inspect --format '{{.Image}}' m3ucrawler
```

Compare o digest com `git rev-parse origin/main` — espera-se que a imagem tenha sido publicada nas últimas horas a partir desse commit. Se a imagem parecer muito antiga em relação ao commit, verificar os GitHub Actions de `docker-ghcr.yml` no commit correspondente antes de continuar (ver § 10).

---

## 9. Rollback

Rollback significa voltar ao estado anterior ao deployment. Há dois cenários:

### 9.1 Rollback da imagem (mesmo `docker-compose.yml`)

Use este cenário se a nova imagem arrancou mas a app está a falhar.

```bash
cd /opt/m3uCrawler-source
docker compose down                              # para o container actual (preserva bind mounts)
docker tag m3ucrawler:previous m3ucrawler:current
# Editar temporariamente o docker-compose.yml para usar a tag antiga (se necessário) OU
# re-pinar manualmente:
docker tag m3ucrawler:previous ghcr.io/ginjeira/m3ucrawler:latest  # override local
docker compose up -d
```

**Atenção**: o segundo `docker tag` substitui o `latest` **localmente** apenas. Não afecta o GHCR. Para reverter: `docker pull ghcr.io/ginjeira/m3ucrawler:latest` (vai re-puxar do registry).

### 9.2 Rollback completo (voltar ao container manual pré-Compose)

Use este cenário se o `docker-compose.yml` mudou estruturalmente e a nova config é incompatível.

```bash
cd /opt/m3uCrawler-source
docker compose down

docker run -d --name m3ucrawler \
  --restart unless-stopped \
  -w /data \
  -p 5000:5000 \
  -v /opt/m3ucrawler/runtime-data:/data \
  -v /opt/m3ucrawler/runtime-data/playlists:/opt/playlists \
  m3ucrawler:rollback-pre-compose \
  --telegram portugal --telegram-maintain --loop-hours 24 --history-hours 360 \
  --max-streams 500 --output-dir /opt/playlists --web --web-port 5000

docker ps --filter name=^m3ucrawler$
```

Isto restaura byte-a-byte o estado anterior: bind mounts idênticos, mesma imagem.

---

## 10. Verificação adicional: alinhamento `latest` ↔ `main`

A imagem `:latest` é mutável: é re-apontada em cada push a `main`. Existe o risco (baixo, mas real) de o workflow `docker-ghcr.yml` ter falhado silenciosamente após um push, deixando o `latest` dessincronizado.

Para confirmar:

```bash
# 1. Qual é o commit que o origin/main aponta?
git rev-parse origin/main

# 2. O :latest foi publicado depois desse commit?
# (a) via digest: requer auth GHCR, fora do alcance deste runbook.
# (b) via timing: o "Created" da imagem deve ser posterior ao timestamp do commit.
docker image inspect --format '{{.Created}}' ghcr.io/ginjeira/m3ucrawler:latest
git log -1 --format='%cI' origin/main
```

Se a imagem for mais antiga que o commit esperado, **parar** e:

1. Abrir GitHub → Actions → `Docker GHCR Publish` para os commits `c07da69`, `d083d84` e qualquer outro pendente.
2. Identificar runs falhadas (ex.: login expirado, rate-limit).
3. Re-trigger via `workflow_dispatch` no workflow se necessário.
4. Como workaround temporário, fazer pull da tag por SHA (workflow publica `type=sha`):
   ```bash
   docker pull ghcr.io/ginjeira/m3ucrawler:sha-<short-sha>
   docker tag ghcr.io/ginjeira/m3ucrawler:sha-<short-sha> m3ucrawler:previous
   # Usar image: m3ucrawler:previous no docker-compose.yml (override local)
   ```

---

## 11. Regras explícitas para não perder dados

- **Nunca** `rm -rf /opt/m3ucrawler/runtime-data` ou equivalente.
- **Nunca** `docker volume prune` (não usamos volumes, mas é hábito perigoso).
- **Nunca** `docker system prune -a` sem verificar primeiro que o `m3ucrawler:rollback-pre-compose` e `m3ucrawler:previous` não serão apagados.
- **Nunca** mover `wtelegram.config` ou `session.dat` para fora de `/opt/m3ucrawler/runtime-data/` enquanto o container está a correr.
- **Backups** (ver `OPERATIONS.md` § Backups): fazer **antes** de qualquer update se houver risco.
- **Credenciais**: `wtelegram.config` e `session.dat` nunca devem aparecer em logs, commits, diffs, documentação, tickets, screenshots ou mensagens. Se aparecerem acidentalmente em logs, ver `OPERATIONS.md` § Rotação de credenciais.

---

## 12. Glossário

- **GHCR**: GitHub Container Registry, em `ghcr.io`.
- **Bind mount**: mapeamento de um directório do host para um directório do container. Diferente de um volume Docker nomeado.
- **Cutover**: momento da primeira passagem de "container manual" para "container Compose".
- **Digest**: identificador SHA256 imutável de uma imagem Docker. O `latest` é um alias mutável que aponta para uma digest específico num dado momento.

# m3uCrawler

Um crawler avançado em C# para pesquisar, testar e guardar streams M3U8 funcionais numa playlist M3U.

## 🚀 Funcionalidades

- 🔍 **Pesquisa automática** de ficheiros M3U8 na internet usando múltiplos motores de busca
- 🧪 **Teste de funcionalidade** paralelo dos streams encontrados
- 💾 **Geração de playlist M3U** com apenas streams funcionais
- 📊 **Relatórios detalhados** em formato JSON
- ⚡ **Processamento paralelo** para testes rápidos e eficientes
- 🎨 **Interface colorida** no console com emojis
- 📝 **Sistema de logging** detalhado
- ⚙️ **Configuração flexível** via JSON
- 🤖 **Modo Telegram** com pesquisa por termo e teste automático de streams
- 🌐 **Filtro por domínio** para manter apenas resultados de um domínio específico
- 🔎 **Scan de domínio** para verificar se um domínio publica playlists de forma pública

## 📦 Instalação

### Pré-requisitos
- .NET 9.0 ou superior
- Windows, Linux ou macOS

### Compilar o projeto
```bash
git clone <repository-url>
cd m3uCrawler/m3uCrawler
dotnet restore
dotnet build
```

## 🐳 Docker

### Build local
```bash
cd m3uCrawler
docker build -t m3ucrawler:latest .
```

### Build direto do GitHub (sem clone local)
```bash
docker build -t m3ucrawler:latest https://github.com/<teu-user>/<teu-repo>.git#main:m3uCrawler
```

### Executar com Docker
```bash
# Primeira execução do Telegram: usar -it para introduzir código/2FA
docker run --rm -it \
   -v $(pwd)/runtime-data:/data \
   m3ucrawler:latest --telegram portugal --max-streams 50
```

### Executar com Docker Compose
```bash
# Na raiz do repositório
docker compose build
docker compose run --rm -it m3ucrawler --telegram portugal --max-streams 50
```

Notas Docker:

- Todos os dados de runtime (output, sessão Telegram e config opcional) ficam em `m3uCrawler/runtime-data`.
- Se quiseres usar `wtelegram.config`, coloca o ficheiro em `m3uCrawler/runtime-data/wtelegram.config`.
- Em produção, podes usar a imagem publicada no GHCR em vez de build local.

### Publicação automática para GHCR (GitHub Container Registry)

Este repositório inclui o workflow `.github/workflows/docker-ghcr.yml`.

Quando fazes push para `main`/`master` ou tags `v*`, a action:

- faz build da imagem com contexto `m3uCrawler/`
- publica em `ghcr.io/<owner>/m3ucrawler`
- gera tags (`latest`, branch, tag e sha)

Exemplo de pull no servidor:

```bash
docker pull ghcr.io/<owner>/m3ucrawler:latest
docker run --rm -it -v /opt/m3ucrawler-data:/data ghcr.io/<owner>/m3ucrawler:latest --scan-domain example.com --max-streams 300
```

## 🔧 Como usar

### Execução rápida
```powershell
# Windows (PowerShell)
.\run.ps1 "sports"

# Windows (CMD)
run.bat "sports"

# Multiplataforma
dotnet run -- "sports"
```

### Modo interativo
```bash
dotnet run
# Digite o termo de pesquisa quando solicitado
```

### Argumentos da linha de comando
```bash
# Pesquisa por desporto
dotnet run -- "football live stream"

# Pesquisa por notícias
dotnet run -- "news channels"

# Múltiplas palavras
dotnet run -- "tv channels portugal"

# Filtrar resultados por domínio
dotnet run -- "iptv" --domain example.com

# Scan direto ao domínio (sem Telegram)
dotnet run -- --scan-domain example.com --max-streams 300

# Modo Telegram com termo por parâmetro
dotnet run -- --telegram portugal

# Modo manutenção (playlist.m3u + playlist_temp.m3u)
dotnet run -- --telegram portugal --telegram-maintain --loop-hours 24 --max-streams 500 --history-hours 72
```

### Modo manutenção de playlist (24h)

Quando usado com `--telegram-maintain`, o fluxo é:

- limpa `output/playlist_temp.m3u` no início de cada ciclo
- pesquisa/testa Telegram e escreve resultados funcionais em `output/playlist_temp.m3u`
- carrega links existentes de `output/playlist.m3u` e retesta
- remove os links que já não funcionam
- junta os links novos de `playlist_temp.m3u` em `playlist.m3u`
- evita duplicados por URL

Com `--loop-hours 24`, o processo repete automaticamente de 24 em 24 horas.

`--history-hours` define quantas horas para trás o Telegram é pesquisado (padrão: 48h).

## 🆕 Novos métodos (CLI)

### 1) Filtro por domínio

Use `--domain DOMINIO` para manter apenas URLs cujo host seja o domínio indicado ou subdomínios.

Exemplo:

```bash
dotnet run -- "iptv" --domain cdn.example.com
```

### 2) Scan de domínio

Use `--scan-domain DOMINIO` para fazer um scan direto ao domínio e descobrir playlists públicas.

Este modo:

- testa endpoints comuns como `/playlist.m3u`, `/playlist.m3u8`, `/index.m3u8`, `/get.php?type=m3u`
- analisa HTML, robots.txt e sitemap.xml para extrair links de playlist
- devolve quantas playlists foram encontradas e uma amostra das URLs

Exemplo:

```bash
dotnet run -- --scan-domain example.com --max-streams 300
```

### 3) Modo Telegram por argumento

Use `--telegram` com termo opcional para pesquisar sem prompt interativo.

Exemplos:

```bash
dotnet run -- --telegram portugal
dotnet run -- --telegram "sport tv"
```

## 📂 Estrutura dos arquivos gerados

### Playlist M3U
```m3u
#EXTM3U
#PLAYLIST:m3uCrawler - Generated on 2025-11-01 15:30:00

#EXTINF:-1 tvg-name="Sports Channel" tvg-logo="" group-title="Sports",Sports Channel
https://example.com/sports.m3u8
```

### Relatório JSON
```json
{
  "generatedAt": "2025-11-01T15:30:00",
  "totalStreams": 10,
  "workingStreams": 3,
  "nonWorkingStreams": 7,
  "averageResponseTime": 1250.5,
  "streams": [...]
}
```

## ⚙️ Configuração

O arquivo `config.json` permite personalizar:

- **Motores de busca** utilizados
- **Limites de tempo** e tentativas
- **Filtros de qualidade** 
- **Configurações de rede**
- **Formato de saída**

## 🏗️ Arquitetura do projeto

```
m3uCrawler/
├── Models/
│   └── M3uStream.cs           # Modelo dos streams M3U8
├── Services/
│   ├── M3uCrawlerService.cs   # Serviço de pesquisa na web
│   ├── M3uTesterService.cs    # Serviço de teste de streams
│   ├── PlaylistManagerService.cs # Gestão de playlists
│   └── LoggingService.cs      # Sistema de logging
├── Program.cs                 # Ponto de entrada da aplicação
├── config.json               # Configurações avançadas
├── appsettings.json          # Configurações básicas
└── output/                   # Diretório de saída
```

## 📊 Exemplos de saída

```
=== m3uCrawler - Pesquisador de Streams M3U8 ===
Versão 1.0 - Novembro 2025

🔍 Procurando streams M3U8 para: sports
📋 Encontradas 25 URLs M3U8

🧪 Testando streams...
✓ https://example1.com/sport.m3u8 (1200ms)
✗ https://example2.com/dead.m3u8 (Timeout)
✓ https://example3.com/live.m3u8 (850ms)

✅ Streams funcionais: 15/25

📊 Estatísticas:
   • Total testado: 25
   • Funcionais: 15
   • Não funcionais: 10
   • Tempo médio resposta: 1025ms

✨ Arquivos gerados:
   • Playlist: output/playlist_20251101_153000.m3u
   • Relatório: output/report_20251101_153000.json
```

## 🔧 Dependências

- **HtmlAgilityPack** - Para análise de HTML e web scraping
- **System.Text.Json** - Para serialização e deserialização JSON

## 🚀 Melhorias futuras

- [ ] Cache de resultados para evitar retestes
- [ ] Interface web opcional
- [ ] Suporte a outros formatos (PLS, XSPF)
- [ ] Integração com APIs de streaming
- [ ] Sistema de categorização automática
- [ ] Suporte a proxy e VPN

## ⚖️ Aviso Legal

Este software é destinado apenas para fins educacionais e de pesquisa. 

**Importante:**
- Certifique-se de que tem permissão para aceder aos streams
- Respeite os direitos autorais e termos de serviço
- Use apenas para conteúdo legal e autorizado
- O desenvolvedor não se responsabiliza pelo uso indevido

## 📄 Licença

Este projeto está sob a licença MIT. Consulte o arquivo LICENSE para mais detalhes.

---

**Desenvolvido com ❤️ em C# - Novembro 2025**

# m3uCrawler

Um crawler em C# (.NET 9) para descobrir, validar e testar playlists M3U/M3U8 encontradas em fontes públicas (incluindo Telegram), classificando-as por país e gerando playlists consolidadas com os streams funcionais.

## Funcionalidades

- 🔍 **Descoberta automática de candidatos** em fontes públicas (modo Telegram e modo scan de domínio)
- 🧪 **Pipeline de validação e teste** dos streams (teste de conectividade paralelo)
- 💾 **Geração de playlist M3U** com apenas streams funcionais
- 📊 **Relatório detalhado** da execução em JSON (`telegram_run_report.json`)
- 🤖 **Modo Telegram** com pesquisa por termo, janela temporal e modo manutenção
- 🌍 **Validação por país** (Portugal por defeito) com threshold de canais distintos
- 🌐 **Dashboard web** com gestão de listas de canais por país e diagnóstico de execuções
- 🧹 **Modo manutenção** que preserva os streams existentes quando não há novas descobertas
- ⚙️ **Configuração** via `wtelegram.config` e diretório `runtime-data`

## Instalação

### Pré-requisitos
- .NET 9.0 ou superior
- Windows, Linux ou macOS

### Compilar
```bash
git clone <repository-url>
cd m3uCrawler/m3uCrawler
dotnet restore
dotnet build
```

## Docker

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

### Docker Compose
```bash
docker compose build
docker compose run --rm -it m3ucrawler --telegram portugal --telegram-maintain --loop-hours 24 --max-streams 500
```

Notas Docker:
- Todos os dados de runtime (output, sessão Telegram e config opcional) ficam em `m3uCrawler/runtime-data`.
- Se quiseres usar `wtelegram.config`, coloca o ficheiro em `m3uCrawler/runtime-data/wtelegram.config`.

## Como usar

### Execução rápida
```powershell
# Windows (PowerShell)
dotnet run -- --telegram "iptv portugal"

# Modo manutenção (ciclo contínuo)
dotnet run -- --telegram portugal --telegram-maintain --loop-hours 24 --max-streams 500 --history-hours 72
```

### Modo interactivo
```bash
dotnet run
# Digite o termo de pesquisa quando solicitado
```

## Argumentos da linha de comando

As opções abaixo são as efectivamente reconhecidas pelo `Program.cs`. Opções fora desta lista não existem.

| Opção | Descrição |
|---|---|
| `--telegram` | Activa o modo de pesquisa via Telegram (com termo opcional seguido). |
| `--telegram-maintain` | Activa o ciclo de manutenção (ver secção "Modo manutenção"). |
| `--history-hours N` | Janela temporal para a pesquisa no Telegram (padrão: 48h). |
| `--max-streams N` | Limite de streams a testar por playlist. |
| `--country CODIGO` | Código do país alvo para validação (padrão: `pt`). |
| `--domain DOMINIO` | Filtra resultados para o domínio/subdomínio indicado. |
| `--loop-hours N` | Repete o ciclo de manutenção a cada N horas. |
| `--scan-domain DOMINIO` | Faz scan directo ao domínio (endpoints comuns de playlist). |
| `--user` / `--pass` | Credenciais para autenticação no scan de domínio. |
| `--web` | Inicia o dashboard web. |
| `--web-port PORTA` | Porta do dashboard (padrão: 5000). |
| `--web-token TOKEN` | Token partilhado para proteger o dashboard (ver secção "Modelo de segurança do dashboard"). Opcional. |
| `--output-dir DIR` | Directório de saída (padrão: `output`). |
| `--bot` | Modo bot Telegram. |
| `--fast` / `--high-performance` | Aumenta a concorrência (modo de pesquisa web). |
| `--help` / `-h` | Mostra ajuda. |

## Descoberta no Telegram (independente de keyword)

A descoberta de candidatos a playlist **não depende** da presença de uma palavra-chave no texto nem no nome de ficheiro. O sistema considera como candidato:

- URLs `.m3u` em qualquer parte do texto;
- URLs `.m3u8` em qualquer parte do texto;
- Anexos cujo nome de ficheiro termina em `.m3u` ou `.m3u8`;
- Conteúdo de anexos cujo corpo começa por `#EXTM3U` (mesmo sem extensão `.m3u`);
- URLs `http(s)` sem extensão `.m3u`/`.m3u8` quando o caminho/query contém uma indicação plausível de playlist (`playlist`, `m3u`, `iptv`, `list`, `xtream`, `channel`, `canal`, `live`, `getplaylist`). Estes ficam marcados com `RequiresContentVerification` e só avançam se o conteúdo HTTP for efectivamente `#EXTM3U`.

A keyword (quando fornecida) é usada apenas como **informação/match reporting** e não como filtro obrigatório.

Responsáveis: `m3uCrawler/Services/M3uCandidateDetector.cs` (detecção) e `m3uCrawler/Models/CandidatePlaylist.cs` (modelo do candidato).

### Suporte a Xtream Codes

O pipeline reconhece candidatos Xtream Codes sem depender de keyword:

- **URL de servidor Xtream** do tipo `http://host:port/live/USER/PASS/ID.ext` — `M3uCandidateDetector.IsXtreamServerUrl` detecta e `ResolveXtreamPlaylistUrl` resolve para a URL da playlist correspondente (`http://host:port/get.php?username=USER&password=PASS&type=m3u_plus`). O candidato entra no pipeline com essa URL de playlist (apenas um download válido em vez de tentar ler um segmento de stream como playlist).
- **URL de playlist Xtream** do tipo `http://host/get.php?type=m3u_plus` (ou `type=m3u`) — `IsXtreamPlaylistUrl` reconhece directamente e cria um candidato `RequiresContentVerification`.

Estes candidatos são tratados exactamente como os outros: passam pelo mesmo gate de verificação de conteúdo (`#EXTM3U`), validação por país, extracção e teste de streams — **não há uma segunda pipeline paralela**. A descoberta permanece independente de keyword.

#### Sanitização de credenciais

A URL interna do candidato Xtream contém credenciais (necessárias para o download HTTP). O projecto distingue explicitamente entre **artefactos funcionais** e **artefactos de diagnóstico** para não quebrar a reprodução Xtream nem expor credenciais:

- **Artefactos funcionais** (URLs reais, necessárias para reprodução):
  - `output/playlist.m3u`, `output/playlist_temp.m3u`, `output/telegram_playlist_<timestamp>.m3u` — a playlist M3U contém as URLs reais (`http://host/live/USER/PASS/ID.ts`) porque sem creds os streams não reproduzem.
  - Download via `GET /api/playlist` e `GET /api/playlist_temp` — devolvem a playlist funcional.

- **Artefactos de diagnóstico** (URLs sanitizadas, nunca expõem creds):
  - Consola — todos os `Console.WriteLine` que tocam em URLs (`M3uTesterService`, `Program.cs` para listagem de streams funcionais e templates de scan-domain) usam `CredentialSanitizer.SanitizeUrl`.
  - `output/telegram_run_report.json` (`RunReport`) — `DiscoveredPlaylists.Name` e `RejectionReasons` passam por `CredentialSanitizer.SanitizeUrl`.
  - `output/telegram_report_<timestamp>.json`, `output/telegram_maintain_report.json`, `output/report_<timestamp>.json` (relatórios JSON de playlist) — os URLs dos streams são sanitizados via `CredentialSanitizer.SanitizeUrl` antes da serialização.
  - Dashboard — a pré-visualização HTML (`<pre id='playlistPreview'>`) usa `GET /api/playlist/preview` (sanitizado via `CredentialSanitizer.SanitizeM3uContent`); os endpoints `/api/playlist*` mantêm-se funcionais para download explícito.
  - Mensagens de erro — sanitizadas via `CredentialSanitizer.SanitizeUrl` (download de playlist e templates).

`CredentialSanitizer` mascara: `user:password@` em userinfo → `user:***@`; segmentos `user/pass` em `/live/`, `/movie/`, `/series/` → `***/***`; parâmetros `username`, `password` e `token` em query string → `***`. É aplicado em todas as combinações (userinfo + path + query).

A URL raw é mantida em memória apenas durante a execução (para `DownloadPlaylistContentAsync` e para o tester); nunca é persistida em logs nem em ficheiros de diagnóstico.

Responsável: `m3uCrawler/Services/CredentialSanitizer.cs` (com `SanitizeUrl` e `SanitizeM3uContent`), integrado em `TelegramScraperService.Display`, `M3uTesterService`, `PlaylistManagerService.SaveToJsonReport`, `WebDashboardService` (endpoints de preview) e `Program.cs`.

## Pipeline de processamento

Fluxo real do modo Telegram:

```
Telegram messages
   ↓
M3uCandidateDetector.DetectFromMessage
   ↓
CandidatePlaylist
   ↓
DownloadPlaylistContentAsync (URL) / DownloadTelegramDocumentTextAsync (anexo)
   ↓
[M3U detection] — se RequiresContentVerification, o conteúdo HTTP tem de começar por #EXTM3U
   ↓
M3uParserService.Parse
   ↓
CountryChannelValidator.AnalyzePlaylist(content, countryCode, threshold: 3)
   ↓
   ├─ País alvo (≥3 canais distintos) → M3uTesterService.TestM3u8Stream
   └─ País não corresponde / playlist inválida → rejeitada, sem testar streams
   ↓
RunReport (métricas + motivos de rejeição)
   ↓
ImportHistoryService.RecordImportAsync
   ↓
PlaylistManagerService.SaveToM3uPlaylist / SaveToJsonReport
   ↓
output/telegram_run_report.json  +  output/playlist*.m3u
```

Método principal: `TelegramScraperService.SearchAndTestM3UInTelegramAsync`.
Os dados são preservados em `TelegramScraperService.LastRunReport` durante a execução e persistidos pelo `Program.cs` em `output/telegram_run_report.json`.

## Validação por país (`CountryChannelValidator`)

A validação do pipeline é feita por `CountryChannelValidator.AnalyzePlaylist(content, countryCode, threshold: 3)`. A classificação:

- **NÃO** depende do filename, caption, título do chat, nem de `group-title="Portugal"`.
- Depende exclusivamente dos **títulos dos canais extraídos dos `#EXTINF`** (via `M3uParserService.Parse`), com:
  - normalização (`NormalizeText`: separa por `-`, `_`, `.`, `/`, espaços);
  - tokenização (`Tokenize`) e correspondência por subconjunto de tokens (sem `Contains` sobre o conteúdo bruto);
  - agrupamento em **famílias canónicas** (`CanonicalChannelKey`: letras/dígitos minúsculos sem separadores), de modo a que `RTP1` e `RTP 1` contam como uma única família;
  - threshold de **3 canais distintos reconhecidos** para classificar a playlist como pertencente ao país.
- Protecção contra falsos positivos de aliases curtos: como o matching é por tokens do título e não por substring do conteúdo, "SIC" não corresponde a "basics" e "TVI" não corresponde a "atvinew".

### APIs preservadas (legacy)

`CountryChannelValidator.ValidatePlaylist` permanece como API pública para retrocompatibilidade (utilizada pelos testes baseline e por `WebDashboardService` quando aplicável). `CountryChannelValidator.ValidateStreams` é a API usada pelo pipeline principal **desde 2026-08-30** para o gate per-stream.

O pipeline aplica dois níveis de validação por país:

1. **`AnalyzePlaylist`** como rejeição rápida (`fast-reject`) — verifica se a playlist contém indicadores fortes do país alvo (≥3 aliases canónicos distintos). Playlists manifestamente de outro país são descartadas sem custos adicionais.
2. **`ValidateStreams`** como aprovação final por stream — depois do parse, cada stream é validado individualmente pelo título (e em fallback pelo `group-title`). Apenas os streams aceites chegam a `TestStreamsAsync`. Streams rejeitados nunca tocam a rede.

Recomendação: código novo que precise de uma decisão de país por playlist deve usar `AnalyzePlaylist`; decisões por stream devem usar `ValidateStreams`.

### Gestão de listas por país

A gestão dos ficheiros de aliases por país é feita por `CountryChannelListService` (`runtime-data/countries/<code>.json`), exposta no dashboard em `/api/countries`, `/api/country`, `/api/country/validate` e `/api/country/save`. A API deste serviço é preservada.

## Parser M3U (`M3uParserService`)

`M3uParserService.Parse(string content)` devolve `List<M3uStream>` e preserva:

- `OriginalExtInf` — a linha `#EXTINF` completa (essencial para reproduzir exactamente a playlist);
- `Title` — texto após a última vírgula na linha `#EXTINF` (ou `tvg-name` em variantes HLS);
- `Group` — atributo `group-title`;
- `Logo` — atributo `tvg-logo`;
- `Url` — linha http/https imediatamente a seguir ao `#EXTINF`.

Suporta:

- `#EXTM3U` (cabeçalho);
- Pares `#EXTINF` + URL (http e https);
- Extensões `.m3u` e `.m3u8` (a playlist é apenas texto; o parser não distingue);
- Playlists master HLS (`#EXT-X-STREAM-INF`): os atributos da variante são preservados (título via `tvg-name`, grupo, logo) mas `OriginalExtInf` fica vazio, permitindo distinguir uma playlist de canais de uma master HLS.

O parsing está **centralizado** em `M3uParserService`. O antigo `ExtractM3u8FromTelegramDocumentAsync` foi removido para evitar duplicação.

## `CandidatePlaylist` / `M3uCandidateDetector`

- `CandidatePlaylist` (`Models/CandidatePlaylist.cs`) representa um candidato: `Id`, `Kind` (`Url`/`Attachment`/`Inline`), `Source` (chat/título), `Url?`, `FileName?`, `SourceText?` (caption), `Content?` (corpo já descarregado), `DetectedFrom` (origem da detecção) e `RequiresContentVerification` (verdadeiro para URLs sem extensão `.m3u`/`.m3u8` que precisam de confirmação de conteúdo).
- `M3uCandidateDetector` (`Services/M3uCandidateDetector.cs`) é a lógica pura e testável que produz candidatos a partir de `(text, filename, attachmentContent?)`. Expõe `IsM3uUrl`, `IsM3uFilename`, `LooksLikePlaylistContent` e `IsPlausiblePlaylistUrl`.

Porque é que URLs sem extensão **não** são todas descarregadas indiscriminadamente: o método `IsPlausiblePlaylistUrl` só considera plausível um URL sem extensão quando o caminho/query contém uma das dicas de playlist (`playlist`, `m3u`, `iptv`, `list`, `xtream`, `channel`, `canal`, `live`, `getplaylist`). A descoberta mantém-se eficiente e não classifica URLs HTTP arbitrários como playlist apenas por serem HTTP.

## `RunReport` e `telegram_run_report.json`

Cada execução preenche um `RunReport` (em `Models/RunReport.cs`) com os seguintes contadores:

| Campo | Significado |
|---|---|
| `StartedAt` / `FinishedAt` / `DurationMs` | Tempos da execução. |
| `Status` | `pending` / `running` / `completed`. |
| `MessagesAnalyzed` | Mensagens do Telegram dentro da janela `--history-hours`. |
| `CandidatesFound` | Candidatos a playlist detectados. |
| `PlaylistsDownloaded` | Playlists cujo conteúdo foi obtido com sucesso. |
| `PlaylistsInvalid` | Playlists indisponíveis, vazias ou cujo conteúdo não é `#EXTM3U` (para candidatos "inspect"). |
| `CountryMatches` | Playlists classificadas como país alvo. |
| `PlaylistsRejected` | Playlists rejeitadas por não atingirem o threshold de país. |
| `ChannelsRecognized` | Soma dos canais distintos reconhecidos nas playlists aceites. |
| `StreamsExtracted` | Streams extraídos do `M3uParserService`. |
| `StreamsTested` | Streams enviados para `M3uTesterService`. |
| `StreamsWorking` | Streams funcionais. |
| `StreamsFailed` | Streams que falharam o teste. |
| `RejectionReasons` | Lista de motivos (ex.: "país PT não corresponde (canais 0/3)"). |
| `DiscoveredPlaylists` | Resumo por playlist (origem, nome, país, canais, streams, funcionais, estado). |

O relatório é gravado em **`output/telegram_run_report.json`** (UTF-8, `JsonNamingPolicy.CamelCase`) em ambos os modos `--telegram` e `--telegram-maintain` (helper `SaveRunReportAsync` em `Program.cs`).

## Modo manutenção (`--telegram-maintain`)

O ciclo de manutenção (`Program.cs` → `RunTelegramMaintenanceCycle`):

1. Limpa `output/playlist_temp.m3u` no início do ciclo.
2. Corre o pipeline (`SearchAndTestM3UInTelegramAsync`) e obtém os novos streams funcionais (`freshStreams`).
3. Carrega `output/playlist.m3u` existente e **retesta** cada stream (`M3uTesterService`).
4. Faz o merge de `stillWorkingMain` (retestados e ainda funcionais) com `freshStreams` via `TelegramScraperService.MergeStreams` (deduplicação por URL; prioriza working).
5. Escreve o resultado em `output/playlist.m3u` e o relatório de manutenção em `output/telegram_maintain_report.json`.
6. Actualiza o histórico (`ImportHistoryService.RecordImportAsync`).
7. Grava o `RunReport` em `output/telegram_run_report.json`.

Regras importantes:

- **Sem novos candidatos** (`CandidatesFound == 0`) → `MergeStreams(stillWorkingMain, [])` devolve `stillWorkingMain`, pelo que **`playlist.m3u` mantém os streams anteriores**. A ausência de novas descobertas **não** é interpretada como ausência de streams válidos.
- **Candidatos sem playlists válidas** (todas rejeitadas por país/ formato) → o mesmo: streams existentes preservados; `playlist_temp.m3u` fica praticamente vazia.
- **Novos streams** → adicionados, dedup por URL, sem duplicar.
- `playlist.m3u` e `playlist_temp.m3u` são sempre produzidos no fim do ciclo.
- **Não existe nenhum caminho** no ciclo em que uma execução vazia apague uma playlist funcional existente: a única escrita em `mainPath` recebe `finalStreams`, que inclui sempre `stillWorkingMain`.

## Dashboard (`WebDashboardService`)

O dashboard (`Services/WebDashboardService.cs`, `HttpListener`) serve a UI em `http://localhost:5000/` e expõe:

| Endpoint | Descrição |
|---|---|
| `/` | UI HTML com gestão de países, diagnóstico e playlists descobertas. |
| `/api/history` | Histórico recente (72h) de importações. |
| `/api/countries` | Lista de países configurados. |
| `/api/country?country=pt` | Detalhe de um país (canais). |
| `/api/country/validate?country=pt` | **Validação de `output/playlist.m3u` usando `AnalyzePlaylist` com threshold 3** (alinhada com o pipeline). Devolve `isMatch` (= `IsTargetCountry`), `matchedAliases`, `recognizedChannelCount`, `threshold`, `totalChannels`, `playlistLength`, `sample`. |
| `/api/country/save` (POST) | Grava a lista de canais de um país. |
| `/api/playlist` / `/api/playlist_temp` | **Funcional**: conteúdo textual das playlists com URLs reais (necessário para reprodução Xtream). Usar para download explícito. |
| `/api/playlist/preview` / `/api/playlist_temp/preview` | **Diagnóstico**: mesmo conteúdo com URLs sanitizadas (`CredentialSanitizer.SanitizeM3uContent`). Usado pela pré-visualização HTML para nunca expor credenciais. |
| `/api/run-report` | `RunReport` da última execução (sanitizado). |
| `/api/discovered-playlists` | Lista de playlists descobertas na última execução (sanitizado). |

A UI mostra:

- **Diagnóstico da última execução**: última execução, estado, mensagens, candidatos, playlists, playlists do país, streams encontrados/testados/funcionais/falhados, duração.
- **Últimas playlists descobertas**: origem, nome, país detectado, canais reconhecidos, número de streams, streams funcionais, estado.

### Modelo de segurança do dashboard

O dashboard é servido por um `HttpListener` que, por defeito, escuta em **todas as interfaces** (`http://+:<porta>/`) sem autenticação. Isto significa que, se o porto for exposto na rede (LAN, Docker com `ports: ["5000:5000"]`, IP público), qualquer pessoa com acesso à rede pode `GET /api/playlist` e obter a playlist M3U funcional com **URLs Xtream reais** (contendo `USER/PASSWORD`).

Para deployments em rede não confiável, **recomenda-se vivamente** proteger o dashboard com um token partilhado usando `--web-token <TOKEN>`:

- Quando o token está configurado, **todos os endpoints** (incluindo `/api/playlist` e `/api/playlist_temp` que servem a playlist Xtream funcional) exigem autenticação via:
  - Header: `Authorization: Bearer <TOKEN>`, ou
  - Query string: `?token=<TOKEN>`.
- A comparação é feita em **tempo constante** (`CryptographicOperations.FixedTimeEquals`) para evitar timing attacks.
- Pedidos sem credencial válida recebem `401 Unauthorized` com `WWW-Authenticate: Bearer`.
- **Sem token configurado**, o comportamento mantém-se aberto (compatibilidade com uso local).

A playlist M3U funcional (`/api/playlist`) continua a devolver as URLs Xtream reais — a protecção do token controla **quem** pode aceder, não **o quê**.

## Comportamento funcional

Os cenários abaixo descrevem o comportamento esperado e estão cobertos por testes unitários sempre que possível.

### Cenário positivo — descoberta sem keyword e playlist portuguesa

Uma mensagem Telegram contém um anexo `lista_2026.m3u` (ou URL) e a playlist tem:

```
#EXTM3U
#EXTINF:-1 tvg-name="RTP1" group-title="PT",RTP1
http://exemplo/rtp1
#EXTINF:-1 tvg-name="RTP2" group-title="PT",RTP2
http://exemplo/rtp2
#EXTINF:-1 tvg-name="SIC" group-title="PT",SIC
http://exemplo/sic
#EXTINF:-1 tvg-name="TVI" group-title="PT",TVI
http://exemplo/tvi
```

Condições:

- A mensagem **não** contém a palavra `Portugal` no caption nem no filename.

Resultado esperado:

1. A mensagem é analisada (`MessagesAnalyzed++`).
2. O anexo é detectado como candidato (filename `.m3u` e/ou conteúdo `#EXTM3U`).
3. O conteúdo é descarregado e validado pelo parser.
4. Os canais são extraídos dos títulos `#EXTINF`: `RTP1`, `RTP2`, `SIC`, `TVI`.
5. As famílias canónicas reconhecidas são: `rtp1`, `rtp2`, `sic`, `tvi` → 4 famílias distintas.
6. `RecognizedChannelCount (4) >= threshold (3)` → a playlist é classificada como Portugal.
7. Os streams são testados por `M3uTesterService` e os funcionais são adicionados à playlist de saída.
8. O `RunReport` regista `countryMatches=1`, `streamsTested>0`, `streamsWorking>0`.

### Cenário negativo — playlist estrangeira

Uma playlist estrangeira (ex.: apenas canais `La 1`, `Antena 3`, `Telecinco`) é descoberta para o país `pt`:

1. Os títulos não correspondem a nenhum alias do país.
2. `RecognizedChannelCount == 0 < threshold (3)`.
3. A playlist é **rejeitada** e o motivo é adicionado a `RejectionReasons`.
4. Os respectivos streams **não** são enviados para `M3uTesterService` (a rejeição ocorre antes do teste).

## Estado dos testes

- Build: `dotnet build m3uCrawler.sln --configuration Release` → **0 warnings, 0 errors**.
- Testes: `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo` → **977 testes, 977 passados, 0 falhados** (verificado em 2026-09-03 com `dotnet 9.0.317`).
- O runner descobre e executa todos os testes; não há testes que passem sem realmente exercitar o comportamento (detector, parser, validação por país com threshold/famílias/falsos-positivos, merge de manutenção).
- Não há teste de integração de rede (Telegram/HTTP); os testes são unitários e independentes de infra-estrutura externa.

## Arquitectura do projecto

```
m3uCrawler/
├── Models/
│   ├── M3uStream.cs                 # Modelo de stream M3U (URL, título, group, logo, OriginalExtInf).
│   ├── CandidatePlaylist.cs         # Candidato a playlist (URL/anexo, content, RequiresContentVerification).
│   ├── DiscoveredPlaylist.cs        # Resumo de uma playlist para o RunReport.
│   ├── RunReport.cs                 # Relatório detalhado de uma execução.
│   └── ImportHistoryEntry.cs        # Entrada de histórico (inclui métricas de discovery).
├── Services/
│   ├── M3uCrawlerService.cs         # Pesquisa na web (HTML).
│   ├── M3uTesterService.cs          # Teste de streams M3U8.
│   ├── M3uParserService.cs          # Parser M3U centralizado (preserva EXTINF).
│   ├── M3uCandidateDetector.cs      # Descoberta de candidatos (URL/anexo/conteúdo).
│   ├── PlaylistManagerService.cs    # Gestão e escrita de playlists M3U/JSON.
│   ├── ImportHistoryService.cs      # Persistência do histórico.
│   ├── TelegramScraperService.cs    # Pipeline Telegram (discovery→parser→país→teste).
│   ├── CountryChannelValidator.cs   # Validação por país (AnalyzePlaylist + legacy ValidatePlaylist/ValidateStreams).
│   ├── CountryChannelListService.cs # Gestão das listas de canais por país (preservada).
│   └── WebDashboardService.cs       # Dashboard web (HttpListener) e endpoints JSON.
├── Program.cs                       # Ponto de entrada, CLI, ciclo de manutenção, SaveRunReportAsync.
└── output/                          # Directório de saída (playlist*.m3u, telegram_run_report.json, etc.)
```

## Ficheiros gerados

- `output/playlist_temp.m3u` — Novos streams funcionais do ciclo (manutenção).
- `output/playlist.m3u` — Playlist consolidada após merge (manutenção).
- `output/telegram_run_report.json` — `RunReport` da última execução (camelCase).
- `output/telegram_playlist_<timestamp>.m3u` e `output/telegram_report_<timestamp>.json` — Saída de uma pesquisa `--telegram` ad-hoc.
- `output/telegram_maintain_report.json` — Relatório do ciclo de manutenção.
- `output/import_history.json` — Histórico persistente.

## Configuração

A autenticação WTelegram lê `wtelegram.config` (na pasta actual ou junto ao executável) com pares `chave=valor` (linhas iniciadas por `#` são comentários). Coloca-se normalmente em `m3uCrawler/runtime-data/wtelegram.config`. Os valores `ask` indicam que o campo é pedido interactivamente (ex.: `verification_code=ask`).

### Listas de canais por país

- `runtime-data/countries/<code>.json` — lista principal de aliases por país. Editável directamente ou via `WebDashboardService` (`/api/country/save`).
- `runtime-data/channel-indicators.json` — **lista suplementar específica de Portugal** (variantes regionais, desportos, sub-canais como `RTP1 HD`, `SportTV 5`, `TVI 24`, etc.). Carregada por `CountryChannelValidator.LoadSupplementaryIndicators` apenas quando `countryCode == "pt"`; para outros países é ignorada.
- A lista final de aliases é a **união case-insensitive** dos dois ficheiros (`CountryChannelValidator.LoadCountryAliases`). O ficheiro principal tem prioridade; os indicadores suplementares só adicionam entradas novas.
- O ficheiro é resolvido por ordem: (1) `runtime-data/channel-indicators.json` junto ao `rootDirectory` configurado; (2) irmão desse directório; (3) `runtime-data/channel-indicators.json` junto ao executável; (4) `runtime-data/channel-indicators.json` junto ao CWD. Em produção, com o bind mount do Compose, (1) é `/opt/m3ucrawler/runtime-data/channel-indicators.json` (se existir) → resolvido em `/data/channel-indicators.json` dentro do container.
- A presença deste ficheiro **não substitui** o `runtime-data/countries/<code>.json` — é puramente complementar. Em servidores onde não exista, o pipeline funciona apenas com a lista principal.

## Dependências

- `HtmlAgilityPack` — análise de HTML.
- `System.Text.Json` — serialização.
- `Telegram.Bot` — modo bot (`--bot`).
- `WTelegramClient` — autenticação e pesquisa Telegram.

## Aviso legal

Este software é destinado apenas para fins educacionais e de pesquisa. Assegure-se de que tem permissão para aceder aos streams, respeite os direitos autorais e os termos de serviço, e use apenas conteúdo legal e autorizado. O programador não se responsabiliza pelo uso indevido.

## Licença

MIT. Consulte o ficheiro `LICENSE`.

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

### Publicações HTML com cards Xtream

Uma mensagem Telegram pode conter um URL `http(s)` genérico (sem pista `playlist|m3u|iptv|list|xtream|channel|canal|live|getplaylist` no path/query) que aponta para uma página HTML com uma ou várias "cards" Xtream — listas visuais do tipo:

```text
Host: example.com:80
User: alice
Pass: secret1
M3U: http://example.com:80/get.php?username=alice&password=secret1&type=m3u_plus
EPG: http://example.com:80/xmltv.php?username=alice&password=secret1
```

O detector (`M3uCandidateDetector`) **não** trata URLs genéricas como playlist (para evitar downloads indiscriminados de páginas não relacionadas). A captura deste caso é feita exclusivamente em `TelegramScraperService.ExtractRemainingHttpUrls` que:

1. Extrai todas as URLs HTTP/HTTPS do texto da mensagem;
2. Descarta as que já foram capturadas pelo detector (m3u, xtream playlist, plausíveis);
3. Descarta URLs Xtream servidor (`/live/USER/PASS/...`) indirectamente consumidas pelo detector;
4. Promove as restantes a `CandidatePlaylist { DetectedFrom = "xtream publication url", RequiresContentVerification = true }`.

No `for` principal de `SearchAndTestM3UInTelegramAsync`, quando o conteúdo HTTP descarregado **não** começa por `#EXTM3U` mas **parece** HTML (`<!DOCTYPE` ou `<html`), o `XtreamPublicationResolver.ResolveFromHtml` é invocado. A estratégia é adaptativa — **não depende de cosmética específica de um publisher**.

#### Parser adaptativo (commits `e963a7a` → `4df856e`)

O parser funciona em duas camadas:

1. **Caminho DOM-based** (preservado): segmentação por estrutura HTML explícita — `<table>` (>=2 linhas de `<tr>`), `<div|section|article|li class='card'>`, ou `<hr>` no body. Cards dentro de cada segmento são parseadas com tolerância a capitalização (`Host`/`HOST`/`host`), espaços, HTML entities (`&amp;`, `&lt;`), `<a href>` (captura `href` em vez do texto do link) e labels alternativos (`Server`, `Username`, `Password`).

2. **Caminho flat-text fallback** (novo): se o caminho DOM não produz contas mas o `InnerText` tem ≥ 50 chars, o `ResolveFromFlatText` é invocado. Este caminho:
   - Strip de ruído cosmético: box-drawing (U+2500..U+25FF), símbolos (U+2600..U+27BF), math (U+2200..U+23FF), pares surrogate (U+D800..U+DFFF), ANSI escape codes, sequências longas de chars repetidos.
   - Transliteração de glyphs fancy para ASCII: modifier letters U+1D00..U+1D7F (small caps `ᴜ`, `ᴇ`, `ᴄ`, …), IPA U+0260..U+029F (`ɢ`, `ɪ`, `ɴ`, …), Latin-1 supplement U+00C0..U+00FF (`á`, `ç`, `ñ`, …), mathematical double-struck digits U+1D7D0..U+1D7D9. Aplica-se em pares surrogate (mathematical `3` é codepoint U+1D7D3).
   - Anchor detection via regex tolerante: labels conhecidos (`host`, `user`, `pass`, `m3u`, `epg`, `expires`, `port`, `server`, `playlist`, etc.) seguidos de separador (`:`, `=`, `→`, `➢`, `|`, `-` ou apenas whitespace) + valor.
   - **Clustering com boundary explícito em `host`**: cada `host` repetido FECHA o cluster anterior e ABRE um novo. Isto resolve a fusão de cards adjacentes mesmo quando estão dentro da janela de proximidade (1000 chars). Janela conservadora calibrada para o caso iptvgold onde `Host` fica no topo e `M3U`/`EPG` no fundo (≈700-800 chars de distância).
   - Validação de host: `LooksLikeValidHost` rejeita texto livre sem `.` ou TLD — protege contra falsos positivos em texto que mencione `Host:`/`User:`/`Pass:` sem ser uma publicação real.
   - Apenas são produzidas contas com **Host + User + Pass** presentes. Cards incompletas são descartadas silenciosamente.
   - Deduplicação determinística por **identidade lógica = `scheme://host:port/username`** (normalizado, case-insensitive em scheme/host; password **nunca** participa). Múltiplas contas no mesmo servidor com usernames diferentes permanecem como fontes independentes; contas repetidas colapsam para uma única.
   - Páginas HTML sem nenhuma card Xtream válida geram um único registo em `RunReport.RejectionReasons` (sanitizado), mas **não** incrementam `PlaylistsInvalid`.

**Validado em produção** (servidor `192.168.68.142`, container `m3ucrawler:sha-4df856e`, single-cycle `--history-hours 24`): descobriu 10 contas Xtream distintas do HTML da msg `110705` (canal `1635952193`, publisher `neorcqds.top:8080`), testou streams funcionais e integrou canais portugueses na `playlist.m3u`. Unitariamente, o fixture `m3uCrawler.Tests/Fixtures/iptvgold_07-09-2026.html` (msg `110658`, 70 contas Xtream em `iptvgold.online:8880`) é parseada correctamente num único `ResolveFromHtml` call.

#### Limitações conhecidas do parser adaptativo

- **Cards adjacentes que partilham labels** com metadata externa (e.g. `CHANNELS`/`MOVIES`/`SERIES` numa MEDIA LIST entre cards) podem fundir-se num cluster único. A heurística `host`-boundary mitiga mas não elimina o caso onde anchors de media list se misturam com labels do card no intervalo.
- **`Label -> Value`** (seta colada sem espaço) não suportado — separador requer pelo menos 1 espaço.
- Variantes FR/ES (`Serveur`/`Mot de passe`, `Servidor`/`Contraseña`) não estão no vocabulário actual; expansão é trivial quando houver procura real.

Cada conta válida é promovida a `CandidatePlaylist { Url = acc.M3uUrl ?? BuildXtreamPlaylistUrl(acc), DetectedFrom = "xtream publication", RequiresContentVerification = true }` e entra no **mesmo loop** do pipeline M3U/Xtream existente (`AnalyzePlaylist` → `Parse` → `ValidateStreams` → `TestStreamsAsync` → `RunReport`). **Não há uma segunda pipeline paralela**.

#### Múltiplas contas no mesmo servidor

Duas contas diferentes no mesmo servidor — e.g. `server.example:80` com `User=A` e `User=B` — permanecem como **duas fontes independentes** no pipeline. Esta é uma escolha deliberada para suportar redundância, fallback, health scoring e selecção da melhor fonte em iterações futuras. O modelo de identidade é **`endpoint + username`** (a password é um segredo operacional que pode mudar sem afectar a identidade da fonte).

#### Limitação conhecida: a "MEDIA LIST" não é fonte de canais

Algumas publicações apresentam listas resumidas como:

```text
MEDIA LIST
PORTUGAL
SIC
TVI
RTP
SPORT TV
…
```

Esta lista é **publicidade/resumo da conta**, não a lista real de canais. O resolver nunca a trata como fonte de canais: extrai apenas labels semânticos (`Host|Server|Endpoint`, `User|Username`, `Pass|Password`, `M3U`, `EPG`, `Expires`, `Max Connections`). A lista real de canais/vod/séries continua a vir da ingestão real (`get.php`/`m3u_plus`) através do pipeline M3U/Xtream. Proibido por invariante do projecto: nunca adicionar canais directamente a partir de texto HTML não verificado.

#### Construção da URL Xtream

`BuildXtreamPlaylistUrl` (em `TelegramScraperService`) **não** cria uma segunda implementação de `get.php`. Quando a card fornece uma URL M3U explícita, essa URL tem prioridade. Caso contrário, é sintetizada uma URL de servidor `http://host:port/live/USER/PASS/0.ts` e delegada a `M3uCandidateDetector.ResolveXtreamPlaylistUrl` — a única forma canónica de produzir `get.php?username=…&password=…&type=m3u_plus` no projecto. Existe uma outra construção independente em `M3uCrawlerService.ScanDomainForPlaylists` para o modo `--scan-domain`; é código pré-existente e **não** foi alterado por esta funcionalidade.

#### Anexos HTML (`m3u@host.html`) — segundo mecanismo

A mesma capacidade é exercida quando a mensagem Telegram traz um **anexo `.html` ou `.htm`** em vez de uma URL pública. O detector emite, sem I/O:

```csharp
CandidatePlaylist {
  Kind = Attachment,
  FileName = "m3u@host.example_07-09-2026.html",
  DetectedFrom = "html attachment",
  RequiresContentVerification = true,
  Content = null   // downloaded by ProcessAttachmentCandidatesAsync
}
```

A partir daqui o download, a gate `LooksLikeHtmlPublication`, a chamada ao `XtreamPublicationResolver`, a promoção a `CandidatePlaylist { DetectedFrom = "xtream publication" }` e o fan-out no pipeline M3U/Xtream são **idênticos** ao caso URL pública acima. **Não há um parser novo** — apenas uma fonte adicional para o mesmo `XtreamPublicationResolver`.

Detecções suportadas pelo `M3uCandidateDetector.IsHtmlFilename`:

| Filename | Detectado? |
|---|---|
| `m3u@host.example_07-09-2026.html` | sim |
| `m3u@host.example_07-09-2026.htm` | sim |
| `foo.HTML`, `foo.HTM`, `foo.HtMl`, `foo.HtM` | sim (case-insensitive) |
| `page.htmx`, `nothtml.txt`, `script.js` | não |
| `null` / `""` | não |

#### Referências `t.me/c/<channel>/<message>` — caminho Telegram

Deep links do Telegram (e.g. `https://t.me/c/1635952193/110637`) **são suportados**. O `TelegramPublicationDiscovery` identifica-os a partir do texto da mensagem e o `TelegramPublicationResolver` resolve-os via `WTelegram.Channels_GetMessages(inputChannel, [InputMessageID])` usando o `access_hash` cacheado a partir dos diálogos do `_client.Messages_GetAllDialogs()`.

Casos cobertos:

- Mensagem resolvida contém **texto com URL HTTP pública** → sub-publicação reportada no `ChildPublications`, tratada pelo pipeline de URL existente.
- Mensagem resolvida contém **attachment HTML `.html`/`.htm`** com cards Xtream → aplica-se o `XtreamPublicationResolver.ResolveFromHtml`, devolvendo 0..N `XtreamAccountInfo`. Cada conta é promovida a `CandidatePlaylist { DetectedFrom="xtream publication" }` que re-entra no loop principal do pipeline M3U/Xtream.
- Mensagem resolvida contém **attachment M3U** → o conteúdo é entregue como `Content` no `CandidatePlaylist` (sem mudança adicional).
- Mensagem contém **outras referências Telegram** (`https://t.me/c/...`) → recursão controlada com depth-limit (`MaxResolutionDepth = 3`) e seen-set.
- Mensagem contém **attachment desconhecido** (e.g. `.pdf`) → `PublicationState.RequiresReview`.
- Mensagem **sem nada útil** → `PublicationState.Unsupported`.
- Mensagem **inacessível** (canal inexistente, FLOOD_WAIT persistente, `CHANNEL_INVALID`) → `PublicationState.ResolutionFailed` com `Reason` sanitizado. **A mensagem não desaparece silenciosamente**: é registada no `RunReport.PublicationsTriageLog`.

Telegram publication URLs (`https://t.me/<username>/<message>`) sem canal id explícito **não** são suportadas nesta iteração — apenas o formato `t.me/c/<channel>/<message>`. Esta decisão evita resolver usernames via `Messages_ResolveUsername` (que adiciona uma chamada API e mais um ponto de falha).


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
   ├─ m3u/m3u8 URL ou filename → CandidatePlaylist M3U
   ├─ Xtream URL → CandidatePlaylist Xtream (resolve para get.php)
   ├─ "url (inspect)" → CandidatePlaylist com RequiresContentVerification
   ├─ .html / .htm attachment → CandidatePlaylist { DetectedFrom="html attachment",
   │                                                    RequiresContentVerification=true }
   ↓
TelegramScraperService.ExtractRemainingHttpUrls
(URLs HTTP genéricas -> candidatas a publicação HTML)
   ↓
TelegramPublicationDiscovery.DiscoverFromText
   ├─ https://t.me/c/<channel>/<message> → TelegramPublicationRef
   │       (referencia Telegram; resolvida via WTelegram Channels_GetMessages)
   └─ outras URLs HTTP → TelegramPublicationRef (processadas como publicacao URL)
   ↓
TelegramPublicationResolver.ResolveAsync
   - dado um TelegramMessageFetcher injetado (testavel)
   - aplica depth-limit (MaxResolutionDepth=3) e seen-set para evitar ciclos
   - classifica resultado: Resolved / ResolutionFailed / RequiresReview / Unsupported
   - para mensagens com attachment HTML: aplica XtreamPublicationResolver.ResolveFromHtml
   - cada conta Xtream descoberta -> promoted a CandidatePlaylist (mesmo fan-out)
   ↓
CandidatePlaylist
   ↓
DownloadPlaylistContentAsync (URL) / DownloadTelegramDocumentTextAsync (anexo)
   ↓
[Gate #EXTM3U / HTML]
   ├─ começa por #EXTM3U → pipeline M3U/Xtream (AnalyzePlaylist → Parse → ValidateStreams → TestStreams)
   ├─ parece HTML (<!DOCTYPE / <html) → XtreamPublicationResolver.ResolveFromHtml
   │      ↓
   │   N XtreamAccountCandidate (uma por card: endpoint + user; password fora da identidade)
   │      ↓
   │   cada conta → CandidatePlaylist (DetectedFrom = "xtream publication")
   │      ↓
   │   re-entra no mesmo pipeline M3U/Xtream acima
   └─ outro conteúdo → rejeitado (conteúdo não é playlist M3U)
   ↓
CountryChannelValidator.AnalyzePlaylist(content, countryCode, threshold: 3)
   ↓
   ├─ País alvo (≥3 canais distintos) → M3uTesterService.TestM3u8Stream
   └─ País não corresponde / playlist inválida → rejeitada, sem testar streams
   ↓
RunReport (métricas + motivos de rejeição + triage de publicações)
   ↓
ImportHistoryService.RecordImportAsync
   ↓
PlaylistManagerService.SaveToM3uPlaylist / SaveToJsonReport
   ↓
output/telegram_run_report.json  +  output/playlist*.m3u
```

### Camadas de descoberta e resolução (introduzido 2026-09-09)

A nova capacidade introduz três camadas distintas com responsabilidades separadas:

1. **TelegramPublicationDiscovery** — parsing puro (sem I/O). Identifica:
   - `https://t.me/c/<channel_id>/<message_id>` (referência Telegram com (channel, message) resolvíveis via WTelegram).
   - Outras URLs HTTP genéricas no texto (mecanismo já existente).
2. **TelegramPublicationResolver** — recebe uma lista de `TelegramPublicationRef` e um `TelegramMessageFetcher` (delegate injectado, testável sem WTelegram). Para cada referência:
   - Resolve a mensagem (texto + attachment).
   - Classifica resultado (`Resolved`, `ResolutionFailed`, `RequiresReview`, `Unsupported`).
   - Para attachment HTML, aplica `XtreamPublicationResolver.ResolveFromHtml` e devolve `IReadOnlyList<XtreamAccountInfo>`.
   - Para mensagens com sub-referências no texto, recursa com depth-limit (`MaxResolutionDepth=3`) e seen-set.
3. **TelegramPublicationReference** no `TelegramScraperService` — orquestrador: integra as duas camadas no loop principal, converte contas Xtream em `CandidatePlaylist { DetectedFrom="xtream publication" }` que entram no pipeline M3U/Xtream existente (zero duplicação).

Estados de triagem expostos no `RunReport`:

- `PublicationsDiscovered` — total de referências + URLs HTTP captadas.
- `PublicationsResolved` — cujo conteúdo foi obtido com sucesso.
- `PublicationsResolutionFailed` — canal inexistente, mensagem inacessível, FLOOD_WAIT persistente.
- `PublicationsRequiresReview` — HTML sem cards Xtream / attachment desconhecido.
- `PublicationsUnsupported` — texto sem URLs nem anexos úteis.
- `XtreamAccountsDiscovered` / `XtreamAccountsAfterDedup` / `XtreamAccountsForwarded` — fan-out.

Cada entrada inclui `PublicationTriageEntry { Kind, Reference, ChannelId, MessageId, State, Reason, XtreamAccountsFound }`. `Reference` é sempre URL público (t.me/c/...) ou URL de página HTTP sem credenciais. `Reason` é sanitizado contra credenciais antes de ser persistido.

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

### Navegação do Dashboard

O dashboard tem os seguintes separadores principais:

- **Overview**: resumo do sistema com métricas da última execução, carteiras de streams e estado do Dispatcharr.
- **Execuções**: histórico detalhado das últimas 72h com métricas por execução.
- **Descoberta**: playlists descobertas com filtros por estado, origem e país.
- **Canais / Países**: validação da playlist actual por país e gestão das listas de aliases.
- **Playlist**: visualização da playlist actual com links para download funcional.
- **Dispatcharr**: estado da última sincronização e detalhes do plano/report.
- **Catálogo**: gestão completa do catálogo de canais (ver secção abaixo).
- **Diagnóstico**: inventário de ficheiros, RunReport completo e glossário de métricas.

### Catálogo de Canais

O catálogo (`ChannelCatalogDbContext`, SQLite em `/data/channel-catalog.db`) gere:

| Separador | Conteúdo |
|---|---|
| **Visão Geral** | Estatísticas agregadas do catálogo (canais, aliases, regras, pending approvals). |
| **Canais** | Lista de canais canónicos com DisplayName, Key, Categoria, Grupo editorial, Política de publicação, Activo, Aliases. |
| **Regras** | IdentityRules explícitas que sobrepõem o matching automático. Criar regra com `ReviewOnly` permite fuzzy matching futuro; `Excluded` bloqueia o canal permanentemente. |
| **Afinidades** | Grupos de afinidade (e.g. "TVI" com membros "tvi24", "tvi 24", "tvi noticias"). Os membros são injetados no `CountryChannelValidator` como aliases adicionais para country-level targeting. |
| **Reviews** | Itens de revisão do Dispatcharr (decisões ambíguas ou uncertainas pendentes de decisão humana). |
| **Sync Runs** | Histórico de sincronizações Dispatcharr com contadores de created/merged/protected/removed. |
| **Pending** | Canais que geraram dúvida no country-level targeting e aguardam decisão manual (ver secção seguinte). |

### Pending Country Approvals

Esta funcionalidade permite ao utilizador decidir manualmente sobre canais que geraram dúvida durante o country-level targeting.

**Quando surge um canal para aprovação manual?**

Quando um stream tem indicadores de país (e.g. "PT" no título ou group-title) mas:
- Não bate num canal canónico conhecido
- Não corresponde a nenhum grupo de afinidade
- O matching fuzzy também não encontra correspondência clara

**Motivos de dúvida:**
- `weak_country_match`: o canal tem indicação de país mas não bate em nada conhecido (e.g. "RTP Africa", "PT Sports Channel")
- `affinity_no_channel`: o canal corresponde a um grupo de afinidade mas o grupo não tem canal canónico associado

**Como funciona a aprovação manual:**

1. **Aprovar** → Cria uma `IdentityRule` com `ReviewOnly` que permite fuzzy matching futuro. O canal fica elegível para ser criado automaticamente em sincronizações futuras.
2. **Reprovar** → Cria uma `IdentityRule` com `Excluded` que impede o canal de ser aceite. Útil para descartar canais extranjeros que usam indicadores de país enganosos.

**Nota de segurança**: As URLs mostradas na lista de pending approvals são sanitizadas antes de guardar (`CredentialSanitizer.SanitizeUrl`), pelo que nunca expõem credenciais Xtream.

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
- Testes: `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo` → **1066 testes, 1066 passados, 0 falhados** (verificado em 2026-09-06 com `dotnet 9.0.317`).
- O runner descobre e executa todos os testes; não há testes que passem sem realmente exercitar o comportamento (detector, parser, validação por país com threshold/famílias/falsos-positivos, merge de manutenção).
- Não há teste de integração de rede (Telegram/HTTP); os testes são unitários e independentes de infra-estrutura externa.

## Arquitectura do projecto

```
m3uCrawler/
├── Models/
│   ├── M3uStream.cs                 # Modelo de stream M3U (URL, título, group, logo, OriginalExtInf).
│   ├── CandidatePlaylist.cs         # Candidato a playlist (URL/anexo, content, RequiresContentVerification).
│   ├── XtreamAccountInfo.cs         # DTO intermediário para uma conta Xtream descoberta via publicação HTML (sem serialização; password nunca na identidade).
│   ├── DiscoveredPlaylist.cs        # Resumo de uma playlist para o RunReport.
│   ├── RunReport.cs                 # Relatório detalhado de uma execução.
│   └── ImportHistoryEntry.cs        # Entrada de histórico (inclui métricas de discovery).
├── Services/
│   ├── M3uCrawlerService.cs         # Pesquisa na web (HTML).
│   ├── M3uTesterService.cs          # Teste de streams M3U8.
│   ├── M3uParserService.cs          # Parser M3U centralizado (preserva EXTINF).
│   ├── M3uCandidateDetector.cs      # Descoberta de candidatos (URL/anexo/conteúdo).
│   ├── XtreamPublicationResolver.cs # Resolver de publicações HTML com cards Xtream (sem I/O).
│   ├── PlaylistManagerService.cs    # Gestão e escrita de playlists M3U/JSON.
│   ├── ImportHistoryService.cs      # Persistência do histórico.
│   ├── TelegramScraperService.cs    # Pipeline Telegram (discovery→parser→país→teste) + orquestração de fan-out Xtream.
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

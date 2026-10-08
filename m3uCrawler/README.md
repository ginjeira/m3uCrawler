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
# CLI interactiva (sem --web): a autenticação Telegram é pedida no terminal
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

### Primeira execução (instalação nova)

- O processo pode arrancar com o **comando normal** de produção (por exemplo
  `--telegram … --web …`) mesmo sem `wtelegram.config`/`session.dat`. Nesse
  caso **não termina nem reinicia em loop**: regista um aviso, não corre o
  ciclo CLI Telegram e mantém o dashboard disponível para o Setup. As acções
  Telegram do scheduler ficam bloqueadas até à autenticação (gate por
  capacidade).
- A autenticação Telegram é uma **operação de aplicação**, feita no dashboard
  (`Setup → Telegram`): guardar `api_id`/`api_hash`/telefone, submeter o
  código de verificação e, se aplicável, a password 2FA. A sessão é
  persistida de forma segura.
- **Não é necessário reiniciar o processo**: depois de autenticado, as
  execuções agendadas (`telegramRun`/`telegramMaintainRun`) e o trigger
  manual usam o mesmo cliente Telegram autenticado.
- Não há passos manuais escondidos: numa instalação nova, o Setup do
  dashboard cria `wtelegram.config`/`session.dat`. Colocar previamente
  `m3uCrawler/runtime-data/wtelegram.config` continua a ser suportado.

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
| `--history-hours N` | Limite superior (Max) da janela temporal para a pesquisa no Telegram (padrão: 24h; máximo: 1440h). |
| `--min-history-hours N` | Limite inferior (em horas) da idade das mensagens (padrão: 0 = comportamento legacy). |
| `--max-streams N` | Limite de streams a testar por playlist. |
| `--country CODIGO` | Código do país alvo para validação (padrão: `pt`). |
| `--domain DOMINIO` | Filtra resultados para o domínio/subdomínio indicado. |
| `--loop-hours N` | Repete o ciclo de manutenção a cada N horas. |
| `--scan-domain DOMINIO` | Faz scan directo ao domínio (endpoints comuns de playlist). |
| `--user` / `--pass` | Credenciais para autenticação no scan de domínio. |
| `--web` | Inicia o dashboard web. |
| `--web-port PORTA` | Porta do dashboard (padrão: 5000). |
| `--web-token TOKEN` | Token partilhado para proteger o dashboard (ver secção "Modelo de segurança do dashboard"). Opcional. |
| `--web-allow-trigger` | Opt-in: permite `POST /api/run/start` (trigger manual) no dashboard. Default: desactivado ⇒ `503 web-allow-trigger-disabled`. |
| `--admin-reset-password USERNAME` | Recuperação **host-only** da password de administrador: a password é lida interactivamente do stdin, **nunca** de argv. Ver secção "Alterar/recuperar password de administrador". |
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

### Janela de histórico da pesquisa Telegram (Min/Max)

A pesquisa no Telegram considera apenas mensagens cuja idade satisfaz `MinHistoryHours <= idade da mensagem <= MaxHistoryHours`, onde `MaxHistoryHours` é o parâmetro/settings `HistoryHours` **já existente** e `MinHistoryHours` é o novo limite inferior (default `0`). Os dois limites são **inclusivos**: idade igual ao mínimo ou ao máximo é aceite.

- `--history-hours N` continua a definir o limite superior (Max), com o comportamento actual (`floor` 1; tecto 1440h = 60 dias).
- `--min-history-hours N` define o limite inferior (≥ 0). `MinHistoryHours = 0` equivale ao comportamento legacy (`0 <= idade <= Max`), sem excluir mensagens recentes.
- **Isolar faixas temporais do histórico** (útil para testes reproduzíveis de janelas): `--min-history-hours 384 --history-hours 720` considera apenas mensagens com idade entre 384h e 720h, sem reprocessar o histórico recente. Outros exemplos de faixas: 0→384, 720→1000.
- **Persistência:** mesma SSOT `runtime-data/app_settings.json`, secção `discovery` (`historyHours` + novo `minHistoryHours`). `GET /api/discovery/settings` devolve `minHistoryHours`; `POST /api/discovery/settings` aceita `minHistoryHours` opcional (semântica de patch). `POST /api/run/start` aceita agora `minHistoryHours` opcional (DC-9), validado em `[0, MaxValidHistoryHours]`; fora do intervalo é ignorado (herda o persistido). Sem overrides, o run usa a janela persistida.
- **Validação:** `MinHistoryHours >= 0`, `MaxHistoryHours >= 0` e `MinHistoryHours <= MaxHistoryHours`. Valores inválidos são rejeitados na API (400) e os valores inválidos persistidos são normalizados para o default (`Min → 0`) pelo mecanismo `Sanitize` existente.

A janela já está configurável na **UI do dashboard**, na vista Descoberta (card "Configuração de discovery predefinida"): `keyword`, `MinHistoryHours`, `HistoryHours`/Max e `MaxStreams`, persistidos via `GET/POST /api/discovery/settings` (ex.: Min 425h / Max 450h ⇒ `425h ≤ idade ≤ 450h`; "Min 0 = sem limite inferior"). Esta configuração é a **base/fallback**; cada **Scheduled Job** pode sobrepor-lhe parâmetros próprios (DC-9 — ver "Scheduler / Scheduled Jobs") e o **"Run now"** da Execução ao Vivo aceita overrides pontuais de modo/keyword/janela/`MaxStreams` (DC-1). Ver "Configuração no Dashboard: o que está exposto e o que não" na secção do Dashboard.

### Proveniência da mensagem de origem (candidate → playlist)

Cada `DiscoveredPlaylist` exposto em `output/telegram_run_report.json` (lista `discoveredPlaylists`) e em `GET /api/discovered-playlists` passa a incluir a proveniência da mensagem Telegram que originou o candidate:

- `candidateId` — identifica o `CandidatePlaylist` de origem;
- `messageId` — id da mensagem Telegram de origem;
- `messageDateUtc` — data/hora UTC dessa mensagem; o título do chat continua em `source` (para candidatos Xtream derivados, `source` permanece o URL público da publicação, comportamento inalterado).

A regra de correlação é:

```text
Run → Mensagem (id, data/hora UTC, chat) → Candidate → Resultado (state/workingStreams)
```

Permite reconstruir operacionalmente a cadeia completa sem novo artefacto, endpoint ou UI.

Limitações actuais (estado honesto):

- candidates que falham **antes** do parse (download falhado, conteúdo não-`#EXTM3U`, parse inválido) não geram `DiscoveredPlaylist` — existem apenas como contadores/`rejectionReasons`, sem linha de proveniência;
- candidates promovidos de referências `t.me/c/...` resolvidas pós-enumeração têm `messageId` (a mensagem referenciada) mas `messageDateUtc` nulo (a resolução não devolve a data);
- candidates fora do caminho de mensagens enumeradas (ex.: `--scan-domain`) têm proveniência nula;
- `telegram_run_report.json` é sobrescrito a cada run.

**Ligação à janela Min/Max** (ver secção anterior): correlacionar cada candidate com a data/hora da mensagem que o originou permite confirmar que os candidates de uma faixa vêm exactamente das mensagens dessa faixa, tornando os testes de janelas auditáveis e reproduzíveis. Nota operacional: como `telegram_run_report.json` é sobrescrito a cada execução, copiar o ficheiro após cada run para comparar faixas Min/Max lado-a-lado.

## Parsing M3U (contrato W3)

O parsing de playlists está centralizado em `m3uCrawler/Services/M3uParserService.cs`, com o contrato normativo em `docs/Reestructure/04-PLAYLIST-STREAM.md` §7. Todos os consumidores (pipeline Telegram, `PlaylistReader`, `PlaylistManagerService`) convergem neste contrato.

- **`ParseDetailed(string? content, CancellationToken ct = default)`** devolve `M3uParseResult` com `Streams`, `Status` (`Success`/`Partial`/`Failed`), `Diagnostics` e contadores (`ValidCount`, `MalformedCount`, `UnusableTargetCount`, `BenignMetadataCount`, `IgnoredCount`).
- **Input mínimo:** a primeira linha não vazia/não-BOM tem de ser `#EXTM3U` (case-insensitive). Sem cabeçalho → `Failed`, 0 streams e nenhuma entrada ingerida.
- **Entrada válida:** `#EXTINF` seguido do próximo conteúdo não vazio, um URL absoluto `http`/`https`. Extraem-se título, `group-title`, `tvg-logo`, `tvg-id` e `OriginalExtInf`.
- **Malformado:** URL `http(s)` sem `#EXTINF` precedente, `#EXTINF` sem URL (EOF ou substituído), ou entrada estruturalmente incompleta. É registado em `Diagnostics`, excluído de `Streams` e o parsing continua.
- **Alvo não utilizável:** entrada estruturalmente válida com esquema diferente de `http`/`https` (ex.: `rtmp://`). Não é malformada; é excluída e registada como `UnusableTarget`.
- **Metadados benignos:** linhas `#...` que não são `#EXTINF` (versão, comentários, `#EXTGRP`, `#EXTVLCOPT`, `#KODIPROP`). Não são streams, não geram malformados e não alteram o estado. As variantes HLS `#EXT-X-STREAM-INF` continuam a produzir stream com os seus atributos (metadados, `OriginalExtInf` vazio).
- **Status:** `Success` = ≥1 entrada válida e 0 malformadas/inutilizáveis; `Partial` = ≥1 válida e ≥1 malformada/inutilizável; `Failed` = 0 válidas ou falha de parsing. `Partial` nunca equivale a sucesso.
- **Segurança:** cada diagnóstico passa por `CredentialSanitizer` — passwords/tokens de URLs nunca aparecem em relatórios ou logs.
- **Limites:** `M3uParserOptions` (tamanho de documento, nº máximo de entradas, comprimento de campo, tempo máximo, cancelamento) são mecanismo. Os valores concretos continuam `PARAMETER_GAP` (`DG-04d`).
- `Parse(string?)` mantém-se como conveniência retro-compatível e devolve `ParseDetailed(...).Streams`.

## Fingerprint canónico de stream (W4)

O fingerprint de stream é calculado por `m3uCrawler/Services/Matching/StreamFingerprint.cs`, com o contrato normativo em `docs/Reestructure/04-PLAYLIST-STREAM.md` §4/§4.1 e DL-108. Versão inicial: **`sfp1`**.

- **Fórmula:** `hex minúsculo de SHA-256(UTF-8("sfp1\n" + canonicalUrl))`. Só o hash e a versão (`FingerprintVersion`) são persistidos; o URL canónico nunca é persistido.
- **Canonicalização:** apenas `http`/`https` absolutos são fingerprintáveis; `scheme`/`host` em minúsculas; ponto final do host removido; porta por omissão removida (80/443); fragmento removido; **userinfo nunca incluído**; `path` preservado case-sensitive sem descodificar percent-encoding (`%2F` ≠ `/`); `path` Xtream `/live|movie|series/<USER>/<PASS>/<ID>` preserva `<ID>` e mascara `<USER>`/`<PASS>` (ex.: `/live/***/***/<ID>`); query remove apenas `username`/`password`/`token`/`authorization`, preservando os restantes parâmetros pela ordem original.
- **Persistência:** `ChannelSource.Fingerprint` + `FingerprintVersion` (nullable, migração aditiva `20260919152739_AddChannelSourceStreamFingerprint`). Rows legacy ficam `null` (sem backfill).
- **Dedup intra-Source:** mesma Source + canal + fingerprint + versão consolidam na mesma row; Sources diferentes nunca são consolidadas. Múltiplas streams por `(canal, source)` continuam suportadas.
- **Selection:** o critério 6 de DL-101 usa o fingerprint persistido; para rows legacy sem fingerprint usa a URL normalizada como fallback.
- **Segurança:** password, token, `Authorization` e username de credencial nunca aparecem na representação canónica, no valor persistido, em logs, diagnósticos ou excepções.
- **Resolução de identidade na selecção (W4.1):** o `SourceSelectionStage` resolve por fingerprint canónico (versão actual) e só depois por URL sanitizada (rows legacy); `SourceId` permanece parte da identidade persistente; sem correspondência ⇒ `Unmatched`/pass-through. Cobre o caso fingerprint-equivalente com representação sanitizada diferente (casing do host, porta default, fragmento, credenciais em query). Ver `WaveW4p1SourceSelectionFingerprintResolutionTests`.
- **Testes:** `WaveW4StreamFingerprintTests` (golden vectors calculados independentemente) e `WaveW4ChannelSourceFingerprintTests` (dedup, migração aditiva, selection).

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
  - Dashboard (projecções JSON, defense-in-depth) — o boundary de saída aplica `CredentialSanitizer.SanitizeUrl` ao `streamUrl` de `GET /api/catalog/sources/{id}/streams` (`ChannelSourceToJson`), ao preview de ordering lists (`PlaylistCompositionToJson`), ao `streamUrl` de `/api/catalog/pending-country-approvals` e ao `baseUrl` de `/api/dispatcharr/config`. Cobre linhas legacy/direct-DB que tenham escapado a sanitização em persistência. Para o Dispatcharr, um `baseUrl` já mascarado reenviado pelo formulário **não** é re-persistido (evita gravar `***`).
  - Mensagens de erro — sanitizadas via `CredentialSanitizer.SanitizeUrl` (download de playlist e templates).

`CredentialSanitizer` mascara: `user:password@` em userinfo → `user:***@`; segmentos `user/pass` em `/live/`, `/movie/`, `/series/` → `***/***`; a forma **bare** de Xtream `scheme://host:port/<username>/<password>/<stream-id>` (sem o marcador `/live|movie|series/`) → `scheme://host:port/***/***/<stream-id>`, preservando scheme, host, porta explícita (e.g. `:8080`) e o stream id; parâmetros `username`, `password` e `token` em query string → `***`. O reconhecimento da forma bare é deliberadamente restritivo — exactamente 3 segmentos de path, os dois primeiros com ≥4 caracteres e o último um stream id numérico (extensão opcional) — pelo que URLs arbitrárias com 3 segmentos (e.g. `/path/to/playlist.m3u8`) não são afectadas. É aplicado em todas as combinações (userinfo + path + query).

A representação sanitizada é usada apenas para apresentação — nunca deve ser usada como identidade interna (ver `AGENTS.md` §2).

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

### Validação física por conta e deduplicação por run (W-DEDUP, 2026-10-01)

No caminho de descoberta Telegram, os streams aceites por `ValidateStreams` entram em `TestStreamsAsync` → `AccountGateCoordinator` → `AccountValidator.ValidateAccountAsync` → `M3uTesterService.TestStreamForAccountAsync` → `ProbeOnceAsync`. Aqui o GET físico é **deduplicado por run**:

- **ValidationKey = `sfp1`.** A chave é o fingerprint canónico `StreamFingerprint.TryComputeFingerprint(url)`; **nunca** uma URL sanitizada. Para `/live|movie|series/USER/PASS/ID`, `sfp1` mascara `USER`/`PASS` e preserva endpoint + `ID`, funcionando como chave (endpoint, canal).
- **Chave já `Working` não é re-testada.** Um `ValidationKey` conhecido `Working` neste run não volta a ser GET-testado por outra conta. As contas não são fundidas.
- **Probe obrigatório.** Cada conta elegível executa sempre ≥1 GET físico com as suas próprias credenciais: primeiro stream cuja chave já é `Working`; senão o primeiro com chave; senão o primeiro elegível (ordem de parse).
- **Falhas nunca são reutilizadas.** `FailedTerminal`/`FailedTransient`, transitórios, curto-circuitados ou vazios nunca dispensam um GET físico de outra conta.
- **Não fingerprintáveis / Xtream *bare*.** URLs não-http (`rtmp://`, `udp://`, …) são sempre testadas e nunca registadas. A forma bare `host:port/user/pass/id` não é mascarada por `sfp1` e por isso não é deduplicada (limitação conhecida).
- **Proveniência.** Um resultado reutilizado tem `ReusedKnownWorking = true` e projecta `LastTested = default`, pelo que `PipelineIngestionService` não cria observação histórica para ele; o canal entra na lista `working` e na playlist.
- **Contadores.** `StreamsTested` conta apenas validações físicas; os GETs evitados vão para o novo `StreamsSkippedAlreadyValidated` (também em `LiveRunCounts` e no dashboard). O invariante `StreamsTested == StreamsWorking + StreamsFailed` mantém-se; `DiscoveredPlaylist.WorkingStreams` inclui reutilizados.
- **Âmbito.** O registo (`ValidationKeyRegistry`) é em memória, por run, thread-safe e não persistido (sem schema/migration, nada em `runtime-data`). Concorrência, timeouts e retries não mudam; manutenção, `ScheduledValidationAction`, `/api/validation/test` e Dispatcharr não são afectados.

Detalhe normativo em `docs/Reestructure/08-VALIDATION.md §6`.

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

A gestão dos ficheiros de aliases por país é feita por `CountryChannelListService` (`runtime-data/countries/<code>.json`), exposta no dashboard em `/api/countries`, `/api/country` (GET/DELETE), `/api/country/validate` e `/api/country/save`. A API deste serviço é preservada.

**W6 — decisão de âmbito:** o país é **configuração/validação**, não uma entidade de domínio (não existe entidade `Country`). O separador **Canais / Países** permite criar/editar/eliminar a configuração (`POST /api/country/save`, `DELETE /api/country`) e validar a playlist actual; não é um CRUD de entidade. O `DeleteCountry(string code)` elimina `<code>.json` e devolve `bool` (`true` se existia). O `displayName` amigável é preservado no save da UI (não é sobrescrito por `code.toUpperCase()`).

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
| `MessagesAnalyzed` | Mensagens do Telegram dentro da janela de histórico (entre `MinHistoryHours` e `HistoryHours`). |
| `CandidatesFound` | Candidatos a playlist detectados. |
| `PlaylistsDownloaded` | Playlists cujo conteúdo foi obtido com sucesso. |
| `PlaylistsInvalid` | Playlists indisponíveis, vazias ou cujo conteúdo não é `#EXTM3U` (para candidatos "inspect"). |
| `CountryMatches` | Playlists classificadas como país alvo. |
| `PlaylistsRejected` | Playlists rejeitadas por não atingirem o threshold de país. |
| `ChannelsRecognized` | Soma dos canais distintos reconhecidos nas playlists aceites. |
| `StreamsExtracted` | Streams extraídos do `M3uParserService`. |
| `StreamsTested` | Validações **físicas** (GET) enviadas para `M3uTesterService`. W-DEDUP (2026-10-01): não conta streams reutilizados por `sfp1` já conhecido `Working`. |
| `StreamsSkippedAlreadyValidated` | GETs físicos evitados porque o mesmo `sfp1` já estava `Working` neste run (W-DEDUP, 2026-10-01). |
| `StreamsWorking` | Streams funcionais. |
| `StreamsFailed` | Streams que falharam o teste. |
| `RejectionReasons` | Lista de motivos (ex.: "país PT não corresponde (canais 0/3)"). |
| `DiscoveredPlaylists` | Resumo por playlist (origem, nome, país, canais, streams, funcionais, estado). |

O relatório é gravado em **`output/telegram_run_report.json`** (UTF-8, `JsonNamingPolicy.CamelCase`) em ambos os modos `--telegram` e `--telegram-maintain` (helper `SaveRunReportAsync` em `Program.cs`).

O contador `streamsSkippedAlreadyValidated` (W-DEDUP) aparece também no histórico de execuções (`GET /api/history`, campo homónimo em `ImportHistoryEntry`) e na UI do dashboard — Live Run (rótulo "Streams reutilizados (W-DEDUP)"), Overview (card + badge) e tabela de Execuções. Entradas de histórico anteriores à W-DASHBOARD mostram `—`.

## Modo manutenção (`--telegram-maintain`)

O ciclo de manutenção (`Program.cs` → `RunTelegramMaintenanceCycle`) delega a cauda no
`RunPublicationService` (o mesmo serviço do single-cycle):

1. Corre o pipeline (`SearchAndTestM3UInTelegramAsync`) e obtém os novos streams funcionais (`freshStreams`).
2. Carrega `output/playlist.m3u` existente e **retesta** cada stream (`M3uTesterService`).
3. Faz o merge de `stillWorkingMain` (retestados e ainda funcionais) com `freshStreams` via `TelegramScraperService.MergeStreams` (deduplicação por URL; prioriza working).
4. Passa o resultado a `RunPublicationService.PublishAsync` (com `PlaylistFileName="playlist.m3u"`): o serviço deduplica por URL, escreve o intermédio `output/playlist_temp.m3u` (estado normalizado/deduplicado, **antes** da selecção) e o canónico `output/playlist.m3u`, e grava o relatório de manutenção em `output/telegram_maintain_report.json`.
5. Actualiza o histórico (`ImportHistoryService.RecordImportAsync`).
6. Grava o `RunReport` em `output/telegram_run_report.json`.

Regras importantes:

- **Sem novos candidatos** (`CandidatesFound == 0`) → `MergeStreams(stillWorkingMain, [])` devolve `stillWorkingMain`, pelo que **`playlist.m3u` mantém os streams anteriores**. A ausência de novas descobertas **não** é interpretada como ausência de streams válidos.
- **Candidatos sem playlists válidas** (todas rejeitadas por país/ formato) → o mesmo: streams existentes preservados; `playlist_temp.m3u` reflecte o merge preservado (não fica vazia enquanto houver streams existentes).
- **Novos streams** → adicionados, dedup por URL, sem duplicar.
- `playlist.m3u` e `playlist_temp.m3u` são sempre produzidos no fim do ciclo, pelo `RunPublicationService` (dono único de ambos).
- **Não existe nenhum caminho** no ciclo em que uma execução vazia apague uma playlist funcional existente: a escrita canónica recebe `finalStreams`, que inclui sempre `stillWorkingMain`.

## Dashboard (`WebDashboardService`)

O dashboard (`Services/WebDashboardService.cs`, `HttpListener`) serve a UI em `http://localhost:5000/` e expõe:

| Endpoint | Descrição |
|---|---|
| `/` | UI HTML com gestão de países, diagnóstico e playlists descobertas. |
| `/api/history` | Histórico recente (72h) de importações. |
| `/api/countries` | Lista de países configurados. |
| `/api/country?country=pt` | Detalhe de um país (canais). |
| `/api/country/validate?country=pt` | **Validação de `output/playlist.m3u` usando `AnalyzePlaylist` com threshold 3** (alinhada com o pipeline). Devolve `isMatch` (= `IsTargetCountry`), `matchedAliases`, `recognizedChannelCount`, `threshold`, `totalChannels`, `playlistLength`, `sample`. |
| `/api/country/save` (POST) | Grava a lista de canais de um país (preserva o `displayName` enviado). |
| `/api/country?country=pt` (DELETE) | **W6** — Elimina a configuração do país (`runtime-data/countries/<code>.json`). `200` quando eliminado, `404` quando ausente. Não altera a playlist publicada. |
| `/api/playlist` / `/api/playlist_temp` | **Funcional**: conteúdo textual das playlists com URLs reais (necessário para reprodução Xtream). Usar para download explícito. |
| `/api/playlist/preview` / `/api/playlist_temp/preview` | **Diagnóstico**: mesmo conteúdo com URLs sanitizadas (`CredentialSanitizer.SanitizeM3uContent`). Usado pelas pré-visualizações HTML (`#playlistPreview` e `#playlistTempPreview`, DC-5e) para nunca expor credenciais. `playlist_temp.m3u` inexistente → `404` ("Playlist temporária não encontrada"). |
| `/api/run-report` | `RunReport` da última execução (sanitizado). |
| `/api/discovered-playlists` | Lista de playlists descobertas na última execução (sanitizado). |
| `/api/audit` (GET) | **W6a/DC-5b** — Registos de auditoria administrativa, read-only. Filtros opcionais `objectType`, `objectId` e `limit` (default 100, cap 1000); ordenados por `occurredAtUtc`/`id` desc. Textos e JSON (`beforeJson`/`afterJson`) já sanitizados (`CredentialSanitizer`); sem catálogo/serviço de auditoria → `503 {"error":"audit-unavailable"}`. Visualizador na UI em Catálogo → **Auditoria**. |
| `/api/classification-summary` (GET) | **DC-5d** — Sumário de classificação do último `MatchPlan` (`dispatcharr_plan_*.json`): contagens por `ChannelKind` (`classification`), `excludedCount`, amostra de exclusões (`title`/`group`/`kind`/`reason`) e metadados (`planGeneratedAtUtc`, `planSourcePlaylistPath`). Sem plano → `200 {"error":"Sem plano de classificação disponível."}` (estado "sem dados", não erro). Visualizado na UI em Dispatcharr → **Classificação (último MatchPlan)**. |
| `/api/catalog/source-selection-policies` (GET/POST) | Política **global** de selecção de fontes por canal (ver secção seguinte). |
| `/api/catalog/source-selection-policies/channels` (GET/POST) | Overrides **por canal** da política de selecção de fontes (identidade = chave canónica; ver secção seguinte). |
| `/api/catalog/source-selection-policies/channels/{key}` (GET/DELETE) | Lê/elimina o override do canal canónico `{key}`. |
| `/api/catalog/source-selection-policies/preview` (GET) | **Preview/dry-run read-only** da selecção de fontes: corre a mesma lógica da produção sobre o catálogo, sem publicar nem escrever. Ver secção seguinte. |
| `/api/catalog/channel-sources/{id}/observations?limit={n}` (GET) | **DC-5c** — Histórico de observações de um *channel-source* (`quality`, `epg`, `availability`, `responseTimeMs`, `observedAtUtc`), mais recentes primeiro. `limit` default 200. Visualizador na UI em Catálogo → **Sources** (acção **Observações** por linha). |
| `/api/catalog/channel-sources/{id}/observations` (POST) | **DC-5c** — Regista uma observação (`{ quality, epg, availability, responseTimeMs }`); enums (`StreamQuality`/`EpgState`/`AvailabilityState`) com parse tolerante (valor desconhecido → default sem falhar). Devolve `{ recorded: true }`. Usado pelo formulário da UI em Catálogo → **Sources**. |

### Semântica HTTP e erros

Os endpoints JSON devolvem erros no envelope `{ "error": "<código>" }`; as Review APIs (`/api/reviews`, `/api/review*`) usam `{ error, message, correlationId }`. Regras uniformes:

- **405 Method Not Allowed** para verbos não suportados numa rota existente. O header `Allow` reflecte os métodos realmente suportados (não fixa `GET, POST`) e o corpo é `{"error":"method-not-allowed"}`. Exemplos: `POST` em `/api/run/start`, `/api/country/save`, `/api/validation/test` e `/api/dispatcharr/test`; `GET` em `/api/configuration/readiness`; `GET, POST` em `/api/discovery/settings`, `/api/telegram/config` e `/api/dispatcharr/config`; `GET` em `/api/telegram/auth/status`; `POST` nas restantes sub-rotas `/api/telegram/auth/*`.
- **404 Not Found** apenas para rotas **inexistentes** (não para verbos errados em rotas existentes).
- **`/api/country`** aceita `GET` (detalhe) e `DELETE` (remoção); outros verbos → `405` com `Allow: GET, DELETE`. **`/api/playlist`**, **`/api/playlist_temp`** e as variantes **`/preview`** são **GET-only** (outros verbos → `405` com `Allow: GET`); a playlist funcional continua intocada.
- O recurso Review é exposto com o contrato canónico (`subject`, `reason`, `createdAt`/`updatedAt`/`resolvedAt`, `runId`) **e** com aliases legacy (`fingerprint`, `normalizedIdentity`, `reasonSignature`, `createdAtUtc`/`updatedAtUtc`/`resolvedAtUtc`). A **UI do separador Catálogo → Reviews já usa a nova API** (`GET /api/reviews`, `POST /api/review/{resolve|ignore|reopen}`) com **identidade por `id` numérico** (não pelo fingerprint) e **paginação** (`offset`/`limit`, cap 500); o filtro de estado mantém os activos por omissão e expõe o histórico via `?state=`. A rota legacy `/api/catalog/reviews` mantém-se inalterada no backend (DL-120/D5), apenas já não é consumida pela UI.
- **Transporte e erros no front-end.** Os handlers de mutação do dashboard (POST/PUT/PATCH/DELETE) usam um único helper `apiRequest(path, { method, body, signal })`: envolve `fetch` em `try/catch` (nunca propaga rejeições de rede), serializa o corpo objecto para JSON, lê a resposta em `r.text()` com parse tolerante (nunca `r.json()` num ramo de erro) e devolve `{ ok, status, json, error }` com a mensagem normalizada (`json.error` → texto → `HTTP <status>` → mensagem de rede). A apresentação é uniforme — `setStatus(...)` em elementos de estado inline e `alert('Erro: ' + res.error)` onde não existe nó dedicado. As leituras de GET/polling mantêm `safeFetchJson`.

### Navegação do Dashboard

O dashboard tem os seguintes separadores principais:

- **Visão Geral**: resumo do sistema com métricas da última execução, carteiras de streams, estado do Dispatcharr e **estado de publicação do catálogo** (DL-130). O card "Publicação do catálogo" consome `GET /api/publication/status` e apresenta os 4 estados (`Pendente` / `Em dia` / `Sem publicação anterior` / `Indisponível`) com badges `warn` / `ok` / `warn` / `muted`; o booleano `publicationPending` chega já calculado pelo backend e não é recalculado no frontend.
- **Execuções**: histórico detalhado das últimas 72h com métricas por execução.
- **Descoberta**: card "Configuração de discovery predefinida" (keyword, janela Min/Max, `MaxStreams`, via `/api/discovery/settings`) e playlists descobertas com filtros por estado, origem e país, incluindo colunas de proveniência MessageId / Data mensagem (UTC) / Candidato.
- **Canais / Países**: validação da playlist actual por país e gestão das listas de aliases.
- **Playlist**: visualização da playlist actual e da intermédia (`playlist_temp.m3u`) com pré-visualizações sanitizadas (`GET /api/playlist/preview` e `GET /api/playlist_temp/preview`, DC-5e), links para download funcional e lista dos ficheiros em `output/`.
- **Dispatcharr**: estado da última sincronização, card **Classificação (último MatchPlan)** (DC-5d, `GET /api/classification-summary`) e detalhes do plano/report.
- **Catálogo**: gestão completa do catálogo de canais, incluindo o separador **Scheduled Jobs** (jobs cron persistentes; ver secção "Scheduler / Scheduled Jobs").
- **Validação de Streams**: política operacional de teste de streams e dry-run de URLs.
- **Execução ao Vivo**: observabilidade do `RunCoordinator` (estado, fases, contadores, actividades e últimas execuções 24h). O "Run now" permite escolher o modo (`telegram` / `telegram-maintain`) e overrides opcionais; a listagem de agendamentos não é duplicada aqui — há um deep-link para **Catálogo → Scheduled Jobs** (fonte única).
- **Setup**: onboarding pós-bootstrap — banner `⚠️ SETUP REQUIRED`, prontidão por componente, config/autenticação Telegram e config/teste Dispatcharr (ver secção "Onboarding / Setup operacional").
- **Diagnóstico**: inventário de ficheiros, RunReport completo e glossário de métricas.

Os editores inline do dashboard (Canais, Reviews, Regras, Afinidade, Ordering, Scheduler, Source selection e Import policies) passam a abrir num modal centrado, com fecho por `Cancelar`, clique fora ou `Escape` e gestão de foco: a abertura memoriza o elemento focado e o fecho restaura-o (sem lançar se o elemento tiver entretanto desaparecido). Não são usados diálogos nativos do browser para escolhas — por exemplo, **alterar a Política de publicação** de um canal (`channelPolicyForm` com `<select>` pré-selecionado) e **duplicar** uma Ordering List (`orderingDuplicateForm` com input pré-preenchido com `<key>-copy`) passam por painéis modais. No detalhe de uma Ordering List, cada item tem ainda um controlo **mover para posição N** (input numérico 1-based + botão *Mover*), que reutiliza `PUT /api/catalog/ordering-items/{id}` com `{ position }` 0-based (DC-12).

### Catálogo de Canais

O catálogo (`ChannelCatalogDbContext`, SQLite em `/data/channel-catalog.db`) gere:

| Separador | Conteúdo |
|---|---|
| **Visão Geral** | Estatísticas agregadas do catálogo (canais, aliases, regras, pending approvals). |
| **Canais** | Catálogo canónico por país: DisplayName, Key, **País**, Categoria, Grupo de publicação (grupo canónico, `groupKey`/`groupName`), Política de publicação, Activo, Aliases. |
| **Grupos** | Grupos canónicos de publicação, ordenados por `order` (ver "Grupos canónicos e atribuição por canal"). |
| **Regras** | IdentityRules explícitas que sobrepõem o matching automático. Criar regra com `ReviewOnly` permite fuzzy matching futuro; `Excluded` bloqueia o canal permanentemente. |
| **Afinidades** | Grupos com discriminator `Kind` (**Channel** ou **Country**). Uma Channel affinity liga variantes a um canal canónico (0..1 por `CanonicalChannelKey`); uma Country affinity liga variantes ao `CountryChannelValidator` para country-level targeting. As variantes são editadas num único campo separado pelo delimiter global (`/api/settings`, default `,`). |
| **Reviews** | Itens de revisão do Dispatcharr (decisões ambíguas ou uncertainas pendentes de decisão humana). A lista usa `GET /api/reviews` com paginação e filtro de estado (activos por omissão; `Resolved`/`Ignored` no histórico); as acções usam `POST /api/review/resolve` (Add Alias → `change.type='channelAlias'`; Create Channel → `change.type='canonicalChannel'`), `POST /api/review/ignore` (excluir, com `reason` obrigatório) e `POST /api/review/reopen` (reabrir um item terminal, com `justification`). A identidade interna é o `id` numérico do `ReviewItem`. |
| **Auditoria** | Visualizador (DC-5b) dos registos de auditoria via `GET /api/audit`, com filtros `objectType`/`objectId`/`limit` (default 100, cap 1000) e tabela de data/hora local, actor, operação, objecto, resultado, `before`/`after` (JSON sanitizado em `<details>` expansível) e detalhe. `503 audit-unavailable` é apresentado como estado inline claro. Read-only. |
| **Sync Runs** | Histórico de sincronizações Dispatcharr com contadores de created/merged/protected/removed. |
| **Pending** | Canais que geraram dúvida no country-level targeting e aguardam decisão manual (ver secção seguinte). |
| **Sources** | Sources de ingestão e *channel sources* associados. Cada linha de *channel source* tem a acção **Observações** (DC-5c) que abre o histórico de observações (`GET /api/catalog/channel-sources/{id}/observations?limit=200`) e permite registar uma nova amostra (`POST` no mesmo caminho com `quality`/`epg`/`availability`/`responseTimeMs`) via `apiRequest`. |
| **Scheduled Jobs** | Jobs agendados persistentes (tabela SQLite `scheduled_jobs`): cron de 5 campos, acção, activo, último/próximo tick e último resultado. Ver secção "Scheduler / Scheduled Jobs". |

### Grupos canónicos e atribuição por canal

O grupo de publicação é uma **propriedade do canal canónico** (`CanonicalChannel.GroupId`
→ `canonical_groups`, FK), não da source. A identidade do grupo é a `Key` estável
(ex.: `pt-desporto`); o `DisplayName` é apenas apresentação e pode ser renomeado sem
quebrar referências. A playlist M3U (`group-title`) e o agrupamento no Dispatcharr usam
o `DisplayName` do grupo do canal, **independentemente do `group-title` da source**; este
último é apenas **sugestão** de pré-selecção.

| Endpoint | Método | Descrição |
|---|---|---|
| `/api/catalog/canonical-groups` | `GET` | Lista os grupos canónicos (ordenados por `order`). |
| `/api/catalog/canonical-groups` | `POST` | Upsert por `Key`: `key`, `displayName`, `country`, `order`, `isEnabled`, `isDefault`. |
| `/api/catalog/canonical-groups/{id}` | `DELETE` | Elimina o grupo; **400** quando está em uso por canais (`GroupId`). |
| `/api/catalog/group-suggestion` | `GET` | Sugere `{ groupKey, groupName }` a partir de `?group=<group-title>&title=<título>`; `null` quando não há sugestão. |

O formulário de canal (criar/editar) e a API usam `groupKey`; o campo legacy
`editorialGroup` e a feature *Group Mapping* (source→canónico) foram removidos
(Waves D1/D2). A sugestão é apenas um default — o valor persistido é sempre a escolha
explícita do operador. Ver invariante em `AGENTS.md` §2.

Migrações relevantes: `AddCanonicalChannelGroupFk` (introduz `GroupId`/`canonical_groups`),
`DropGroupMappings` (remove `group_mappings`) e `DropEditorialGroupColumn`
(remove `canonical_channels.EditorialGroup`).

### Ordering Lists — uma lista por país (DC-11a / DC-D4)

É permitida **no máximo uma Ordering List por país** (`OrderingListEntity.Country`).
A unicidade por país garante que a lista de um país é inequívoca; o sync do
Dispatcharr consome a **única** Ordering List activa (ver § "Sync do Dispatcharr
a partir da Ordering List"). Múltiplas listas activas (possível apenas quando
`Country` é nulo) tornariam a escolha indeterminística (DC-D3/DC-D4). A regra é
imposta em três camadas:

- **Schema:** índice parcial único `IX_ordering_lists_Country` sobre `Country`, com
  filtro `"Country" IS NOT NULL` (migração `AddUniqueOrderingListCountry`). Listas sem
  país (`Country` nulo) continuam a poder coexistir; listas com o mesmo país não nulo
  são rejeitadas pela BD.
- **Resolver (`CatalogResolver`):** `Country` é normalizado (trim; vazio→null) em
  create/update/duplicate; `CreateOrderingListAsync` e `UpdateOrderingListAsync`
  rejeitam a colisão de país com `ChannelAdministrationException`
  (`ChannelAdministrationError.CountryConflict`). Manter o próprio país num update é
  permitido.
- **API/UI:** `POST /api/catalog/ordering-lists` e `PUT /api/catalog/ordering-lists/{id}`
  devolvem **409 Conflict** (`{ "error": ... }`) na colisão de país; payload inválido
  continua a sair como 400. A UI bloqueia a submissão de um país já usado (o servidor
  permanece a autoridade).

A duplicação de uma lista **não herda** o `Country` (a cópia não é a lista do país):
`DuplicateOrderingListAsync` limpa o campo na cópia e a `Key` nova continua a ser
validada como antes.

#### Sync do Dispatcharr a partir da Ordering List (DC-11b / DC-D3)

Quando existe **exactamente uma** Ordering List activa, o sync do Dispatcharr
compõe o plano a partir dessa lista
(`PlaylistComposerService.ComposeAsync` → `ChannelMatcher.BuildPlanFromCompositionAsync`)
em vez de ler `output/playlist.m3u` cru:

- **Membros:** só entram os canais `IsEnabled` da lista **com ≥1 fonte
  elegível**; itens desactivados e canais sem fonte elegível são omitidos (o
  compositor regista-os em `MissingChannels`).
- **Ordem:** os canais **novos** são criados no Dispatcharr com
  `channel_number` = **posição 0-based** do índice em `composition.Entries`
  (a ordem da lista, sem gaps). Canais existentes **não** são reordenados nesta
  wave (não existe `PATCH` de `channel_number` — follow-up).
- **Agrupamento:** continua a ser o do **canal canónico**
  (`CanonicalChannel.Group.DisplayName`); a Ordering List não define grupos.
- **Canais fora da lista:** os canais `CrawlerManaged` existentes que deixem de
  constar da lista são **mantidos** (não há DELETE/desactivação de canais); só
  as streams dentro de um canal casado seguem a lógica actual de
  remoção/substituição.
- **Fallback (decisões C/D):** com **0** listas activas ou com **várias**
  listas activas (selecção indeterminística), o sync cai no caminho legado
  (`playlist.m3u`) e regista o motivo no feed do Live Run e na consola — sem
  credenciais.
- **Sem catálogo:** o caminho `CatalogUnavailable`/legado mantém-se intacto
  (a resolução da lista só corre no caminho com catálogo).
- **Endpoints `/api/dispatcharr/dry-run` e `/api/dispatcharr/sync`:** com uma
  Ordering List activa, ambos compõem o plano a partir da lista (membros +
  ordem) e **o `playlistPath` indicado no corpo é ignorado**. Sem lista activa
  (0 ou >1), o `playlistPath` é usado como no caminho legado. O plano
  (`dispatcharr_plan_*.json`, incluindo o dry-run) expõe
  `proposedChannelNumber` = posição 0-based para os canais da lista; a
  numeração é aplicada na criação de canais novos (canais existentes não são
  reordenados nesta wave).
- **Selecção de fontes ignorada:** com composição, a selecção de fontes
  (artefacto `dispatcharr_selection_*.json`) é **ignorada** — a própria
  Ordering List é a autoridade da fonte. Não há `PATCH streams=[]` nem DELETE
  de streams por efeito da selecção.

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

1. **Aprovar** → Cria uma `IdentityRule` com `ReviewOnly`. Isto desbloqueia o matching/revisão do canal, mas **não** autoriza a criação automática: `ReviewOnly` nunca gera NewChannel. A criação continua a ser uma decisão humana explícita no catálogo canónico.
2. **Reprovar** → Cria uma `IdentityRule` com `Excluded` que impede o canal de ser aceite. Útil para descartar canais extranjeros que usam indicadores de país enganosos.

**Nota de segurança**: As URLs mostradas na lista de pending approvals são sanitizadas antes de guardar (`CredentialSanitizer.SanitizeUrl`), pelo que nunca expõem credenciais Xtream.

### Baseline canónico PT

O catálogo canónico PT (`docs/catalog/m3ucrawler_pt_canonical_catalog.json`,
`catalog_id=pt-canonical-tv`) é importado de forma idempotente e aditiva em cada
arranque por `ChannelCatalogBootstrapper.TryImportBaselineAsync`, depois do
`CatalogSeed`. A origem é resolvida por esta ordem de precedência:

1. `M3U_BASELINE_PATH` (override; definido na imagem Docker).
2. `<CWD>/docs/catalog/m3ucrawler_pt_canonical_catalog.json`.
3. `<AppContext.BaseDirectory>/docs/catalog/m3ucrawler_pt_canonical_catalog.json`
   (ficheiro empacotado em `/app/docs/catalog`).
4. Raiz do repositório (5 e 4 níveis acima do `BaseDirectory`), para
   desenvolvimento e testes.

Quando não existe ficheiro em nenhum destes caminhos, o baseline é lido do
**recurso embutido** na assembly (`EmbeddedResource`), garantindo que uma
instalação fresca tem na mesma os canais PT generalistas (RTP 1/2, SIC, TVI,
CNN Portugal, CMTV, …). A ausência do ficheiro é registada como warning, mas
não aborta o arranque.

### Afinidades e catálogo canónico por país (PHASE 9C.3)

- **Catálogo canónico por país**: `CanonicalChannelEntity.Country` (opcional,
  máx. 10; `null` = global). O país **não** faz parte da identidade — a
  identidade estável continua a ser `CanonicalChannel.Key`, imutável. Alterar
  `DisplayName` nunca altera a `Key`, não quebra afinidades e nunca cria uma
  nova afinidade.
- **Dois tipos de afinidade** (`AffinityKind`):
  - **Channel** — variantes que resolvem para um canal canónico
    (`CanonicalChannelKey`). Cardinalidade **0..1 por canal** (imposta por
    índice/validação). A resolução (`IdentityRule > Affinity > ChannelAlias`)
    considera apenas afinidades `Channel`.
  - **Country** — variantes/indicadores country-level injetados no
    `CountryChannelValidator`; não resolvem canal. A mesma variante pode
    coexistir numa Channel affinity e numa Country affinity (usos semânticos
    diferentes); a unicidade de `NormalizedMember` aplica-se apenas a
    `Channel` (índice único filtrado).
- **Separador global**: as variantes de uma afinidade são editadas num único
  campo, dividido pelo delimiter configurado em `runtime-data/app_settings.json`
  (`affinityVariantDelimiter`, default `,`, exposto em `GET/POST /api/settings`).
  É apenas uma convenção de edição: as variantes persistem como
  `AffinityMember` (uma por registo); mudar o delimiter não exige migration.
- **Dispatcharr**: canais criados pelo crawler usam o `DisplayName` actual do
  canal canónico e ficam registados como `CrawlerManaged`; só esses podem ser
  renomeados. Canais `External`/`Unknown` nunca são renomeados (read-only).
- **Migração** (`AddCanonicalCountryAndAffinityKind`): aditiva e transaccional;
  faz backfill de `CanonicalChannelId → CanonicalChannel.Key` por JOIN (nunca
  pelo nome), classifica grupos, faz split controlado de grupos mixed
  (Channel + Country, sem perder membros) e preserva órfãos para revisão. A
  reversibilidade é garantida pela tabela de proveniência
  `affinity_migration_backup` (não mapeada no EF, mantida para rollback). O
  `Down()` identifica os artefactos criados pelo Up **exclusivamente** por
  `GeneratedCountryGroupId` (sem `LIKE`, sem `MIN(Id)`, sem deduplicação
  arbitrária), restaura o `OriginalCountryCode` e, se o estado remanescente
  tiver `NormalizedMember` duplicados que impeçam restaurar a unicidade global
  antiga, **aborta** com erro explícito sem apagar dados; só conclui a remoção
  da proveniência e do schema novo quando o rollback é possível.

### Política de selecção de fontes (Waves 13-4 / 13-4b / 13-5 / 13-6)

O endpoint `GET/POST /api/catalog/source-selection-policies` gere a política
**global** que limita quantas fontes (streams) de um canal são publicadas.
`GET` devolve a política global (criando a default na primeira chamada); `POST`
faz upsert com os campos:

| Campo | Tipo | Default | Validação |
|---|---|---|---|
| `maxSourcesPerChannel` | int (obrigatório) | `10` | `>= 0`. `0` é válido e significa que nenhuma fonte do canal é publicada; valores negativos são rejeitados com `400`. |
| `preferDistinctProviders` | bool (obrigatório) | `true` | — |
| `maxSourcesPerProvider` | int ou `null` | `null` | `null` = sem limite; caso contrário `>= 1` (`0` e negativos rejeitados com `400`). |
| `allowFallbackToSameProvider` | bool (obrigatório) | `true` | — |

Payloads inválidos (campo obrigatório ausente, `maxSourcesPerChannel` negativo
ou `maxSourcesPerProvider` `<= 0`) devolvem `400 Bad Request` com
`{ "error": "..." }`. O sucesso devolve a política gravada (campos `id`,
`scopeKey`, `canonicalChannelKey`, `createdAtUtc`, `updatedAtUtc`).

#### Overrides por canal (Wave 13-4b)

Os endpoints `GET/POST /api/catalog/source-selection-policies/channels` e
`GET/DELETE /api/catalog/source-selection-policies/channels/{key}` gerem
overrides **por canal**, sob o mesmo gate de autenticação/CSRF:

- **Identidade:** a chave canónica (`CanonicalChannel.Key`, string estável) —
  nunca o `CanonicalChannelId`.
- **Substituição completa:** um override é uma política **completa** que
  substitui a política global por inteiro quando existe; **não** há merge campo
  a campo. A resolução efectiva é override por canal → global → defaults. Não
  há Foreign Key: um override de um canal inexistente é inerte, e a identidade
  sobrevive a apagar/recriar o canal com a mesma `Key`.
- **Validação:** os mesmos campos e regras da tabela acima. `maxSourcesPerChannel`
  `0` é válido (o canal não publica fontes por esse override); negativos são
  rejeitados com `400`.
- **Persistência por execução:** a política global e os overrides são carregados
  em lote no início de cada execução (2 queries, sem N+1 e sem cache entre
  execuções); o pipeline resolve a política efectiva por canal canónico.
- `GET .../channels` devolve a lista de overrides; `POST .../channels` faz
  upsert; `GET .../channels/{key}` devolve o override do canal; `DELETE
  .../channels/{key}` elimina-o. O dashboard inclui a gestão de overrides na
  área Catálogo, ao lado do cartão global.

#### Preview / Dry-Run (Wave 13-5)

`GET /api/catalog/source-selection-policies/preview` corre a selecção de fontes
em modo **read-only** sobre o catálogo actual e devolve o que seria seleccionado,
sem publicar, sem escrever ficheiros, sem mutar o catálogo/ownership/Dispatcharr
e sem criar a linha global da política. Usa o **mesmo** `SourceSelectionStage` da
produção (não há algoritmo duplicado) e carrega a política global de forma
read-only (nunca insere a linha default).

- **Filtro opcional:** `?channelKey=<CanonicalChannel.Key>` restringe a um canal
  canónico (match **exacto e case-sensitive**, `Ordinal`). Chave desconhecida
  devolve **HTTP 200** com `applied=false`, `status="channel-not-found"` e
  `channelsProcessed=0`, registando o filtro (sanitizado) em
  `source.channelKeyFilter`.
- **Gate:** GET-only (outros métodos → `405`), sob o gate de autenticação;
  `GET` não exige CSRF. `503` "Catálogo não inicializado." quando o catálogo não
  está disponível. Falhas inesperadas na rota devolvem
  `500 {"error":"preview-failed"}` com o response sempre fechado (o cliente não
  fica pendurado). O helper partilhado `WriteJsonAsync` **não** foi alterado: os
  estados HTTP vêm do status passado pela própria rota.
- **`status`:** campo top-level (`SourceSelectionPreviewStatuses`) que
  desambigua os casos antes colapsados em `applied=false`. Valores e
  precedência: `channel-not-found` (o filtro `channelKey` não corresponde a
  nenhum canal canónico; `applied=false`, `channelsProcessed=0`); senão
  `no-channels` (o catálogo não tem canais canónicos); senão `no-input` (há
  canais no âmbito mas nenhum tem `ChannelSource`; `applied=false` com
  `channelsProcessed>0` é válido e esperado); senão `applied` (a selecção
  correu). `channelsProcessed` mantém-se "canais canónicos no âmbito" e não foi
  zerado; `applied` não foi redefinido.
- **Resposta:** `applied`, `status`, `generatedAtUtc`, `inputStreamCount`,
  `source` (origem `catalog` + contagens), `metrics`, `channels` e as listas
  top-level **disjuntas** `unmatched[]` e `ambiguous[]` (mesma forma). Métricas
  agregadas:
  `channelsProcessed` (canais canónicos no âmbito, incluindo os sem fontes),
  `channelsWithSources`, candidatos, seleccionados, rejeitados,
  `unmatchedStreamCount` (sem hit), `ambiguousStreamCount` (URL mapeada a >1
  canal canónico), `totalUnmatchedStreamCount` (soma das duas), canais no limite,
  rejeições por limite de canal/fornecedor/fallback/source desactivada,
  `diversitySelectionCount` (selecções por diversidade), `distinctProviderCount`
  (fornecedores distintos com ≥1 selecção), `fillSelectionCount`,
  `rejectionCounts` e `providerDistribution`. Cada canal tem `policyScope`
  `override`/`global`/`default`, política efectiva, contagens e listas
  `selected`/`rejected` com `rank`/`decision`/`reason`.
- **Sanitização:** todas as URLs emitidas passam
  `CredentialSanitizer.SanitizeUrl` (mesmo que o catálogo já guarde URLs
  sanitizadas) e `unmatched[].title`/`ambiguous[].title` passam `SanitizeText`;
  não há credenciais no output.
- **Limitações:** o input é o catálogo (`ChannelSource`), não a descoberta
  Telegram ao vivo; `IsWorking` usa `Availability not Dead/Unreachable` como
  proxy; `unmatched[]` e `ambiguous[]` são disjuntos por construção
  (`unmatchedStreamCount` conta só as não-ambíguas, `ambiguousStreamCount` só as
  ambíguas); `fillSelectionCount` é o proxy da Fase B; canais sem fontes contam
  em `channelsProcessed` (rotulado na UI "Canais no âmbito") mas não aparecem em
  `channels`. **Limitação de paridade: `ResponseTime`** — o preview usa o valor
  **persistido** `ChannelSourceEntity.LastResponseTimeMs` quando presente; na
  prática essa coluna só é escrita como `0` no insert e nunca é actualizada
  (`CatalogResolver.cs:1496`; update `:1466-1478`), pelo que está normalmente a
  `0`/indisponível e **não** representa o `DurationMs` da probe ao vivo; a
  observação é append-only e sem flag de sucesso
  (`WebDashboardService.cs:2156`) e o pipeline ignora `stream.ResponseTime`
  (`PipelineIngestionService.cs:236-247`); o valor real em produção é o stopwatch
  `DurationMs` da probe exacta (`M3uTesterService.cs:550,555`). Por isso o
  preview **não** reproduz a ordenação por `ResponseTimeKey`
  (`ChannelSourceSelector.cs:128,260-261`) e a sua ordenação por response time
  **pode divergir** da produção — em empates nas primeiras quatro chaves, a
  ordem e o conjunto seleccionado podem diferir da produção. As métricas ricas
  são âmbito do preview — `RunReport.SourceSelection`
  mantém-se só contagens. A integração no Dispatcharr foi entregue na **Wave
  13-6** (secção seguinte); o `MatchPlan` mantém-se inalterado.

#### Integração no Dispatcharr (Wave 13-6)

A selecção de fontes é transportada até ao apply do Dispatcharr por um artefacto
próprio, `DispatcharrSourceSelection`, sem alterar o `MatchPlan` (que continua a
ser exclusivamente o resultado do matching).

- **Artefacto:** `DispatcharrSourceSelection` tem `generatedAtUtc`, `applied`,
  `channels[]` (`canonicalChannelKey` — identidade; `canonicalChannelId`
  transiente; `policyScope`; `candidateCount`; `rejectedCount`; `selected[]` com
  `streamUrl`/`rank`/`provider`/`reason`/`sourceId`) e `counts` (`channels`,
  `candidates`, `selected`, `rejected`, `unmatched`, `ambiguous`; `unmatched` e
  `ambiguous` são disjuntos). `applied` (default `true`) indica se o estágio
  aplicou a selecção; é construído **uma vez** a partir do
  `SourceSelectionStageResult` por
  `DispatcharrSourceSelectionFactory.FromStageResult(result, policies, nowUtc)`
  (projecção pura; reutiliza literalmente `SelectionReasons`; não recalcula a
  selecção nem toca no catálogo). A identidade é `CanonicalChannel.Key`; o
  `CanonicalChannelId` é apenas transportado.
- **Artefacto não aplicado:** `Program.cs` só constrói/passa o artefacto quando
  `SourceSelectionStageResult.Applied == true`; um resultado não aplicado
  (catálogo nulo, input vazio, falha de leitura ou catálogo sem `ChannelSource`)
  resulta em `selection = null` (sem filtragem e sem artefacto escrito). O
  `DispatcharrSyncService` também trata `applied == false` como `null` — um
  artefacto não aplicado **nunca** significa "seleccionar zero".
- **Regra de associação:** apenas as fontes `Selected` são associadas/publicadas;
  `Unmatched` e `Ambiguous` **não** são associados (alteração deliberada face ao
  comportamento legado). Aplica-se à associação de canal e a
  `globalKeepStreamIds`/`globalRemoveCandidates`, sem mutar o `plan`.
- **Canal avaliado vs não avaliado:** um canal com entrada no artefacto — mesmo
  com `Selected = []` — é **avaliado** e segue selecção estrita. Um canal com
  `CanonicalChannelKey` nula/vazia **ou sem** entrada no artefacto é **não
  avaliado**: comportamento conservador — mantém as streams existentes na
  associação (qualquer ownership), **não** desassocia nem `DELETE`, e **não**
  faz `POST` de streams novas. Os dois casos são deliberadamente distintos.
- **Ownership e cleanup:** as streams criadas pelo crawler passam a ser
  registadas `CrawlerManaged` via `EnsureStreamOwnershipAsync` após o
  `CreateAsync` (antes em falta). Sob selecção, as CrawlerManaged não
  seleccionadas são removidas da associação e tornam-se candidatas a `DELETE`
  (guard de ownership inalterado); as `External`/`Unknown` não seleccionadas
  ficam associadas e **nunca** são eliminadas. Um canal novo sem streams
  efectivas não é criado; um canal existente só recebe `PATCH streams=[]` se
  actualmente tiver streams (idempotência).
- **Resiliência e compensação:** cada escrita de `EnsureStreamOwnershipAsync` é
  protegida individualmente — uma falha é registada no relatório
  (`FailedReportEntry`) e a run **continua** (sem row de ownership falsa, guards
  preservados). Se a criação do canal novo falhar depois de a Phase 2 ter criado
  streams, ou se um `POST` de stream falhar a meio da Phase 2 depois de já
  existir pelo menos uma criada, essas streams são registadas `CrawlerManaged`
  com channel id `0` e adicionadas aos candidatos de remoção da Phase 4, em
  qualquer early-return posterior à Phase 2, para que o `DELETE` com guard remova
  **apenas** streams provadamente criadas pelo crawler. A mensagem de falha de
  criação de stream é sanitizada (`CredentialSanitizer.SanitizeUrl`).
- **Persistência sanitizada:** `DispatcharrSourceSelectionSerializer` aplica
  `CredentialSanitizer.SanitizeUrl` a cada `StreamUrl`; o ficheiro
  `output/dispatcharr_selection_<yyyyMMdd_HHmmss>.json` é escrito por
  `DispatcharrSyncService.RunAsync(path, selection, ct)` **antes** do branch
  dry-run/apply — o dry-run também o produz e nunca contém credenciais.
- **Compatibilidade:** `RunAsync`/`ApplyAsync` ganharam overloads com
  `DispatcharrSourceSelection? selection`; os overloads antigos delegam com
  `selection: null`. **Correcção documental:** nesse caminho,
  matching/ordering/rename/criação de canal e relatório mantêm-se equivalentes,
  mas o **registo de ownership das streams criadas nessa execução também
  ocorre** quando há catálogo — alteração intencional da Wave 13-6 que habilita
  cleanup futuro seguro. Não se afirma que `selection == null` seja totalmente
  inalterado face ao pré-13-6.
- **Limitação documentada:** os caminhos `--dispatcharr-sync` standalone e
  `ScheduledDispatcharrSyncAction` passam `selection = null` por não existir
  stage de selecção nessa execução; nesses caminhos **não** há correlação
  heurística entre a playlist e o artefacto de selecção. Churn/estabilidade,
  review-queue/hard-block, `ProviderDefinition`, `SelectionPolicy` separada e
  `MinimumValidatedSources` permanecem fora de âmbito.

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

## Ciclo de vida de configuração (PHASE 9C.1)

Uma instalação nova deve ser **fail-safe**: arranca, expõe o Dashboard e **não**
inicia discovery nem jobs automáticos enquanto não estiver configurada. O estado
do lifecycle é persistido e sobrevive a restart.

### Estados

| Estado | Significado |
|---|---|
| `NOT_CONFIGURED` | Instalação nova, sem estado persistido e sem evidência de operação anterior. Discovery automático e scheduler bloqueados. |
| `CONFIGURING` | Configuração em curso. Continua a bloquear discovery automático e scheduler. |
| `READY` | Instalação configurada. O comportamento operacional existente é preservado. |

Os nomes `NOT_CONFIGURED` / `CONFIGURING` / `READY` são contrato externo
(reportados em `/api/configuration/lifecycle` e nos logs) e não devem ser
renomeados.

### Autoridade e bootstrap

- **O estado persistido é a autoridade.** Se existir estado persistido válido,
  é usado sem inferência.
- Sem estado persistido, corre-se o bootstrap:
  1. procurar **evidência objectiva** de instalação já operacional;
  2. se existir, adoptar `READY` (**legacy adoption**) e registar em log;
  3. caso contrário, iniciar em `NOT_CONFIGURED`.

### Evidência de legacy adoption

Considera-se evidência (entidades que não são criadas por migration/seed numa
instalação nova) a existência de qualquer uma de:

- catálogo persistente: `sources`, `channel-sources`, observações, `ordering
  lists/items`, `import policies`, jobs agendados,
  `sync-runs/steps`, `review items`, `matching audits`, `pending country
  approvals`, `affinity groups`, ownership Dispatcharr e `identity rules`;
- artefactos de output: `import_history.json`, `playlist.m3u`,
  `telegram_run_report.json` ou `telegram_playlist_*.m3u`.

`CanonicalChannels`, `ChannelAliases` (seed/baseline),
`CanonicalGroups` (criados por migration/seed) e `SourcePriorityPolicies`
(default global lazily) **não** contam como evidência.

### Persistência

O estado é gravado em `configuration_lifecycle.json`, ao lado do
`channel-catalog.db` (mesmo volume persistente; em produção `/data`). Reutiliza
o padrão de estado persistente já existente (JSON em runtime-data) — não
introduz tabela nem base de dados nova. A escrita é atómica.

### Gates

- **Scheduler:** `ScheduledJobRunner` recusa executar qualquer job automático em
  `NOT_CONFIGURED`/`CONFIGURING` e regista `blocked:not-configured`, sem o tratar
  como sucesso nem avançar o próximo tick (o job continua vencido e corre assim
  que a instalação fique `READY`).
- **Discovery automático (Telegram):** o modo manutenção
  (`--telegram-maintain`) e o loop (`--loop-hours`) são bloqueados em
  `NOT_CONFIGURED`/`CONFIGURING`. Invocações manuais de um único ciclo
  permanecem operador-iniciadas.
- A decisão é centralizada em `IConfigurationGate` (um único mecanismo).

### API e observabilidade

`GET /api/configuration/lifecycle` devolve o estado, a marca de adopção legacy,
a razão (não sensível) e a avaliação **advisory** dos requisitos da PHASE 9C
§5. O endpoint é de leitura apenas, respeita o token do dashboard
(`--web-token`) e não expõe operações destrutivas.

Os requisitos da PHASE 9C §32.18 §5 (ordering list, import policies, grupos,
sources activas, source priority, etc.) são **advisory** nesta wave: informam,
não bloqueiam `READY`. A sua validação como gate pertence ao wizard (wave
seguinte).

Documentação de arquitectura: `docs/architecture/configuration-lifecycle.md`.

## Autenticação e bootstrap (PHASE 9C.2)

Numa instalação nova o Dashboard expõe um **wizard mínimo** que cria o primeiro
administrador e conclui o bootstrap; só depois o lifecycle passa a `READY`.
Não é necessário configurar Telegram, sources, ordering, import policies, grupos,
source priority, affinities ou scheduler para ficar `READY` (esses itens
continuam configuráveis depois).

### Fluxo

```
NOT_CONFIGURED
  → GET  /bootstrap
  → POST /api/bootstrap/start     → CONFIGURING
  → POST /api/bootstrap/admin     → cria o 1.º administrador
  → POST /api/bootstrap/complete  → valida L2 → READY
  → POST /api/session             → login (cookie de sessão)
  → Dashboard normal autenticado
```

### Caminho `BOOTSTRAP_REQUIRED` (PHASE 9C.5)

Numa instalação legacy adoptada (`READY` persistido, `AdoptedFromLegacy=true`)
mas **sem administrador**, o wizard é servido sem passar por `CONFIGURING`:

```
READY (sem administrador)
  → GET  /bootstrap
  → POST /api/bootstrap/admin     → cria o 1.º administrador (estado PERMANECE READY)
  → POST /api/session             → login (AuthMode passa a UserAuth)
  → Dashboard normal autenticado
```

A configuração existente não é recriada nem reconfigurada (apenas uma linha
é acrescentada a `admin_users`) e o estado persistido não é alterado. Antes
da criação, os endpoints administrativos normais ficam bloqueados
(`403 bootstrap-required`, com o estado de lifecycle real no corpo). Depois
de existir **qualquer** registo em `admin_users` (activo ou desactivado), o
bootstrap da criação fecha (`AlreadyReady`). O wizard exige confirmação da
password no cliente; as regras de validação (mínimo 12 caracteres) mantêm-se.

> **Âmbito (2026-09-17).** O fluxo first-run da 9C destina-se a instalações
> **novas/limpas**. Migração/recuperação de configuração de administrador de
> versões anteriores **não** faz parte do ciclo de vida suportado e não existe
> mecanismo de recuperação/reactivação de administradores. Um estado
> inconsistente (`READY` com administrador desactivado) resulta em modo
> `Bootstrap` com criação rejeitada e sem login humano; pode exigir
> reinicializar a instalação (`runtime-data`) e configurar de raiz.

### Configuração mínima (L2) exigida para `READY`

- administrador activo;
- catálogo canónico utilizável (com canais);
- output directory utilizável (verificado com escrita real);
- Dispatcharr válido **apenas se estiver activado**.

Telegram, sources, ordering, import policies, grupos, source priority e scheduler
**não** são obrigatórios para `READY`.

### Modelo de autenticação

- **Primeiro administrador**: password definida explicitamente no wizard; mínimo
  12 caracteres; sem regras artificiais de complexidade; sem password default.
- **Hashing**: PBKDF2-HMAC-SHA256 nativo (210 000 iterações, salt 16 B, hash
  32 B), formato versionado `pbkdf2-sha256$<iter>$<salt>$<hash>`.
- **Sessões**: tabela `admin_sessions` em SQLite; cookie `m3u_session` com apenas
  um id opaco de 256 bits (nunca username/password/hash), `HttpOnly`,
  `SameSite=Strict` e `Secure` quando HTTPS. Sessão revogável (logout), expira
  (12 h) e sobrevive a restart. Cada login roda o id (anti session-fixation).
- **CSRF**: token por sessão exigido no header `X-CSRF-Token` em métodos mutantes.
- Password e hash nunca aparecem em logs, respostas ou erros; utilizador
  inexistente usa hash dummy (tempo uniforme) e erro genérico.

### Alterar/recuperar password de administrador

**Pelo Dashboard (autenticado).** Área "Alterar password"
(actual/nova/confirmar) que faz `POST /api/session/password` (CSRF
obrigatório) com `{currentPassword, newPassword}`. Em sucesso devolve
`200 {message:"password-changed", reloginRequired:true}` e a UI mostra
`password alterada — faça login novamente`, redireccionando para o login.
A alteração **revoga todas as sessões do utilizador na mesma transacção**
(`AdminUserStore.ChangePasswordAsync`), pelo que é necessário voltar a
autenticar. Erros: `400 invalid-current-password`,
`400 invalid-new-password`, `401 authentication-required`,
`403 csrf-invalid`; apenas `POST` (`405` com `Allow: POST` nos restantes).
Nunca devolve nem registra passwords/hashes.

**Recuperação host-only (CLI).** Numa máquina com acesso ao catálogo:

```bash
m3uCrawler --admin-reset-password <username>
```

A password é pedida interactivamente (`Nova password:` /
`Confirmar nova password:`) e **nunca** é passada como argumento. Exit
codes: `0` alterada, `2` utilizador não encontrado, `3` password inválida,
`4` passwords não coincidem, `1` erro de uso/setup. Não verifica a password
actual (recuperação host-only) e não arranca web/Telegram/discovery/sync.
**Não existe endpoint HTTP não autenticado de reset**; o bootstrap
(`BOOTSTRAP_REQUIRED`) não é afectado. Contrato completo e limitação de
máscara de input em contentores: ver
`docs/architecture/configuration-lifecycle.md` §"Gestão da password de
administrador".

### Modos de autorização

| Modo | Condição | Efeito |
|---|---|---|
| Bootstrap | `NOT_CONFIGURED`/`CONFIGURING`, **ou** `READY` sem administrador (`BOOTSTRAP_REQUIRED`) | Só bootstrap/sessão/lifecycle/version; restantes endpoints → 403 |
| UserAuth | `READY` + administrador | Endpoints normais exigem sessão humana **ou** credencial de máquina válida; mutantes exigem CSRF |
| Legacy | contexto explicitamente standalone/testes (lifecycle e auth não ligados) | Mantém o comportamento aberto de `--web-token`; não cria administrador. Deixou de ser o modo de `READY` sem administrador na PHASE 9C.5 |

`--web-token` mantém-se como **credencial de máquina/automação** em todos os
modos (quando configurado). Precedência: o token é avaliado primeiro e, quando
válido, **autoriza o pedido sem exigir sessão humana** (inclusive em `READY` +
administrador), sem criar utilizador nem sessão. Sem `--web-token`, em `READY` o
acesso exige sessão humana (e CSRF nos métodos mutantes). Não substitui a
autenticação humana e não é necessário numa instalação nova.

A página autenticada do Dashboard recebe o token CSRF apenas **em memória
JavaScript** (nunca em URL, query, `localStorage` ou logs) e envia-o
automaticamente em métodos mutantes através de um helper de `fetch`.

Se a inicialização do serviço de autenticação falhar, o Dashboard entra em
**fail-closed** em `READY` (401, sem acesso administrativo anónimo) — nunca cai
silenciosamente para o modo legacy.

### Limitação TLS

O dashboard serve HTTP. Sem TLS não é possível garantir `Secure` nem proteger
credenciais em trânsito; uma exposição fora de rede confiável deve usar reverse
proxy/TLS.

## Onboarding / Setup operacional (pós-bootstrap)

`READY` (bootstrap) **não** significa operacional. O Dashboard expõe uma vista
**Setup** e um banner superior `⚠️ SETUP REQUIRED` que distingue
`Bootstrap: READY` de `Operational`, mostrando cada componente com ✓/❌ e o
respectivo formulário. Fluxo de onboarding:

```
Bootstrap → Admin → Setup Required → Telegram → Dispatcharr → Sources → Operational Ready
```

| Endpoint | Método | Descrição |
|---|---|---|
| `/api/configuration/readiness` | GET | Snapshot de prontidão (`bootstrapReady`, `setupComplete`, `operationalReady`, `sourcesCount`, `items[]`, `missingRequired[]`). |
| `/api/telegram/config` | GET/POST | Lê/grava `api_id`, `phone_number`, `session_pathname`; `api_hash` nunca é devolvido (`hasApiHash`). |
| `/api/telegram/auth/start` | POST | Inicia o login Telegram por passos. |
| `/api/telegram/auth/code` | POST | Submete o código de verificação. |
| `/api/telegram/auth/password` | POST | Submete a password 2FA. |
| `/api/telegram/auth/status` | GET | Estado do login (`state`, `userName`, `detail`, `configured`). |
| `/api/dispatcharr/config` | GET/POST | Lê/grava `enabled`, `base_url`, `dry_run`, `match_threshold`, `auto_create_groups`, `provider_priority`, `alias_file`, `target_group_name` e credenciais (nunca devolvidas). Patch semântico: chave ausente preserva o valor; `match_threshold` fora de 0–100 → 400 `invalid-payload`. |
| `/api/dispatcharr/test` | POST | Teste de ligação read-only (`GET /api/core/version/`). |
| `/api/dispatcharr/dry-run` | POST | **W6** — Gera `MatchPlan` + `SyncReport` sem escrever no Dispatcharr. Corpo `{ "playlistPath": "playlist.m3u" }` (caminho relativo resolvido sob o output dir). **DC-11b:** com uma Ordering List activa, o plano é composto da lista e o `playlistPath` é ignorado. Devolve `{status, mode:"dry-run", planPath, reportPath, counts{...}}`. |
| `/api/dispatcharr/sync` | POST | **W6** — Aplica a sincronização (mutação real no Dispatcharr, sujeita a `dispatcharr_enabled`/`dispatcharr_dry_run`). Mesmo contrato, `mode:"sync"`; com uma Ordering List activa, segue a lista e ignora o `playlistPath`. |

Todos os `POST` são métodos mutantes e, em `UserAuth`, exigem `X-CSRF-Token`.

O separador **Dispatcharr** do Dashboard expõe dois botões: **Dry Run** (sem confirmação)
e **Sync Dispatcharr** (confirmação forte, pois é uma mutação real), com guarda de
busy/anti-duplo-clique, apresentação de `counts`/`planPath`/`reportPath` e erro real do
backend. O wiring de produção (`Program.cs`) regista o **mesmo** `DispatcharrSyncCoordinator`
do `RunPublicationService` com o dashboard via `WebDashboardService.SetDispatcharrSync`
(mais um `DispatcharrConcurrencyGate`, que devolve `409 concurrency-conflict` a pedidos
concorrentes). Em `--web` **standalone** (sem `--telegram`) não há coordenador ligado e
ambos os endpoints respondem `503 dispatcharr-unavailable` — o que é o comportamento
esperado. Nunca são expostas credenciais.

### Autenticação Telegram interactiva

`TelegramAuthService` corre `WTelegram.Client.Login` por passos
(`start` → `code` → opcional `password`), sem `Console.ReadLine` no fluxo web.
A sessão é persistida em `session.dat` via `session_pathname`. O `api_hash`
nunca é devolvido nem registado e o `detail` de estado é sanitizado.

### Dispatcharr (config + teste)

`DispatcharrConfigurationService` grava a configuração em `wtelegram.config`
por um writer atómico que preserva chaves desconhecidas e aplica permissões
restritivas (600). O teste de ligação é **read-only** e distingue `CONNECTED`,
`AUTHENTICATION_FAILED`, `UNREACHABLE`, `INVALID_CONFIGURATION` e `ERROR`;
nunca faz escrita nem sincronização.

O Setup → Dispatcharr (DC-5f) expõe agora os campos avançados suportados pela
API e usados pelo sync (`DispatcharrSyncService`): `match_threshold`
(`MatchingOptions.MatchThreshold`), `auto_create_groups`, `provider_priority`,
`alias_file`, e `username`/`password` (write-only, placeholder *configurado*).
`apiKey`, `username` e `password` só são enviados no POST quando preenchidos —
campo vazio mantém o valor persistido. `match_threshold` é validado no cliente
(0–100) e no servidor (400). O campo `target_group_name` é mostrado **apenas
como leitura, informativo**, com uma nota explícita: é persistido mas **não
aplicado** pelo sync actual (lacuna conhecida) — o grupo de publicação vem do
grupo do canal canónico. Não é enviado no POST.

### Bootstrap Ready vs Operational Ready

- **Bootstrap Ready**: lifecycle `READY` (administrador + L2).
- **Operational Ready**: administrador + Telegram autenticado + Dispatcharr
  válido (se activado) + catálogo + output, e `sources >= 1`.
- `sources` **não** entra em `SetupComplete` (evita bloquear o scheduler por
  nunca haver fontes); só afecta `operationalReady`.
- O scheduler/discovery exige `READY` **e** `SetupComplete`; jobs bloqueados
  mantêm `NextRunAtUtc` inalterado e são registados.
- Instalações adoptadas como legacy (`adoptedFromLegacy`) ficam grandfathered.
- O provisionamento inicial de país (`runtime-data/countries/*.json`, nunca
  sobreposto) corre no arranque em `--web` e `--telegram`.

Regras completas, componentes obrigatórios e a dívida do baseline de país em
`docs/architecture/configuration-lifecycle.md` §"Operational Readiness
(pós-bootstrap)".

## Live Run Monitor (PHASE 9C.4)

Observabilidade da execução Telegram (descoberta + validação + composição +
sync) com trigger manual opt-in e arranque agendado, **sem** duplicar pipeline
nem scheduler.

### Modelo persistente

- `live_runs` + `live_run_steps` (migration aditiva `AddLiveRuns`).
- Fases: `idle`, `reading-telegram`, `discovering`, `downloading`, `analyzing`,
  `validating`, `composing`, `syncing-dispatcharr`, `completed`, `error`.
- Estado terminal: `Unknown`, `Completed`, `Failed`
  (`Failed` ≠ fase `Error` — são conceitos distintos).
- `CountsJson` é a representação persistente de `LiveRunCounts` (tipado). Nada
  é contado a partir de logs.
- Um run interrompido por restart (`FinishedAtUtc == null` e
  `TerminalStatus == Unknown`) é recuperado como `Failed`.
- `SyncRun`/`SyncRunStep` continuam uma família separada e não são tocados.

### Execução única (`RunCoordinator`)

CLI, scheduler e API manual convergem no **mesmo** `RunCoordinator`
(`LiveRunHost`). O lock é partilhado, pelo que uma corrida manual/scheduler
resulta numa única execução (`409 already-running` para o perdedor). `Source`
(`cli`/`manual`/`scheduler`) e `Mode` (`telegram`/`telegram-maintain`) ficam
registados. A pipeline invocada é sempre a existente
(`SearchAndTestM3UInTelegramAsync` / `RunTelegramMaintenanceCycle`).

### API

| Endpoint | Comportamento |
|---|---|
| `GET /api/run/status` | Snapshot sanitizado: `isRunning`, `status`, `runId`, `mode`, `source`, `phase`, `phaseStartedAtUtc`, `durationMs`, `counts`, `recentActivities`, `recentRuns` (24 h), `webAllowTrigger`. `503 pipeline-not-configured` quando não há pipeline Telegram no processo. |
| `POST /api/run/start` | Arranque assíncrono (não bloqueia até ao fim). Corpo opcional `{ mode, keyword?, historyHours?, maxStreams? }`: `mode` ∈ `telegram` \| `telegram-maintain` (default `telegram`; valor desconhecido → `400`), `historyHours` ∈ 1–720 e `maxStreams` ∈ 1–5000 (valores fora do intervalo ou ausentes caem no default persistido). `202` aceite · `409 already-running` · `503 web-allow-trigger-disabled` · `503 pipeline-not-configured` · `400 invalid payload` · `401`/`403` conforme o gate 9C.2. |
| `GET /api/publication/status` | Snapshot derivado dos cursores de publicação do catálogo: `catalogChangedAtUtc` (`MAX` sobre `UpdatedAtUtc`/`CreatedAtUtc` das entidades que afectam a próxima `playlist.m3u`), `lastSuccessfulPublicationAtUtc` (`MAX(LiveRun.FinishedAtUtc) WHERE TerminalStatus=Completed AND Mode∈{telegram, telegram-maintain}`), `publicationPending` (`true` se o catálogo mudou depois da última publicação, ou se nunca houve uma publicação bem-sucedida). `503` quando o catálogo não está inicializado. DL-019/DL-130. |

#### Cursores de publicação (DL-130)

`GET /api/publication/status` é a primeira superfície observável do estado de
publicação pendente. Os dois cursores são derivados por query — não há
persistência adicional, schema migration ou novo campo em `ReviewItemEntity`.

- **`catalogChangedAtUtc`** = `MAX` sobre os sinais canónicos das entidades
  que contribuem para a próxima `playlist.m3u`:
  `CanonicalChannel.UpdatedAtUtc`, `ChannelSource.UpdatedAtUtc`,
  `Source.UpdatedAtUtc`, `ChannelAlias.CreatedAtUtc` (a entidade não tem
  `UpdatedAtUtc`), `ExternalIdentity.UpdatedAtUtc`,
  `ProviderAccount.UpdatedAtUtc`, e `ReviewItem.UpdatedAtUtc` filtrado por
  `State = Resolved` (exclui `Ignored`, que não altera o catálogo).
- **`lastSuccessfulPublicationAtUtc`** = `MAX(LiveRun.FinishedAtUtc) WHERE
  TerminalStatus = Completed AND Mode ∈ {telegram, telegram-maintain}`.
  O filtro `TerminalStatus = Completed` é essencial porque
  `MarkTerminalAsync`/`RecoverInterruptedRunsAsync` também escrevem
  `FinishedAtUtc` em runs `Failed` e em runs reaped após restart.
- **`publicationPending`** = `true` se `lastSuccessfulPublicationAtUtc`
  é `null` (nunca houve Run Completed, portanto nunca houve
  `playlist.m3u` publicada — DL-019 per-artifact atomic write, sem
  caminho de upload manual), OU se `catalogChangedAtUtc >
  lastSuccessfulPublicationAtUtc`.

**Separação DL-019/DL-128:** uma falha do Dispatcharr após a escrita
atómica de `playlist.m3u` **não** invalida o cursor de publicação do
ficheiro — `LiveRun.TerminalStatus=Completed` é suficiente. O outcome
do Dispatcharr é sinal independente, exposto separadamente em
`/api/dispatcharr/state`.

A autorização reutiliza o gate da 9C.2/9C.5 (sessão + CSRF em `UserAuth`,
`--web-token` como credencial de máquina, Bootstrap bloqueado; `READY` sem
administrador é `BOOTSTRAP_REQUIRED` e também bloqueia). **Não existe
autenticação dedicada.** O trigger manual exige `--web-allow-trigger`
(opt-in, default desactivado).

Em **standalone** (`--web` sem `--telegram`) ambos os endpoints devolvem
`503 pipeline-not-configured` — o dashboard continua a arrancar normalmente.

### Actividades (ring buffer em memória)

`LiveRunActivityFeed` é um ring buffer thread-safe de capacidade 200, **não
persistido** em SQLite nem em disco. Só acompanha o run corrente/último run
in-process; após restart não existem actividades. Mensagens e metadata passam
por `LiveRunSanitizer` (URLs com credenciais, `Authorization: Bearer …`,
`cookie`, `api_key=…`, `session=…`).

### Arranque agendado (sem `StartAtUtc`)

Duas acções estáveis no scheduler existente (`ScheduledJobRunner`):

| Acção | Modo |
|---|---|
| `telegramRun` | `telegram` |
| `telegramMaintainRun` | `telegram-maintain` |

A UI calcula a `CronExpression` (não existe `startAtUtc` nem scheduler
paralelo). Cron inválido é rejeitado de forma segura: o job não executa, é
neutralizado (`LastResult = invalid-cron:…`) e o tick continua a processar os
restantes jobs.

### Vista "Execução ao Vivo" no dashboard

Estado, runId, fase, desde quando, duração, última actualização, mensagem,
contadores, últimas actividades, últimas execuções (24 h), estado do trigger,
botão **Run now** e um deep-link para os agendamentos Telegram. O "Run now" expõe
um select de **Modo** (`telegram` / `telegram-maintain`) e overrides opcionais
(`keyword`, `historyHours`, `minHistoryHours`, `maxStreams`) enviados em `POST /api/run/start`;
sem overrides, o run usa a configuração de discovery persistida. A listagem de
jobs agendados **não é duplicada** na vista: o botão abre
**Catálogo → Scheduled Jobs** (`showView('catalog')` + sub-tab `scheduled`), que
é a fonte única. Actualização automática por **polling de 3 s** (apenas com a
vista activa e sem pedidos sobrepostos). Não há SSE/WebSocket, tail de logs nem
parsing de `docker logs`.

O feed de actividades (`recentActivities`) transporta agora **`metadata`** (dict opcional de contexto) e as actividades de fase incluem **`runId`**; cada actividade é apresentada com **badge de categoria** e o metadata como `key=value`. O contexto cobre a cadeia completa: leitura do Telegram (`keyword` + janela), mensagem analisada (`messageId`/`messageDateUtc`/`chat`), candidate criado (`candidateId`/`kind`/`from`), promoção Xtream (`candidateId`/`parentCandidateId`), download/parse (`candidateId` + motivo), validação por país e por playlist (**physical N / reused M**), conclusão do run e Dispatcharr (contadores do sync; tipo de erro).

A dedup física W-DEDUP passa a ser visível **sem alterar a sua semântica**: o `AccountValidator` emite uma activity por ronda de conta (`account validation: N physical, M reused, K failed`, com a conta mascarada) e o contador `StreamsSkippedAlreadyValidated` aparece no Live Run, no Overview e no histórico de Execuções.

Limitações: não há evento por stream individual (o ring buffer de 200 tornaria o feed inútil); `requestId` só existe no caminho de download; o `PipelineTrace` (`M3UCRAWLER_TRACE`) continua consola-only e separado do Live Run; em modo CLI não existe `runId` operacional.

### Configuração no Dashboard: o que está exposto e o que não

A vista **Descoberta** expõe a configuração operacional de discovery numa única card, **"Configuração de discovery predefinida"** (SSOT `app_settings.json#discovery`, via `GET/POST /api/discovery/settings`): `keyword`, `MinHistoryHours` (≥ 0), `HistoryHours`/Max (1–1440) e `MaxStreams` (≥ 1). A janela inclusiva é explicada a partir dos valores (ex.: Min 425h / Max 450h ⇒ `425h ≤ idade ≤ 450h`) e "Min 0 = sem limite inferior (comportamento legacy)". Erros de validação do backend (HTTP 400) surgem inline e, após guardar, o formulário recarrega os valores persistidos. Esta card é a **base/fallback** (CLI, runs manuais sem overrides e jobs sem overrides); cada **Scheduled Job** pode definir overrides próprios (ver "Scheduler / Scheduled Jobs").

**Deliberadamente não expostos** (e porquê):

- **Credenciais WTelegram/Dispatcharr** (`api_hash`, `api_key`, username/password) — write-only nos formulários de Setup existentes; o dashboard não as revela. `dispatcharr_username`/`dispatcharr_password`/`session_pathname` são API-only. Segredos nunca em superfícies de leitura.
- **Flags de deployment/processo** (`--web`, `--web-port`, `--web-token`, `--web-allow-trigger`, `--output-dir`, `--catalog-db`, `--country`) — requerem restart/âmbito de processo, não são configuração de runtime do dashboard.
- **Modos legacy** (`--bot`, `--loop-hours`, `--fast`, `--scan-domain`).
- **Constantes técnicas** (timeouts HTTP, `limit=200`, `maxConcurrency=5`, threshold país 3, poll do scheduler 30s, `ExactMatchScore`/`AmbiguityMargin`) — valores de engenharia, não parâmetros operacionais.
- **`dispatcharrTest`** — estado derivado, não configuração.
- **Extras Dispatcharr** — os campos `dispatcharr_match_threshold`, `dispatcharr_alias_file`, `dispatcharr_provider_priority` e `dispatcharr_auto_create_groups` são editáveis no Setup → Dispatcharr (DC-5f). **`dispatcharr_target_group_name`** é apenas mostrado em modo leitura: está persistido mas **não é aplicado** pelo sync actual (lacuna conhecida — o grupo de publicação vem do grupo do canal canónico). Ver secção "Dispatcharr (config + teste)".

## Scheduler / Scheduled Jobs

O separador **Scheduled Jobs** do dashboard (dentro do **Catálogo**) gere jobs persistentes na tabela SQLite `scheduled_jobs`. Cada job tem `Name` único, `CronExpression` de 5 campos, `ActionName`, `IsEnabled` e os campos observáveis `LastRunAtUtc` / `NextRunAtUtc` / `LastResult`. O `ScheduledJobRunner` calcula o próximo tick a partir da expressão e é o único componente que dispara as acções.

No formulário de criação, os controlos de frequência (Todos os dias / A cada N horas / Semanal / Manual) são apenas um assistente que preenche a expressão Cron (5 campos); o único valor persistido é a expressão Cron.

### Overrides de discovery por job (DC-9 / DC-D2)

Cada job pode definir os seus próprios parâmetros de discovery (`keyword`, `minHistoryHours`, `historyHours`, `maxStreams`), aplicados como **overrides sobre a configuração global** (`app_settings.json#discovery`). A precedência é:

```
efetivo = DiscoverySettings.Load().WithOverrides(overrides_do_job)
```

- **Sem overrides** (ou campos ausentes/null), o job **herda a global**. A configuração global mantém-se a base/fallback para a CLI, para os runs manuais sem overrides e para os jobs sem overrides.
- Os overrides são persistidos na coluna `scheduled_jobs.DiscoveryJson` (JSON camelCase, nullable); campos ausentes no JSON herdam a global. Um `DiscoveryJson` inválido é ignorado (cai em `null`, herda a global) e **nunca impede o run**.
- Precedência por campo: um override válido substitui o valor global; valores fora dos intervalos (`minHistoryHours` fora de `[0, MaxValidHistoryHours]`, `historyHours` fora de `[1, 1440]`, `maxStreams < 1`) são ignorados (herdam a global). A normalização de janela invertida (`Min > History` → `Min = 0`) mantém-se após os overrides.
- Na UI (separador **Scheduled Jobs**), os campos de overrides são opcionais; em branco herdam a predefinição e a pré-visualização mostra o **efetivo**. A acção `telegramMaintainRun`/`telegramRun` usa os overrides do job quando existem; sem eles, o comportamento é idêntico ao anterior (config global).

A listagem (`GET /api/catalog/scheduled-jobs`) e o retorno de `POST` incluem um objecto `discovery` (os overrides persistidos, `null` quando não há) e um `effectiveDiscovery` (o default herdado já resolvido). O `POST` aceita `keyword`, `minHistoryHours`, `historyHours` e `maxStreams` opcionais; ausentes/null significa sem override.

O runner só é arrancado quando `--web` é passado (bootstrap do `ScheduledAutomationHost`), pelo que o scheduler depende do dashboard estar activo no processo. As duas acções Telegram (`telegramRun` / `telegramMaintainRun`) e a vista "Live Run" estão descritas em **Live Run Monitor** §"Arranque agendado (sem `StartAtUtc`)"; esta secção é a referência canónica para a API, o cron e as restantes acções.

### Endpoints

| Método e path | Comportamento |
|---|---|
| `GET /api/scheduled-actions` | Lista as acções registadas no registry do scheduler (contrato abaixo). Sem registry ligado devolve `[]`. |
| `GET /api/catalog/scheduled-jobs` | Lista todos os jobs persistidos. |
| `POST /api/catalog/scheduled-jobs` | Cria ou actualiza (upsert) um job pelo `Name`. |
| `PUT /api/catalog/scheduled-jobs/{id}/enabled` | Liga/desliga o job (`{ "isEnabled": true|false }`). |
| `DELETE /api/catalog/scheduled-jobs/{id}` | Elimina o job. |

Semântica HTTP:

- payload válido → `200` com o job (POST/PUT) ou `{ "deleted": true, "id": … }` (DELETE);
- erro de validação → `400` com corpo `{ "error": "…" }`;
- recurso inexistente → `404` com corpo `{ "error": "…" }`.

**Upsert pelo `Name`.** O `POST` procura um job com o mesmo `Name`: se existir, actualiza `cronExpression`/`actionName`/`isEnabled` e recalcula `nextRunAtUtc`; se não existir, cria um novo. Um `Name` diferente cria sempre um job novo — o `POST` não faz update por `id`.

**Validação de `ActionName`.** Com o scheduler ligado (o `--web` arranca o `ScheduledAutomationHost`), o `ActionName` é validado contra as acções registadas; um nome desconhecido devolve `400`. Sem registry ligado (contexto standalone, ex.: `--web` sem scheduler) não há validação de `ActionName` — mantém-se o campo livre retrocompatível.

### Contrato de `GET /api/scheduled-actions`

Devolve um array de objectos (antes era `string[]` com apenas os nomes):

```json
[
  {
    "name": "discoverM3u",
    "description": "Descoberta M3U8 por pesquisa web …",
    "capabilities": "Output",
    "requiresTelegram": false,
    "requiresDispatcharr": false
  }
]
```

- `capabilities` é a representação textual de `ScheduledActionCapabilities` (`None`, `Output`, `Catalog`, `Telegram`, `Dispatcharr`), podendo combinar quando a acção exige mais do que uma.
- `requiresTelegram` / `requiresDispatcharr` são atalhos booleanos que indicam se a acção depende da sessão Telegram autenticada / do Dispatcharr activo.

### Expressão cron (5 campos)

O parser (`CronExpression`) aceita **exactamente 5 campos**, na ordem `minuto hora dia-do-mês mês dia-da-semana`:

```
* * * * *
│ │ │ │ │
│ │ │ │ └── dia da semana (0-6, 0=Domingo)
│ │ │ └──── mês (1-12)
│ │ └────── dia do mês (1-31)
│ └──────── hora (0-23)
└────────── minuto (0-59)
```

- Intervalos válidos: minuto `0-59`; hora `0-23`; dia-do-mês `1-31`; mês `1-12`; dia-da-semana `0-6` (`0` = Domingo).
- Operadores: `*` (wildcard; `?` é aceite e equivalente a `*`), listas `a,b`, ranges `a-b`, steps `*/n`, `a-b/n` e `a/n`.
- **Não** suporta `L`, `W`, `#` nem nomes (`JAN`, `MON`) — só números. Timezone: **UTC**.
- **6 campos com segundos NÃO são suportados**: o parser rejeita e devolve `Cron deve ter 5 campos, recebido 6.`
- Semântica dia-do-mês vs dia-da-semana: se ambos os campos estiverem restringidos → **OU**; se um estiver em wildcard → **E** (semântica cron padrão).

Exemplos: `0 8 * * *` (todos os dias às 08:00 UTC); `0 */6 * * *` (de 6 em 6 horas); `30 2 * * 1` (segunda-feira às 02:30 UTC).

### Acções registadas

| `ActionName` | Descrição | Capabilities |
|---|---|---|
| `discoverM3u` | Descoberta M3U8 por pesquisa web (`M3uCrawlerService`) seguida de validação; publica as streams funcionais em `<output-dir>/playlist.m3u`. O termo e o limite vêm de `ScheduledActionOptions`. Substitui a playlist funcional e faz pedidos HTTP externos. | `Output` |
| `validatePlaylist` | Re-testa todas as streams de `<output-dir>/playlist.m3u` e reescreve o ficheiro mantendo apenas as que respondem. Se a playlist estiver ausente ou vazia, não a esvazia. Faz pedidos HTTP externos (probes). | `Output` |
| `generatePlaylist` | Compõe `<output-dir>/playlist.m3u` a partir de uma `OrderingList` do catálogo canónico. O nome do job `generatePlaylist:<id>` seleciona a lista; sem id válido usa a primeira lista (regista fallback). Não faz pedidos externos. | `Catalog` + `Output` |
| `syncDispatcharr` | Sincroniza `<output-dir>/playlist.m3u` com o Dispatcharr via `DispatcharrSyncCoordinator`. Respeita `dispatcharr_enabled` e `dispatcharr_dry_run`; decisões ambíguas nunca são aplicadas automaticamente. Só chama a API Dispatcharr se activo e fora de dry-run. | `Dispatcharr` |
| `telegramRun` | Ciclo Telegram (descoberta + validação) via `RunCoordinator`, com a configuração de Discovery persistida. Publica `playlist.m3u` (canónico, input do Dispatcharr) e o intermédio `playlist_temp.m3u`; mantém `telegram_playlist_<timestamp>.m3u` como histórico/técnico. Escreve também os relatórios e corre o sync Dispatcharr se activo. Requer sessão Telegram autenticada. | `Telegram` |
| `telegramMaintainRun` | Ciclo Telegram em modo manutenção: re-testa `playlist.m3u`, preserva streams working/retryable e incorpora novas descobertas em `playlist.m3u`; o `RunPublicationService` produz o intermédio `playlist_temp.m3u` (normalizado/deduplicado) e o canónico final. Requer sessão Telegram autenticada. | `Telegram` |

### Cron inválido num job persistido

Um cron inválido gravado em `scheduled_jobs` é neutralizado de forma segura no tick: o job não executa, `LastResult` fica `invalid-cron:<expr>` e `NextRunAtUtc` fica `null`; o tick continua e os restantes jobs correm. Reconfigurar o job (upsert no dashboard) recalcula o `NextRunAtUtc` e re-arma o agendamento.

### Formulário: frequência simples e cron manual

O formulário de criação/actualização inclui um auxiliar de frequência/hora que compõe a expressão de 5 campos; o campo de cron manual continua disponível para expressões arbitrárias. A validação da expressão acontece no `POST` (`400` com corpo `{error}`) e no upsert do catálogo.

### Limitação operacional conhecida (processo)

Resolvida (W-LIFECYCLE-IMPLEMENTATION, 2026-10-02): o processo é residente com `--web`, independentemente de `--loop-hours`. Ver § "Processo residente e shutdown" abaixo.

## Processo residente e shutdown

### Residência do processo

Com `--web`, o processo é residente: Dashboard, Scheduler (`ScheduledJobRunner`) e RunCoordinator (`LiveRunHost`) partilham o MESMO processo. Depois de um ciclo Telegram one-shot, o processo continua vivo e o Dashboard/Scheduler continuam disponíveis para execuções agendadas (cron) e manuais (botão "Run now").

Cenários de lifecycle:

| Comando | Ciclo Telegram | Processo termina? |
|---|---|---|
| `--telegram` (sem `--web`) | 1 ciclo | **Sim** — one-shot preservado (COMPORTAMENTO CLI) |
| `--telegram --web` (sem `--telegram-maintain`) | 1 ciclo | **Não** — residente |
| `--telegram-maintain --web` (sem `--loop-hours`) | 1 ciclo (se READY) | **Não** — residente |
| `--telegram --web --loop-hours N` | Ciclos a cada N horas | **Não** — residente pelo loop (semântica inalterada) |
| `--web` (sem `--telegram`) | — | **Não** — residente (dashboard standalone) |
| `--telegram --web` + lifecycle não READY | **não acontece** | **Não** — residente com Dashboard disponível para Setup |

Às 4 situações residentes corresponde um único padrão em `Program.cs`, extraído no helper `AwaitResidentDashboardAsync(webTask, automationHost, …)`, que espera pelo `webTask` (que termina quando o token de processo for cancelado ou o listener colapse) e, no fim, faz `Dispose()` do host do Scheduler. O `TestTempDb`-equivalente de teste é `ProgramLifecycleResidencyTests`.

### Shutdown coerente

O processo tem um `CancellationTokenSource` partilhado (`processCts`) criado no arranque e propagado ao `HttpListener` do Dashboard; os sinais documentados são:

- **Ctrl+C / SIGINT:** capturado por `Console.CancelKeyPress`; `e.Cancel = true` e inicio do shutdown ordenado.
- **SIGTERM / `docker stop` / systemd:** capturado via `AppDomain.CurrentDomain.ProcessExit`.

A sequência de shutdown é sempre a mesma:

1. **Sinalizar:** `processCts.Cancel()` — o token global é cancelado; novas execuções agendadas/manuais são bloqueadas.
2. **Scheduler:** `automationHost.StopAsync()` — `CancellationTokenSource` interno do runner é cancelado, propagando cancellation às acções em curso que o respeitem. Existe limite de drain documentado de **10 segundos** — se uma action em curso não respeitar cancellation, o shutdown termina por força e o log regista explicitamente `Scheduler não terminou dentro do limite` (nunca mascaredo como saída normal).
3. **Dashboard:** `WebDashboardService.StopDashboard()` — para o `HttpListener`, desbloqueia o `GetContextAsync()` pendente e permite que o loop termine sem registar qualquer `ObjectDisposedException` como falha.
4. **Sair:** `Main` juntamente com o `webTask` no caminho residente, faz `Dispose()` do scheduler host, devolve normalmente.

A ordem de sinalização é Única: se Ctrl+C e SIGTERM chegam em simultâneo, o shutdown corre só uma vez (contador interlocked).

Sem Generic Host, sem Kestrel, sem `IHostedService`: o mecanismo é nativo (`CancellationTokenSource`, `Console.CancelKeyPress`, `AppDomain.CurrentDomain.ProcessExit`) e não introduz uma nova abstracção de hosting. Preserva o invariante do `AGENTS.md` §2 — o `HttpListener` é arrancado no top-level de `Main`, e a respetiva autenticação com `--web-token` não é alterada.

### Papel de `--loop-hours` nesta wave

A semântica de `--loop-hours N` NÃO foi alterada nesta wave (nem o `docker-compose.yml` do repo, que continua com `--loop-hours 24`). É ainda um mecanismo de recorrência CLI válido e independente do novo mecanismo de residency:

- `--loop-hours N` mantém o **processo** vivo entre ciclos (não pelo Dashboard, mas pelo próprio `do/while` CLI).
- O Scheduler pode também agendar ciclos Telegram (`telegramRun` / `telegramMaintainRun`), mas **nunca em simultâneo** com uma execução CLI em curso — a proteção por CAS do `RunCoordinator` garante que só um ciclo corre de cada vez (409 `already-running` / `blocked:already-running`).

Uma wave posterior (**não esta**) poderá avaliar retirar `--loop-hours` do compose, deixando o Scheduler como único motor de cadência — só depois de validado em runtime, com um job Telegram persistido em `scheduled_jobs`.

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

- Build: `dotnet build m3uCrawler.sln --configuration Release --no-restore` → **0 errors**; o baseline tem warnings pré-existentes (analyzers xUnit) — não introduzir warnings novos.
- Testes: `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo` — referência operacional; **não** interpretar o número como propriedade permanente da arquitectura. Estado medido em **2026-10-02** (working tree de W-DASHBOARD): 2827 testes, com 1 falha pré-existente conhecida (observabilidade do dashboard, `WaveW6b2ObservabilityTests`) e 1 skipped; ver `docs/PROJECT_STATUS.md`.
- **Execução real (2026-10-02):** cadeia Telegram→candidate→playlist→Dispatcharr exercitada em runtime (imagem local de `fcd442e`), com janela `--min-history-hours 425 --history-hours 450` e Dispatcharr em **dry-run**; artefactos gerados (playlist M3U, relatórios Telegram, plano/relatório Dispatcharr). Números detalhados em `docs/PROJECT_STATUS.md` (não duplicados aqui).
- O runner descobre e executa todos os testes; não há testes que passem sem realmente exercitar o comportamento (detector, parser, validação por país com threshold/famílias/falsos-positivos, merge de manutenção).
- **Testes de comportamento JS (DC-8):** o IIFE do Dashboard servido em `GET /` é extraído e executado in-process por um harness **Jint** (`m3uCrawler.Tests/DashboardJs/`, dependência **só de teste**) com um shim DOM/Web mínimo — determinístico, sem browser nem servidor externo; complementa os testes de markup/string.
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

Directório de output = `--output-dir` (padrão `output`); em produção o Compose monta-o em `/opt/playlists` (ver `DEPLOYMENT.md`). A propriedade de cada artefacto é importante:

- `output/playlist.m3u` — **Playlist canónica final** (nome fixo). É escrita/reescrita atómicamente pelo `RunPublicationService` em todos os caminhos de publicação Telegram (ciclo único `--telegram`, manutenção `--telegram-maintain`, `POST /api/run/start` e `telegramRun` agendado) e pelas acções agendadas `discoverM3u`, `validatePlaylist` (in place) e `generatePlaylist`. É o input do `syncDispatcharr` / `--dispatcharr-sync` (default) e servida por `/api/playlist` (raw) e `/api/playlist/preview` (sanitizada). O código nunca a apaga; a manutenção nunca remove streams existentes só por não haver novos candidatos.
- `output/playlist_temp.m3u` — **Intermédio canónico normalizado/deduplicado**, escrito atómicamente pelo `RunPublicationService` **antes** do filtro de domínio, do country gate e da selecção de fontes. Contém o conjunto descoberto após dedup por URL e pode conter streams que a selecção rejeita no resultado final. É produzido em todos os caminhos de publicação Telegram (incluindo a manutenção); **não** é renomeado. Servido por `/api/playlist_temp` (raw) e `/api/playlist_temp/preview` (sanitizada) e listado em `/api/output/inventory`.
- `output/telegram_playlist_<timestamp>.m3u` e `output/telegram_report_<timestamp>.json` — Artefactos **históricos/técnicos** de um ciclo Telegram. O timestamped é produzido pelo caminho normal (ciclo único e `telegramRun` agendado) a partir do resultado final; o Dispatcharr **nunca** o consome (consome `playlist.m3u`). Quando o nome final coincide com o canónico (modo manutenção), não é escrito. A pesquisa M3U interactiva legacy escreve `playlist_<timestamp>.m3u`.
- `output/telegram_run_report.json` — `RunReport` da última execução (camelCase; sobrescrito a cada run em ambos os modos).
- `output/telegram_maintain_report.json` — Relatório adicional do ciclo de manutenção.
- `output/import_history.json` — Histórico persistente.
- `output/dispatcharr_selection_<timestamp>.json` — Artefacto da selecção de fontes (Wave 13-6), escrito pelo sync Dispatcharr **antes** do branch dry-run/apply (o dry-run também o produz). As URLs são sanitizadas (`CredentialSanitizer.SanitizeUrl`); nunca contém credenciais.

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

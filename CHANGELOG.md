# 📈 Changelog - m3uCrawler

Todas as mudanças notáveis neste projeto serão documentadas neste arquivo.

O formato é baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.0.0/),
e este projeto adere ao [Semantic Versioning](https://semver.org/lang/pt-BR/).

## [Unreleased]

### ✨ Adicionado
- **Pipeline Telegram completo**: descoberta de candidatos → aquisição de conteúdo → detecção M3U → parsing → validação por país → extracção de streams → teste de streams → relatório.
- **`M3uCandidateDetector`**: descoberta independente de keyword (URLs `.m3u`/`.m3u8`, anexos, conteúdo `#EXTM3U`, URLs plausíveis sem extensão).
- **`CandidatePlaylist`**: modelo de candidato com `RequiresContentVerification` para URLs sem extensão sujeitos a verificação de conteúdo.
- **`M3uParserService`**: parser M3U centralizado que preserva `OriginalExtInf`, `Title`, `Group`, `Logo` e distingue playlists de canais de master HLS.
- **`AnalyzePlaylist` (CountryChannelValidator)**: validação por país baseada nos **títulos `#EXTINF`**, com **threshold 3**, famílias canónicas, variantes colapsadas (`RTP1` ≡ `RTP 1`) e protecção contra falsos positivos de aliases curtos.
- **`RunReport`**: relatório detalhado da execução (`MessagesAnalyzed`, `CandidatesFound`, `PlaylistsDownloaded`, `PlaylistsInvalid`, `CountryMatches`, `PlaylistsRejected`, `ChannelsRecognized`, `StreamsExtracted`, `StreamsTested`, `StreamsWorking`, `StreamsFailed`, `RejectionReasons`, `DiscoveredPlaylists`).
- **`telegram_run_report.json`**: persistido em `output/` em ambos `--telegram` e `--telegram-maintain`.
- **Dashboard**: novos endpoints `/api/run-report` e `/api/discovered-playlists`; secções de diagnóstico da última execução e de últimas playlists descobertas.
- **Alinhamento do `/api/country/validate`** com o critério rigoroso do pipeline (`AnalyzePlaylist`, threshold 3, `recognizedChannelCount`, `threshold`).
- **Suporte a Xtream Codes**: `M3uCandidateDetector` reconhece URLs Xtream de servidor (`/live/USER/PASS/...`) — resolvendo-as para `get.php?username=...&password=...&type=m3u_plus` — e URLs de playlist Xtream (`get.php?type=m3u_plus`). Ambos entram no mesmo pipeline (`RequiresContentVerification` + gate `#EXTM3U`), sem segunda pipeline paralela e sem depender de keyword.
- **`CredentialSanitizer`**: sanitiza URLs com credenciais (Xtream `user:password@`, `username/password/token` em query, segmentos `user/pass` em `/live/`/`/movie/`/`/series/`) para `***`. Garantia de que passwords **nunca** aparecem em logs, `RunReport` (`DiscoveredPlaylists.Name`, `RejectionReasons`) ou dashboard.

### 🔧 Melhorado
- **`--telegram-maintain`** preserva `playlist.m3u` quando não existem novos candidatos (`MergeStreams` com `freshStreams` vazio devolve `stillWorkingMain`).
- Descoberta de playlists com anexo cujo filename é `.m3u`/`.m3u8` ou cujo conteúdo começa por `#EXTM3U` (mesmo sem extensão).
- URLs HTTP plausíveis sem extensão (`/getplaylist?...`) são inspeccionados e só avançam se o conteúdo HTTP for `#EXTM3U`.
- `ImportHistoryEntry` inclui métricas de discovery (mensagens, candidatos, playlists, streams).
- Documentação (`m3uCrawler/README.md`, `CHANGELOG.md`, `README.md` raiz) actualizada para reflectir o estado real.

### 🐛 Corrigido
- Falso positivo de aliases curtos: `SIC`/`TVI` já não correspondem a `basics`/`atvinew` (matching por tokens, não por `Contains` sobre o conteúdo bruto).
- Duplicação de parsing M3U (centralizado em `M3uParserService`; removido `ExtractM3u8FromTelegramDocumentAsync`).
- `--telegram-maintain` sem novos candidatos não apaga streams existentes.
- **Credenciais Xtream em logs**: a implementação anterior imprimia servidor, username e password no terminal; agora toda a representação destinada a logs/relatórios/dashboard é sanitizada (`CredentialSanitizer`) e o pipeline integra Xtream de forma segura.
- **Credenciais em consola (tester e listagem de streams)**: `M3uTesterService` e `Program.cs` (listagem de streams funcionais e templates de scan-domain) usam `CredentialSanitizer.SanitizeUrl` em todas as impressões, de modo que passwords Xtream nunca aparecem no terminal.
- **Credenciais em JSONs de relatório/diagnóstico**: `PlaylistManagerService.SaveToJsonReport` sanitiza o `Url` de cada stream antes de serializar; a playlist M3U funcional (`SaveToM3uPlaylist`) preserva as URLs reais para reprodução.
- **Credenciais no dashboard**: a pré-visualização HTML usa `GET /api/playlist/preview` e `GET /api/playlist_temp/preview` (sanitizados via `CredentialSanitizer.SanitizeM3uContent`); os endpoints funcionais `/api/playlist` e `/api/playlist_temp` mantêm-se para download explícito.
- **Distinção explícita artefacto funcional vs diagnóstico**: a playlist M3U funcional contém URLs reais (necessárias para Xtream); logs, JSONs de relatório, `RunReport` e pré-visualização do dashboard são sempre sanitizados.
- **Acesso ao dashboard**: por defeito o `HttpListener` escuta em todas as interfaces sem autenticação — qualquer pessoa na rede podia obter a playlist Xtream funcional via `GET /api/playlist`. Adicionada protecção opcional por token partilhado (`--web-token`): quando configurado, todos os endpoints (incluindo `/api/playlist*`) exigem `Authorization: Bearer <token>` ou `?token=<token>` (comparação em tempo constante, `401` caso contrário). Sem token configurado, o comportamento mantém-se aberto para compatibilidade com uso local.
- **Grupos Dispatcharr com nome duplicado (case-insensitive)**: o `ChannelMatcher` (`Services/Matching/ChannelMatcher.cs:68`) lançava `ArgumentException: An item with the same key has already been added. Key: portugal` quando o Dispatcharr devolvia ≥2 grupos cujo `Name` colapsava para a mesma chave normalizada (`Trim().ToLowerInvariant()`). Substituído o `ToDictionary(..., OrdinalIgnoreCase)` por um `GroupNameIndex` que distingue chaves únicas de chaves ambíguas; para chaves ambíguas, **nenhum** `Id` é seleccionado e a entrada é propagada em `MatchPlan.AmbiguousGroups` / `SyncReport.AmbiguousGroups` (campo `AmbiguousGroups` também adicionado a `SyncReportCounts`). A mesma política é aplicada em `DispatcharrSyncService.ApplyAsync` (substituição do `g.First().Id` por uma construção que exclui nomes ambíguos do dicionário de aplicação), garantindo que `dry_run=false` também não selecciona arbitrariamente.

### 📦 Deployment
- **Migração para Docker Compose**: `docker-compose.yml` passa a ser a fonte de verdade do deployment. Imagem `ghcr.io/ginjeira/m3ucrawler:latest` (pull-only, sem `build:` no servidor), `container_name: m3ucrawler`, `restart: unless-stopped`, comando completo com `--history-hours 360`, bind mounts absolutos para `/opt/m3ucrawler/runtime-data`. Documentação consolidada em `DEPLOYMENT.md`, `OPERATIONS.md`, `ROADMAP.md`, `AGENTS.md`.
- **`m3uCrawler/runtime-data/` no repositório** permanece como placeholder commitado (apenas `channel-indicators.json`, `countries/pt.json`, `.gitkeep`). Em produção é substituído por bind mount para `/opt/m3ucrawler/runtime-data` no host.
- Cobertura: detector (URLs/anexos/conteúdo/inspecção, **Xtream server + get.php**), parser (EXTINF, master HLS), validação por país (threshold, famílias, variantes, falsos positivos, normalização), merge de manutenção, **sanitização de credenciais** (userinfo, query, path Xtream, combinações, `SanitizeM3uContent`, `SaveToJsonReport` não persiste passwords, `SaveToM3uPlaylist` preserva URLs funcionais), **autenticação do dashboard** (`IsAuthorized`: sem token, header Bearer, query `?token=`, rejeição de token errado/parcial, credenciais Xtream não aceites), carregamento de listas por país, baseline legacy.

## [v2.1.0] - 2025-11-02

### ✨ Adicionado
- **Argumentos de linha de comando**: Suporte completo para parâmetros CLI
- **--max-streams N**: Definir limite de streams para testar (1-1000)
- **--fast / --high-performance**: Modo alta performance (20 conexões paralelas)
- **--help / -h**: Sistema de ajuda integrado
- **Configuração flexível**: Limite padrão aumentado para 500 streams
- **Parsing inteligente**: Separação correcta entre termo de pesquisa e opções
- **Scripts auxiliares**: test_simple.ps1 e run_advanced.bat
- **Detecção automática**: Console interactivo vs linha de comando

### 🔧 Melhorado
- **UX drasticamente melhorada**: Interface muito mais amigável
- **Performance configurável**: 10-20 conexões paralelas conforme modo
- **Validação robusta**: Tratamento de argumentos malformados
- **Documentação expandida**: Exemplos práticos e casos de uso
- **Flexibilidade total**: De 5 streams (teste) até 1000 (produção)

### 🔧 Corrigido
- **Console.ReadKey**: Não bloqueia quando entrada é redireccionada
- **Argumentos CLI**: Parsing correcto de termos vs opções
- **Timeout melhorado**: Redução de falsos negativos

### 📖 Exemplos de Uso
```bash
# Teste rápido
dotnet run -- "demo test" --max-streams 5

# Uso normal
dotnet run -- "iptv portugal" --max-streams 200

# Alta performance
dotnet run -- "worldwide streams" --fast --max-streams 1000

# Ajuda
dotnet run -- --help
```

## [v1.1.0] - 2025-11-02

### ✨ Adicionado
- **Múltiplas fontes de pesquisa**: 15+ fontes diferentes implementadas
- **Repositórios GitHub**: IPTV-ORG, Free-TV, M3U Filter Samples
- **APIs públicas**: StreamWeasels, IPTV Cat, Pluto TV
- **Motores alternativos**: DuckDuckGo, SearX, StartPage
- **Fontes regionais**: Portugal, Brasil, Chile
- **Extração HTML avançada**: Links de páginas web especializadas
- **Logging detalhado**: Rastreamento por fonte durante pesquisa
- **Regex melhorada**: Captura mais precisa de URLs M3U8

### 🔧 Melhorado
- **Performance**: Agora encontra 22.000+ URLs vs 100 da versão anterior
- **Eficiência**: Tempo médio reduzido de 961ms para 753ms
- **Resiliência**: Falha de uma fonte não afecta as outras
- **Headers HTTP**: Mais realistas para evitar bloqueios
- **Diversificação**: Não depende mais apenas do iptv-org.github.io

### 🐛 Corrigido
- **Problema principal**: "Nenhuma URL M3U8 encontrada" resolvido
- **Anti-bot**: Contornado com fontes alternativas
- **Timeout**: Melhor tratamento de erros de conexão
- **Regex**: Captura URLs com parâmetros de query

### 📊 Estatísticas v1.1.0
- **URLs encontradas**: 22.033 (vs 0-100 anterior)
- **URLs únicas**: 11.039 após filtragem
- **Taxa de sucesso**: 59% (59/100 streams funcionais)
- **Fontes activas**: 15+ implementadas
- **Performance**: 23x mais URLs descobertos

## [v1.0.0] - 2025-11-01

### Adicionado
- 🔍 Pesquisa automática de streams M3U8 usando múltiplos motores de busca
- 🧪 Sistema de teste paralelo de conectividade dos streams
- 💾 Geração automática de playlists M3U com apenas streams funcionais
- 📊 Relatórios detalhados em formato JSON
- 🎨 Interface colorida no console com emojis e feedback visual
- ⚙️ Sistema de configuração flexível via arquivos JSON
- 📝 Sistema de logging detalhado
- 🚀 Scripts de execução para Windows (PowerShell e CMD)
- 📚 Documentação completa com exemplos de uso
- 🧹 Script de limpeza de arquivos temporários

### Recursos Técnicos
- Processamento assíncrono e paralelo para máxima eficiência
- Tratamento robusto de erros e timeouts
- Suporte a múltiplos formatos de saída
- Arquitectura modular com separação de responsabilidades
- Compatibilidade com .NET 9.0

### Dependências
- HtmlAgilityPack para web scraping
- System.Text.Json para serialização
- .NET 9.0 como framework base

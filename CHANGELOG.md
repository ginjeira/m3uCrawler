# 📈 Changelog - m3uCrawler

Todas as mudanças notáveis neste projeto serão documentadas neste arquivo.

O formato é baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.0.0/),
e este projeto adere ao [Semantic Versioning](https://semver.org/lang/pt-BR/).

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
- **Resiliência**: Falha de uma fonte não afeta as outras
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
- **Fontes ativas**: 15+ implementadas
- **Performance**: 23x mais URLs descobertas

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
- Arquitetura modular com separação de responsabilidades
- Compatibilidade com .NET 9.0

### Dependências
- HtmlAgilityPack para web scraping
- System.Text.Json para serialização
- .NET 9.0 como framework base

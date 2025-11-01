# 🎯 m3uCrawler

<div align="center">

![C#](https://img.shields.io/badge/c%23-%23239120.svg?style=for-the-badge&logo=c-sharp&logoColor=white)
![.Net](https://img.shields.io/badge/.NET-5C2D91?style=for-the-badge&logo=.net&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-0078D6?style=for-the-badge&logo=windows&logoColor=white)
![Linux](https://img.shields.io/badge/Linux-FCC624?style=for-the-badge&logo=linux&logoColor=black)

**Um crawler avançado em C# para descobrir, testar e organizar streams M3U8 funcionais**

[🚀 Quick Start](#-quick-start) • [📖 Documentação](#-documentação) • [🤝 Contribuir](#-contribuir) • [📄 Licença](#-licença)

</div>

---

## ✨ Funcionalidades

- 🔍 **Pesquisa Inteligente** - Busca automática em múltiplos motores de pesquisa
- 🧪 **Teste Paralelo** - Verificação rápida e eficiente de conectividade
- 💾 **Playlist Automática** - Gera arquivos M3U apenas com streams funcionais
- 📊 **Relatórios Detalhados** - Análises completas em formato JSON
- ⚡ **Alto Performance** - Processamento assíncrono e paralelo
- 🎨 **Interface Amigável** - Console colorido com feedback visual
- ⚙️ **Configurável** - Personalizações via arquivos JSON

## 🚀 Quick Start

### Instalação
```bash
git clone https://github.com/yourusername/m3uCrawler.git
cd m3uCrawler/m3uCrawler
dotnet restore
dotnet build
```

### Uso Básico
```bash
# Execução rápida
dotnet run -- "sports live"

# Modo interativo
dotnet run

# Windows PowerShell
.\run.ps1 "news channels"
```

## 📊 Exemplo de Saída

```
=== m3uCrawler - Pesquisador de Streams M3U8 ===

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
   • Tempo médio: 1025ms

✨ Arquivos gerados:
   • output/playlist_20251101_153000.m3u
   • output/report_20251101_153000.json
```

## 🏗️ Arquitetura

```
📁 m3uCrawler/
├── 📁 Models/
│   └── M3uStream.cs              # Modelo de dados
├── 📁 Services/
│   ├── M3uCrawlerService.cs      # Motor de pesquisa
│   ├── M3uTesterService.cs       # Testador de streams
│   ├── PlaylistManagerService.cs # Gerador de playlists
│   └── LoggingService.cs         # Sistema de logs
├── Program.cs                    # Ponto de entrada
├── config.json                   # Configurações
└── 📁 output/                    # Arquivos gerados
```

## ⚙️ Configuração

Personalize o comportamento editando `config.json`:

```json
{
  "SearchSettings": {
    "MaxResults": 100,
    "MaxConcurrency": 10,
    "RequestTimeout": 30
  },
  "SearchEngines": [
    {
      "Name": "Google",
      "Url": "https://www.google.com/search?q=filetype:m3u8+{0}",
      "Enabled": true
    }
  ]
}
```

## 📖 Documentação

- 📚 [Guia Completo](m3uCrawler/README.md) - Documentação detalhada
- 🔧 [Exemplos de Uso](m3uCrawler/EXEMPLOS.md) - Casos práticos
- 📝 [Changelog](CHANGELOG.md) - Histórico de versões

## 🛠️ Requisitos

- .NET 9.0 ou superior
- Windows, Linux ou macOS
- Conexão com a internet

## 🤝 Contribuir

Contribuições são muito bem-vindas! Veja [CONTRIBUTING.md](CONTRIBUTING.md) para detalhes.

### 🎯 Como ajudar:
- 🐛 Reportar bugs
- 💡 Sugerir funcionalidades
- 🔧 Implementar melhorias
- 📚 Melhorar documentação

## ⚖️ Aviso Legal

⚠️ **IMPORTANTE**: Este software é destinado apenas para fins educacionais e de pesquisa.

- ✅ Use apenas para conteúdo legal e autorizado
- ✅ Respeite direitos autorais e termos de serviço
- ✅ Obtenha permissão antes de acessar streams
- ❌ O desenvolvedor não se responsabiliza pelo uso indevido

## 📄 Licença

Este projeto está licenciado sob a Licença MIT - veja [LICENSE](LICENSE) para detalhes.

## 🌟 Agradecimentos

- [HtmlAgilityPack](https://html-agility-pack.net/) - Web scraping
- [.NET Foundation](https://dotnetfoundation.org/) - Framework
- Comunidade open source por inspiração e feedback

---

<div align="center">

**⭐ Se este projeto foi útil, considere dar uma estrela!**

[🐛 Reportar Bug](../../issues/new?template=bug_report.md) • [💡 Sugerir Feature](../../issues/new?template=feature_request.md) • [❓ Fazer Pergunta](../../discussions)

*Desenvolvido com ❤️ em C# - Novembro 2025*

</div>

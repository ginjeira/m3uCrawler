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

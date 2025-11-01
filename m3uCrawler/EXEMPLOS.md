# Exemplo de uso do m3uCrawler

## Como testar o projeto

### 1. Teste simples
```powershell
# Executar o script PowerShell
.\run.ps1 "sports"

# Ou executar diretamente
dotnet run -- "sports"
```

### 2. Teste interativo
```powershell
# Executar sem argumentos para modo interativo
.\run.ps1

# Ou
dotnet run
```

### 3. Estrutura dos arquivos gerados

#### Playlist M3U (exemplo)
```m3u
#EXTM3U
#PLAYLIST:m3uCrawler - Generated on 2025-11-01 15:30:00

#EXTINF:-1 tvg-name="Sports Channel" tvg-logo="" group-title="Sports",Sports Channel
https://example.com/sports.m3u8

#EXTINF:-1 tvg-name="News Channel" tvg-logo="" group-title="News",News Channel
https://example.com/news.m3u8
```

#### Relatório JSON (exemplo)
```json
{
  "generatedAt": "2025-11-01T15:30:00",
  "totalStreams": 10,
  "workingStreams": 3,
  "nonWorkingStreams": 7,
  "averageResponseTime": 1250.5,
  "streams": [
    {
      "url": "https://example.com/sports.m3u8",
      "title": "Sports Channel",
      "group": "Sports",
      "logo": "",
      "isWorking": true,
      "lastTested": "2025-11-01T15:30:00",
      "responseTime": 1200.0
    }
  ]
}
```

## Funcionalidades implementadas

✅ **Pesquisa automática** de streams M3U8  
✅ **Teste de conectividade** de cada stream  
✅ **Geração de playlist** M3U funcional  
✅ **Relatório detalhado** em JSON  
✅ **Interface de linha de comando** simples  
✅ **Processamento paralelo** para eficiência  
✅ **Logs coloridos** no console  
✅ **Scripts de execução** (.ps1 e .bat)  

## Próximas melhorias possíveis

- 🔄 **Cache de resultados** para evitar retestes
- 🎯 **Filtros por categoria** de conteúdo
- 🌐 **Interface web** opcional
- 📱 **Suporte a outros formatos** (m3u, pls)
- 🔍 **Pesquisa em sites específicos**
- ⚙️ **Configurações avançadas** via arquivo JSON

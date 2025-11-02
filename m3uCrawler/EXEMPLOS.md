# 📖 Exemplos de Uso - m3uCrawler v2.1

## 🚀 Uso Básico

### 1. Execução Simples
```powershell
# Modo interativo
.\run.ps1

# Via linha de comando com termo de pesquisa
dotnet run -- "iptv portugal"
```

### 2. Ajuda e Informações
```powershell
# Mostrar todas as opções disponíveis
dotnet run -- --help
```

## ⚙️ Opções de Linha de Comando

### 3. Limitando Número de Streams
```powershell
# Testar apenas 50 streams (rápido)
dotnet run -- "iptv" --max-streams 50

# Testar 1000 streams (máximo permitido)  
dotnet run -- "tv channels" --max-streams 1000

# Configuração balanceada
dotnet run -- "streaming" --max-streams 300
```

### 4. Modo Alta Performance
```powershell
# Modo rápido - 20 conexões paralelas
dotnet run -- "iptv portugal" --fast

# Equivalente ao --fast
dotnet run -- "tv brasil" --high-performance

# Combinando opções para máxima eficiência
dotnet run -- "canais tv" --fast --max-streams 500
```

### 5. Exemplos Práticos Completos
```powershell
# Busca rápida para desenvolvimento/teste
dotnet run -- "demo test" --max-streams 10

# Busca média para uso pessoal
dotnet run -- "iptv free" --max-streams 200

# Busca extensa para coleção completa
dotnet run -- "worldwide iptv" --fast --max-streams 1000
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

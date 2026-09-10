# 🎯 m3uCrawler v2.1 - Stream Limit Enhancement Summary

> **DEPRECATED** — este documento descreve cenários e comportamento da versão pré-pipeline Telegram (v2.1) e é mantido apenas para referência histórica. O pipeline actual é Telegram-first (`--telegram portugal --telegram-maintain`), e o `--max-streams` aplica-se de forma diferente da descrita aqui.
>
> Para o comportamento actual consultar `m3uCrawler/README.md` e `DEPLOYMENT.md` §9.1 (argumentos CLI suportados).

## 📊 What Changed: From 100 to Unlimited Streams

### 🔍 **Why Only 100 Streams Before?**

The previous version was limited to **100 streams by default** for several practical reasons:

1. **⏱️ Time Efficiency**: Testing 100 streams takes ~2-5 minutes with 10 parallel connections
2. **🌐 Rate Limiting**: Respectful usage to avoid triggering anti-bot measures  
3. **📈 Quality Focus**: 59% success rate means ~59 working streams from 100 tests
4. **💾 Resource Management**: Balanced memory and network usage

### 🚀 **New Capabilities in v2.1**

Now you can customize stream limits from **1 to 1000 streams** with multiple configuration methods:

## 🎛️ Configuration Methods

### 1. **Command Line Arguments** (New!)
```bash
# Quick test - 5 streams
dotnet run -- "demo test" --max-streams 5

# Normal usage - 200 streams  
dotnet run -- "iptv portugal" --max-streams 200

# High performance - 1000 streams
dotnet run -- "worldwide streams" --fast --max-streams 1000

# Show all options
dotnet run -- --help
```

### 2. **Interactive Prompt** (Enhanced)
```
Quantos streams testar? (padrão: 500, máx: 1000): 300
🎯 Configurado para testar 300 streams
```

### 3. **Configuration File** (Updated)
```json
{
  "SearchSettings": {
    "MaxResults": 500,        // Increased from 100
    "MaxConcurrency": 15,     // Increased from 10
    "HighPerformanceMode": false
  }
}
```

### 4. **Performance Modes** (New!)
```bash
# Standard mode (10-15 parallel connections)
dotnet run -- "iptv streams" --max-streams 500

# High performance mode (20 parallel connections)
dotnet run -- "iptv streams" --fast --max-streams 1000
```

## 📈 Performance Comparison

| Stream Count | Time (Standard) | Time (--fast) | Success Rate | Working Streams |
|--------------|----------------|---------------|--------------|-----------------|
| **5**        | ~30 seconds    | ~20 seconds   | 60%          | ~3 streams      |
| **100**      | ~3-5 minutes   | ~2-3 minutes  | 59%          | ~59 streams     |
| **500**      | ~12-20 minutes | ~8-15 minutes | 55-65%       | ~275-325 streams|
| **1000**     | ~25-40 minutes | ~15-30 minutes| 50-70%       | ~500-700 streams|

## 🎯 Use Cases & Recommendations

### 🔬 **Development/Testing**
```bash
# Quick validation
dotnet run -- "demo test" --max-streams 5
.\test_simple.ps1 quick
```

### 👤 **Personal Use**
```bash
# Balanced approach
dotnet run -- "iptv portugal" --max-streams 200
```

### 🏢 **Production/Collection**
```bash
# Maximum efficiency
dotnet run -- "worldwide iptv" --fast --max-streams 1000
```

### 🎮 **Interactive Exploration**
```bash
# Let user decide
dotnet run
# Then enter desired number at prompt
```

## 🛠️ New Helper Scripts

### **PowerShell Examples**
```powershell
# Test different scenarios
.\test_simple.ps1 help      # Show options
.\test_simple.ps1 quick     # 5 streams
.\test_simple.ps1 normal    # 100 streams
```

### **Batch Files**
```batch
# Windows users
run_advanced.bat help               # Show help
run_advanced.bat "iptv" --max-streams 300
```

## 🎉 Key Benefits of v2.1

### ✅ **Flexibility**
- **1-1000 streams**: Complete range for any use case
- **Multiple interfaces**: CLI, interactive, config file
- **Performance modes**: Standard vs high-performance

### ✅ **User Experience**
- **Intuitive commands**: `--max-streams 500 --fast`
- **Smart defaults**: 500 streams (up from 100)
- **Help system**: `--help` shows all options

### ✅ **Performance**
- **20 parallel connections**: In fast mode (vs 10 standard)
- **Optimized parsing**: Better argument handling
- **Console improvements**: No hanging on redirected input

### ✅ **Backward Compatibility**
- **Old commands still work**: Interactive mode unchanged
- **Configuration preserved**: config.json enhanced, not replaced
- **Scripts still functional**: run.ps1 and run.bat updated

## 🚀 Migration Guide

### **From v1.1 to v2.1**

**Old way (v1.1):**
```bash
dotnet run
# Then manually enter search term and accept 100 limit
```

**New way (v2.1):**
```bash
# Much more efficient
dotnet run -- "iptv portugal" --max-streams 500 --fast
```

**Configuration update:**
```json
// Old config.json
{
  "SearchSettings": {
    "MaxResults": 100,
    "MaxConcurrency": 10
  }
}

// New config.json  
{
  "SearchSettings": {
    "MaxResults": 500,
    "MaxConcurrency": 15,
    "HighPerformanceMode": false
  }
}
```

## 📚 Documentation Updates

- ✅ **README.md**: Updated with new CLI examples
- ✅ **EXEMPLOS.md**: Comprehensive usage scenarios  
- ✅ **CHANGELOG.md**: Detailed v2.1 feature list
- ✅ **Helper scripts**: PowerShell and batch examples
- ✅ **Built-in help**: `--help` command

## 🎯 Summary

**m3uCrawler v2.1** transforms the application from a fixed 100-stream limit to a **flexible, scalable solution** that can handle:

- 🚀 **Quick tests** (5 streams, 30 seconds)
- 📺 **Personal collections** (200-500 streams, 10-20 minutes)  
- 🌐 **Mass collection** (1000 streams, 15-30 minutes)
- ⚡ **High-performance mode** (20 parallel connections)

The **59% success rate remains consistent**, but now you have complete control over the volume and speed of stream discovery!

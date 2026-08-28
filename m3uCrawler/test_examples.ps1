# m3uCrawler Quick Test Script v2.1
# Exemplos de uso da nova versao com argumentos de linha de comando

param(
    [string]$Action = "help"
)

$ProjectDir = "p:\source\repos\m3uCrawler\m3uCrawler"

switch ($Action.ToLower()) {
    "help" {
        Write-Host "=== m3uCrawler v2.1 - Exemplos de Uso ===" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Opcoes disponiveis:" -ForegroundColor Yellow
        Write-Host "  quick    - Teste rapido (5 streams)" -ForegroundColor Green
        Write-Host "  normal   - Teste normal (100 streams)" -ForegroundColor Green  
        Write-Host "  large    - Teste extenso (500 streams)" -ForegroundColor Green
        Write-Host "  massive  - Teste massivo com alta performance (1000 streams)" -ForegroundColor Green
        Write-Host "  demo     - Demonstracao com argumentos especificos" -ForegroundColor Green
        Write-Host ""
        Write-Host "Uso: .\test_examples.ps1 [quick|normal|large|massive|demo]" -ForegroundColor White
        Write-Host ""
        Write-Host "Exemplo de comando manual:" -ForegroundColor Yellow
        Write-Host '  dotnet run -- "iptv portugal" --max-streams 200 --fast' -ForegroundColor Gray
    }
    
    "quick" {
        Write-Host "Teste Rapido - 5 streams" -ForegroundColor Green
        Set-Location $ProjectDir
        dotnet run -- "demo test" --max-streams 5
    }
    
    "normal" {
        Write-Host "Teste Normal - 100 streams" -ForegroundColor Green
        Set-Location $ProjectDir
        dotnet run -- "iptv free" --max-streams 100
    }
    
    "large" {
        Write-Host "Teste Extenso - 500 streams" -ForegroundColor Green
        Set-Location $ProjectDir
        dotnet run -- "iptv worldwide" --max-streams 500
    }
    
    "massive" {
        Write-Host "Teste Massivo - 1000 streams (alta performance)" -ForegroundColor Green
        Set-Location $ProjectDir
        dotnet run -- "streaming channels" --fast --max-streams 1000
    }
    
    "demo" {
        Write-Host "Demonstracao - Varios comandos" -ForegroundColor Green
        Set-Location $ProjectDir
        
        Write-Host "1. Mostrar ajuda:" -ForegroundColor Yellow
        dotnet run -- --help
        
        Write-Host "2. Teste rapido com argumentos:" -ForegroundColor Yellow
        dotnet run -- "demo" --max-streams 3
        
        Write-Host "Demonstracao concluida!" -ForegroundColor Green
    }
    
    default {
        Write-Host "Opcao invalida: $Action" -ForegroundColor Red
        Write-Host "Use: .\test_examples.ps1 help" -ForegroundColor Yellow
    }
}

# Script para executar o m3uCrawler
# Uso: .\run.ps1 "termo de pesquisa"

param(
    [string]$SearchTerm = ""
)

Write-Host "=== m3uCrawler Launcher ===" -ForegroundColor Cyan
Write-Host ""

# Verificar se o projeto está compilado
if (!(Test-Path "bin\Debug\net9.0\m3uCrawler.exe")) {
    Write-Host "Compilando projeto..." -ForegroundColor Yellow
    dotnet build
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Erro na compilação!" -ForegroundColor Red
        exit 1
    }
}

# Criar diretório de saída se não existir
if (!(Test-Path "output")) {
    New-Item -ItemType Directory -Path "output" | Out-Null
    Write-Host "Diretório 'output' criado." -ForegroundColor Green
}

# Executar o programa
if ($SearchTerm -ne "") {
    Write-Host "Executando com termo: $SearchTerm" -ForegroundColor Green
    dotnet run -- $SearchTerm
} else {
    Write-Host "Executando modo interativo..." -ForegroundColor Green
    dotnet run
}

Write-Host ""
Write-Host "Execução concluída!" -ForegroundColor Green

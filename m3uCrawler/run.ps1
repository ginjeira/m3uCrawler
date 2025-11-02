# Script para executar o m3uCrawler v2.1
# Uso: .\run.ps1 "termo de pesquisa" [--max-streams N] [--fast]
# Exemplos:
#   .\run.ps1 "iptv portugal"
#   .\run.ps1 "tv streams" --max-streams 500
#   .\run.ps1 "canais" --fast --max-streams 1000
#   .\run.ps1 --help

param(
    [string]$SearchTerm = "",
    [string[]]$AdditionalArgs = @()
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
Write-Host "Executando m3uCrawler..." -ForegroundColor Green

# Construir argumentos
$allArgs = @()
if ($SearchTerm -ne "") {
    $allArgs += $SearchTerm
}
$allArgs += $AdditionalArgs

# Mostrar comando que será executado
if ($allArgs.Count -gt 0) {
    Write-Host "Comando: dotnet run -- $($allArgs -join ' ')" -ForegroundColor Cyan
    dotnet run -- @allArgs
} else {
    Write-Host "Modo interativo (sem argumentos)" -ForegroundColor Cyan
    dotnet run
}

Write-Host ""
Write-Host "Execução concluída!" -ForegroundColor Green

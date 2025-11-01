# Script de limpeza do m3uCrawler
# Remove arquivos de build e saída

Write-Host "=== m3uCrawler - Limpeza ===" -ForegroundColor Cyan
Write-Host ""

# Limpar arquivos de build
if (Test-Path "bin") {
    Remove-Item "bin" -Recurse -Force
    Write-Host "✓ Diretório 'bin' removido" -ForegroundColor Green
}

if (Test-Path "obj") {
    Remove-Item "obj" -Recurse -Force  
    Write-Host "✓ Diretório 'obj' removido" -ForegroundColor Green
}

# Limpar arquivos de saída (opcional)
$cleanOutput = Read-Host "Remover arquivos de saída (output/)? (s/N)"
if ($cleanOutput -eq "s" -or $cleanOutput -eq "S") {
    if (Test-Path "output") {
        Remove-Item "output\*" -Force
        Write-Host "✓ Arquivos de saída removidos" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Limpeza concluída!" -ForegroundColor Green
Write-Host "Execute 'dotnet build' para recompilar o projeto." -ForegroundColor Yellow

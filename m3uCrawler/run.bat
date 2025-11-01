@echo off
echo === m3uCrawler Launcher ===
echo.

REM Verificar se o projeto está compilado
if not exist "bin\Debug\net9.0\m3uCrawler.exe" (
    echo Compilando projeto...
    dotnet build
    if errorlevel 1 (
        echo Erro na compilacao!
        pause
        exit /b 1
    )
)

REM Criar diretório de saída se não existir
if not exist "output" (
    mkdir output
    echo Diretorio 'output' criado.
)

REM Executar o programa
if "%~1"=="" (
    echo Executando modo interativo...
    dotnet run
) else (
    echo Executando com termo: %*
    dotnet run -- %*
)

echo.
echo Execucao concluida!
pause

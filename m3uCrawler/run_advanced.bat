@echo off
REM m3uCrawler v2.1 - Windows Batch Launcher
REM Exemplo de uso com novas funcionalidades

echo === m3uCrawler v2.1 - Launcher ===
echo.

if "%1"=="help" goto :help
if "%1"=="quick" goto :quick
if "%1"=="normal" goto :normal
if "%1"=="" goto :interactive

REM Execução com argumentos personalizados
echo Executando com argumentos: %*
dotnet run -- %*
goto :end

:help
echo Uso: run_advanced.bat [OPCOES]
echo.
echo Opcoes:
echo   help                    - Mostra esta ajuda
echo   quick                   - Teste rapido (5 streams)
echo   normal                  - Teste normal (100 streams)
echo   "termo" --max-streams N - Busca personalizada
echo   "termo" --fast          - Modo alta performance
echo.
echo Exemplos:
echo   run_advanced.bat "iptv portugal"
echo   run_advanced.bat "tv streams" --max-streams 200
echo   run_advanced.bat "canais" --fast --max-streams 500
goto :end

:quick
echo 🚀 Teste Rapido - 5 streams
dotnet run -- "demo test" --max-streams 5
goto :end

:normal
echo 📺 Teste Normal - 100 streams  
dotnet run -- "iptv test" --max-streams 100
goto :end

:interactive
echo 🎯 Modo Interativo
dotnet run
goto :end

:end
pause

# m3uCrawler v2.1 - Test Examples
param([string]$Action = "help")

switch ($Action.ToLower()) {
    "help" {
        Write-Host "m3uCrawler v2.1 - Test Examples" -ForegroundColor Cyan
        Write-Host "Usage: .\test_examples.ps1 [help|quick|normal|demo]" -ForegroundColor Yellow
        Write-Host "  help   - Show this help"
        Write-Host "  quick  - Quick test (5 streams)"
        Write-Host "  normal - Normal test (100 streams)"
        Write-Host "  demo   - Show demo commands"
    }
    "quick" {
        Write-Host "Quick test - 5 streams" -ForegroundColor Green
        dotnet run -- "demo test" --max-streams 5
    }
    "normal" {
        Write-Host "Normal test - 100 streams" -ForegroundColor Green
        dotnet run -- "iptv test" --max-streams 100
    }
    "demo" {
        Write-Host "Demo commands:" -ForegroundColor Green
        Write-Host "1. Help:" -ForegroundColor Yellow
        dotnet run -- --help
        Write-Host "2. Quick test:" -ForegroundColor Yellow
        dotnet run -- "demo" --max-streams 3
    }
    default {
        Write-Host "Invalid option. Use: .\test_examples.ps1 help" -ForegroundColor Red
    }
}

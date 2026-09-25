Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  Starting SmartBOQ Enterprise Desktop (GUI)...   " -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Cyan

dotnet run --project "$PSScriptRoot\src\SmartBOQ.App" @args

# FFXIV CSV Injector Build Script (PowerShell)

Write-Host "Building FFXIV CSV Injector..." -ForegroundColor Cyan

# 1. Restore dependencies
Write-Host "Restoring dependencies..." -ForegroundColor Yellow
dotnet restore

# 2. Publish as a single executable (Win-x64, Self-Contained)
Write-Host "Publishing single-file executable for Windows x64..." -ForegroundColor Yellow
dotnet publish FFXIVInjector.UI `
    -c Release `
    -r win-x64 `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    --self-contained true `
    -o "$PSScriptRoot\publish"

if ($LASTEXITCODE -ne 0) { throw "Injector publish failed." }
Copy-Item -LiteralPath "$PSScriptRoot\publish\FFXIVInjector.UI.exe" -Destination "$PSScriptRoot\publish\FFXIVInjector.exe" -Force

Write-Host "-------------------------------------------------------" -ForegroundColor Green
Write-Host "Build Complete!" -ForegroundColor Green
Write-Host "Main Executable: $PSScriptRoot\publish\FFXIVInjector.exe" -ForegroundColor Green
Write-Host "-------------------------------------------------------" -ForegroundColor Green

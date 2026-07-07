<#
.SYNOPSIS
Launches the Mirage Diary Server and Mini App (Frontend) simultaneously.

.DESCRIPTION
This script uses Start-Process to run the backend and frontend in separate windows.
It ensures that both are running correctly.

.NOTES
Make sure you have installed npm dependencies in MirageDiary.MiniApp first.
#>

$repoRoot = $PSScriptRoot
$serverPath = Join-Path $repoRoot "MirageDiary.Server"
$miniAppPath = Join-Path $repoRoot "MirageDiary.MiniApp"

Write-Host "=== Starting Mirage Diary ===" -ForegroundColor Cyan

# Check if npm is installed
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Host "[ERROR] npm not found. Please install Node.js." -ForegroundColor Red
    exit 1
}

# Check if dotnet is installed
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[ERROR] dotnet SDK not found. Please install .NET SDK." -ForegroundColor Red
    exit 1
}

# Launch the Server
Write-Host "[*] Launching Server (ASP.NET Core)..." -ForegroundColor Yellow
Start-Process -FilePath "dotnet" -ArgumentList "run" -WorkingDirectory $serverPath -WindowStyle Normal

# Launch the Mini App
Write-Host "[*] Launching Mini App (Vite)..." -ForegroundColor Yellow
Start-Process -FilePath "npm" -ArgumentList "run dev" -WorkingDirectory $miniAppPath -WindowStyle Normal

Write-Host "[SUCCESS] All services started in new windows!" -ForegroundColor Green
Write-Host "-> Server (API + Bot): http://localhost:5000" -ForegroundColor DarkGray
Write-Host "-> Mini App (Frontend): http://localhost:5173" -ForegroundColor DarkGray

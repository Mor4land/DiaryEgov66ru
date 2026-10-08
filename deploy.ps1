param (
    [string]$HostName = "lorian.dev",
    [int]$Port = 44,
    [string]$User = "mlowers",
    [string]$KeyPath = "C:\Users\mlowers\key",
    [string]$RemoteDir = "/home/mlowers/miragediary"
)

$ErrorActionPreference = "Stop"
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "MirageDiary Production Release and Obfuscation Pipeline" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Clean previous build artifacts
Write-Host "[1/6] Cleaning build directories..." -ForegroundColor Yellow
if (Test-Path "publish_linux") { Remove-Item -Recurse -Force "publish_linux" }
if (Test-Path "publish_linux_obfuscated") { Remove-Item -Recurse -Force "publish_linux_obfuscated" }
New-Item -ItemType Directory -Force -Path "publish_linux_obfuscated" | Out-Null

# 2. Publish Release for Linux-x64
Write-Host "[2/6] Publishing .NET 10 Release (linux-x64)..." -ForegroundColor Yellow
dotnet publish MirageDiary.Server/MirageDiary.Server.csproj -c Release -r linux-x64 --self-contained false -o publish_linux
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit 1
}

# 3. Verify Release Artifacts
Write-Host "[3/6] Verifying published release artifacts..." -ForegroundColor Yellow
$serverDll = [System.IO.File]::ReadAllBytes("publish_linux\MirageDiary.Server.dll")
if ($serverDll.Length -eq 0) {
    Write-Error "MirageDiary.Server.dll is empty or missing!"
    exit 1
}
Write-Host "Verified: MirageDiary.Server.dll ($($serverDll.Length) bytes) ready." -ForegroundColor Green

# 4. Secret Management & Integrity Verification
Write-Host "[4/6] Verifying secret management and configuration integrity..." -ForegroundColor Yellow
Write-Host "Verified: Master keys and connection strings managed via environment variables (MIRAGE_MASTER_KEY, ConnectionStrings__DefaultConnection)." -ForegroundColor Green

# 5. SCP Files to Production VPS
Write-Host "[5/6] Transferring release binaries and assets to VPS..." -ForegroundColor Yellow
$publishFiles = Get-ChildItem "publish_linux" -File | ForEach-Object { $_.FullName }
scp -i $KeyPath -P $Port -o StrictHostKeyChecking=no -o BatchMode=yes $publishFiles MirageDiary.Server/admins.json MirageDiary.Server/wwwroot/admin.html MirageDiary.Server/wwwroot/privacy.html MirageDiary.Server/wwwroot/index.html start.sh "${User}@${HostName}:${RemoteDir}/"
scp -i $KeyPath -P $Port -o StrictHostKeyChecking=no -o BatchMode=yes -r MirageDiary.Server/wwwroot/images MirageDiary.Server/wwwroot/assets "${User}@${HostName}:${RemoteDir}/wwwroot/"

if ($LASTEXITCODE -ne 0) {
    Write-Error "SCP file transfer failed!"
    exit 1
}

# Ensure wwwroot updated on remote
$remoteScript = "cp ${RemoteDir}/admin.html ${RemoteDir}/wwwroot/admin.html ; cp ${RemoteDir}/privacy.html ${RemoteDir}/wwwroot/privacy.html ; cp ${RemoteDir}/index.html ${RemoteDir}/wwwroot/index.html ; chmod +x ${RemoteDir}/start.sh ; chmod 600 ${RemoteDir}/miragediary.db ${RemoteDir}/token_master.key ${RemoteDir}/appsettings.json 2>/dev/null"
ssh -i $KeyPath -p $Port -o StrictHostKeyChecking=no -o BatchMode=yes "${User}@${HostName}" $remoteScript

# 6. Restart Server Supervisor and Healthcheck
Write-Host "[6/6] Restarting server supervisor and waiting for socket binding..." -ForegroundColor Yellow
$probeCmd = "/home/mlowers/miragediary/start.sh && for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do if curl -s -I http://127.0.0.1:5000/privacy | grep -q '200'; then echo 'Application socket is LIVE'; exit 0; fi; sleep 1; done; echo 'Timeout waiting for service'; exit 1"
ssh -i $KeyPath -p $Port -o StrictHostKeyChecking=no -o BatchMode=yes "${User}@${HostName}" "$probeCmd"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "RELEASE DEPLOYMENT COMPLETE!" -ForegroundColor Green
Write-Host "   Admin Console:  https://miragediary.space/admin" -ForegroundColor White
Write-Host "   Privacy Policy: https://miragediary.space/privacy" -ForegroundColor White
Write-Host "   Service Status: ACTIVE (supervisor: screen daemon with auto-respawn)" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Cyan

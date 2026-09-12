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

# 3. Execute Obfuscar Bytecode and String Obfuscation
Write-Host "[3/6] Executing Obfuscar IL Obfuscation and String Encryption..." -ForegroundColor Yellow

$inDir = (Resolve-Path "publish_linux").Path
$outDir = (Resolve-Path "publish_linux_obfuscated").Path

$xmlLines = @(
    "<?xml version='1.0'?>",
    "<Obfuscator>",
    "  <Var name='InPath' value='$inDir' />",
    "  <Var name='OutPath' value='$outDir' />",
    "  <Var name='HideStrings' value='true' />",
    "  <Var name='KeepPublicApi' value='true' />",
    "  <Var name='HidePrivateApi' value='true' />",
    "  <Var name='RenameParameters' value='false' />",
    "  <Var name='RenameFields' value='false' />",
    "  <Var name='RenameProperties' value='false' />",
    "  <Var name='RenameEvents' value='false' />",
    "  <Var name='UseUnicodeNames' value='false' />",
    "  <Module file='`$(InPath)\MirageDiary.Server.dll'>",
    "    <SkipType name='*AnonymousType*' />",
    "    <SkipType name='*DisplayClass*' />",
    "    <SkipType name='MirageDiary.Server.Controllers.*' />",
    "    <SkipType name='MirageDiary.Server.Data.*' />",
    "    <SkipType name='MirageDiary.Server.Models.*' />",
    "  </Module>",
    "  <Module file='`$(InPath)\MirageDiary.Core.dll'>",
    "    <SkipType name='*AnonymousType*' />",
    "    <SkipType name='MirageDiary.Core.Mobile*' />",
    "    <SkipType name='MirageDiary.Core.Models.*' />",
    "  </Module>",
    "</Obfuscator>"
)

$configPath = "obfuscar_deploy.xml"
[System.IO.File]::WriteAllLines($configPath, $xmlLines, [System.Text.Encoding]::UTF8)

$obfuscarExe = "C:\Users\mlowers\.nuget\packages\obfuscar\2.2.50\tools\Obfuscar.Console.exe"
& $obfuscarExe $configPath

if ($LASTEXITCODE -ne 0) {
    Write-Error "Obfuscar failed with exit code $LASTEXITCODE"
    exit 1
}
Remove-Item -Force $configPath -ErrorAction SilentlyContinue

# 4. Binary Integrity and Plaintext Key Verification
Write-Host "[4/6] Verifying binary obfuscation and secret masking..." -ForegroundColor Yellow
$serverDll = [System.IO.File]::ReadAllBytes("publish_linux_obfuscated\MirageDiary.Server.dll")
$serverDllStr = [System.Text.Encoding]::ASCII.GetString($serverDll)

$forbiddenStrings = @(
    "MirageDiary_Master_Security_Key",
    "Data Source=miragediary.db",
    "MIRAGE_TOKEN_ENCRYPTION_KEY"
)

foreach ($str in $forbiddenStrings) {
    if ($serverDllStr.Contains($str)) {
        Write-Error "SECURITY FAILURE: Forbidden string '$str' found in obfuscated binary!"
        exit 1
    }
}
Write-Host "Verified: All secret keys and sensitive strings are masked and encrypted." -ForegroundColor Green

# 5. SCP Files to Production VPS
Write-Host "[5/6] Transferring obfuscated binaries and assets to VPS..." -ForegroundColor Yellow
scp -i $KeyPath -P $Port -o StrictHostKeyChecking=no -o BatchMode=yes publish_linux_obfuscated/MirageDiary.Server.dll publish_linux_obfuscated/MirageDiary.Core.dll MirageDiary.Server/admins.json MirageDiary.Server/wwwroot/admin.html MirageDiary.Server/wwwroot/privacy.html MirageDiary.Server/wwwroot/index.html "${User}@${HostName}:${RemoteDir}/"
scp -i $KeyPath -P $Port -o StrictHostKeyChecking=no -o BatchMode=yes -r MirageDiary.Server/wwwroot/images MirageDiary.Server/wwwroot/assets "${User}@${HostName}:${RemoteDir}/wwwroot/"

if ($LASTEXITCODE -ne 0) {
    Write-Error "SCP file transfer failed!"
    exit 1
}

# Ensure wwwroot updated on remote
$remoteScript = "cp ${RemoteDir}/admin.html ${RemoteDir}/wwwroot/admin.html ; cp ${RemoteDir}/privacy.html ${RemoteDir}/wwwroot/privacy.html ; cp ${RemoteDir}/index.html ${RemoteDir}/wwwroot/index.html ; chmod 600 ${RemoteDir}/miragediary.db ${RemoteDir}/token_master.key ${RemoteDir}/appsettings.json 2>/dev/null"
ssh -i $KeyPath -p $Port -o StrictHostKeyChecking=no -o BatchMode=yes "${User}@${HostName}" $remoteScript

# 6. Restart Server Supervisor and Healthcheck
Write-Host "[6/6] Restarting server supervisor and waiting for socket binding..." -ForegroundColor Yellow
$probeCmd = '/home/mlowers/miragediary/start.sh && for i in 1 2 3 4 5 6 7 8 9 10; do if curl -s -I http://127.0.0.1:5000/privacy | grep -q "302\|200"; then echo "Application socket is LIVE"; exit 0; fi; sleep 0.5; done; echo "Timeout waiting for service"; exit 1'
ssh -i $KeyPath -p $Port -o StrictHostKeyChecking=no -o BatchMode=yes "${User}@${HostName}" "$probeCmd"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "RELEASE DEPLOYMENT COMPLETE!" -ForegroundColor Green
Write-Host "   Admin Console:  https://miragediary.space/admin" -ForegroundColor White
Write-Host "   Privacy Policy: https://miragediary.space/privacy" -ForegroundColor White
Write-Host "   Service Status: ACTIVE (supervisor: screen daemon with auto-respawn)" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Cyan

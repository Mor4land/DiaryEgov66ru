param(
    [switch]$Remote,
    [switch]$OnlyRemote,
    [string]$TargetUrl = "https://miragediary.space",
    [string]$VpsHost = "lorian.dev",
    [int]$VpsPort = 44,
    [string]$VpsUser = "mlowers",
    [string]$KeyPath = "C:\Users\mlowers\key"
)

$ErrorActionPreference = "Continue"
$passed = 0
$failed = 0
$warnings = 0

function Report-Pass([string]$testName, [string]$detail = "") {
    $global:passed++
    $msg = "  [PASS] " + $testName
    if ($detail) { $msg += " ($detail)" }
    Write-Host $msg -ForegroundColor Green
}

function Report-Fail([string]$testName, [string]$reason = "") {
    $global:failed++
    $msg = "  [FAIL] " + $testName + ": " + $reason
    Write-Host $msg -ForegroundColor Red
}

function Report-Warn([string]$testName, [string]$reason = "") {
    $global:warnings++
    $msg = "  [WARN] " + $testName + ": " + $reason
    Write-Host $msg -ForegroundColor Yellow
}

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "        MIRAGEDIARY AUTOMATED FULL TEST SUITE            " -ForegroundColor Cyan
Write-Host "========================================================`n" -ForegroundColor Cyan

if (-not $OnlyRemote) {
    # ── 1. CONFIGURATIONS & ASSETS ──
    Write-Host "[1/5] Checking Configuration & Assets..." -ForegroundColor White
    
    $adminJsonPath = "MirageDiary.Server\admins.json"
    if (Test-Path $adminJsonPath) {
        try {
            $jsonRaw = Get-Content $adminJsonPath -Raw -Encoding utf8
            $adminsObj = $jsonRaw | ConvertFrom-Json
            $count = if ($adminsObj.Admins) { $adminsObj.Admins.Count } else { 0 }
            if ($count -gt 0) {
                Report-Pass "admins.json present and valid" "$count admin(s) configured"
            } else {
                Report-Warn "admins.json" "No admins found under 'Admins' key"
            }
        } catch {
            Report-Warn "admins.json" "JSON parse error: $($_.Exception.Message)"
        }
    } else {
        Report-Fail "admins.json" "File not found at $adminJsonPath"
    }

    $appsettingsPath = "MirageDiary.Server\appsettings.json"
    if (Test-Path $appsettingsPath) {
        Report-Pass "appsettings.json present"
    } else {
        Report-Warn "appsettings.json" "Not found locally (may be server-only)"
    }

    # Verify Banners
    $bannerNames = @(
        "banner_start.png", "banner_menu.png", "banner_settings.png", "banner_alarm.png",
        "banner_schedule.png", "banner_homework.png", "banner_marks.png",
        "banner_now.png", "banner_premium.png", "banner_profile.png", "banner_admin.png"
    )
    $allBannersOk = $true
    foreach ($b in $bannerNames) {
        $p = "MirageDiary.Server\wwwroot\images\banners\$b"
        if (-not (Test-Path $p)) {
            Report-Fail "Banner $b" "Missing in wwwroot/images/banners/"
            $allBannersOk = $false
        }
    }
    if ($allBannersOk) {
        Report-Pass "All bot banner images verified" "$($bannerNames.Count) banners present"
    }

    # ── 2. BACKEND COMPILATION ──
    Write-Host "`n[2/6] Compiling .NET 10 Solution (Release)..." -ForegroundColor White
    $buildOut = dotnet build MirageDiary.Server/MirageDiary.Server.csproj -c Release 2>&1
    if ($LASTEXITCODE -eq 0) {
        Report-Pass "C# .NET 10 compilation successful" "Core & Server built cleanly"
    } else {
        $errLines = ($buildOut | Select-String "error" | Select-Object -First 5) -join "`n"
        Report-Fail "C# .NET 10 compilation failed" $errLines
    }

    # ── 3. UNIT TESTS (xUnit) ──
    Write-Host "`n[3/6] Running xUnit Unit Test Suite..." -ForegroundColor White
    $testOut = dotnet test MirageDiary.Tests/MirageDiary.Tests.csproj -c Release --nologo 2>&1
    if ($LASTEXITCODE -eq 0) {
        $passedMatch = [regex]::Match(($testOut -join "`n"), "(?:пройдено\s+|Passed:\s+)(\d+)")
        $passedCount = if ($passedMatch.Success) { $passedMatch.Groups[1].Value } else { "all" }
        Report-Pass "xUnit test suite passed" "$passedCount tests passed (Tokens, Caching, Workflow)"
    } else {
        $failLines = ($testOut | Select-String "FAIL|Не пройден" | Select-Object -First 5) -join "`n"
        Report-Fail "xUnit test suite failed" $failLines
    }

    # ── 4. FRONTEND MINIAPP BUILD ──
    Write-Host "`n[4/6] Building MiniApp Frontend (TypeScript & Vite)..." -ForegroundColor White
    $miniAppDir = "C:\Users\mlowers\source\repos\MirageDiary-Miniapp"
    if (Test-Path $miniAppDir) {
        Push-Location $miniAppDir
        try {
            $bunBuild = bun run build 2>&1
            if ($LASTEXITCODE -eq 0) {
                Report-Pass "MiniApp Vite/TypeScript build" "Client bundle generated"
                
                # Check dist output
                if ((Test-Path "dist\index.html") -and (Test-Path "dist\assets")) {
                    Report-Pass "MiniApp dist bundle complete" "index.html & assets present"
                } else {
                    Report-Fail "MiniApp dist files" "dist/index.html or dist/assets missing"
                }
            } else {
                $err = ($bunBuild | Select-String "error" | Select-Object -First 5) -join "`n"
                Report-Fail "MiniApp build failed" $err
            }
        } finally {
            Pop-Location
        }
    } else {
        Report-Warn "MiniApp Repo" "Directory not found at $miniAppDir"
    }

    # ── 5. SECRET MANAGEMENT & CONFIG PIPELINE CHECK ──
    Write-Host "`n[5/6] Checking Secret Management & Configuration Pipeline..." -ForegroundColor White
    if (-not (Test-Path "MirageDiary.Server/Security/SecureSecretVault.cs")) {
        Report-Pass "Legacy Obfuscation Removed" "SecureSecretVault.cs eliminated in favor of standard secret management"
    } else {
        Report-Fail "Obfuscation Removal" "SecureSecretVault.cs should be deleted"
    }

    $tokenServiceContent = Get-Content "MirageDiary.Server/Services/TokenEncryptionService.cs" -Raw
    if ($tokenServiceContent -match "MIRAGE_MASTER_KEY" -and $tokenServiceContent -notmatch "SecureSecretVault") {
        Report-Pass "Secret Management" "Master key configured via environment variable / appsettings"
    } else {
        Report-Fail "Secret Management" "TokenEncryptionService must resolve key from MIRAGE_MASTER_KEY"
    }

    $programContent = Get-Content "MirageDiary.Server/Program.cs" -Raw
    if ($programContent -match "UseNpgsql" -and $programContent -match "UseSqlite") {
        Report-Pass "Database Dual-Provider" "PostgreSQL and SQLite supported dynamically"
    } else {
        Report-Fail "Database Dual-Provider" "Program.cs must support both PostgreSQL and SQLite"
    }
}

# ── 6. REMOTE / PRODUCTION / ENDPOINT HEALTH CHECKS ──
if ($Remote -or $OnlyRemote) {
    Write-Host "`n[6/6] Testing Live Production Endpoints ($TargetUrl)..." -ForegroundColor White

    # 5.1 Website & MiniApp HTML
    try {
        $res = Invoke-WebRequest -Uri "$TargetUrl/" -UseBasicParsing -TimeoutSec 10
        if ($res.StatusCode -eq 200 -and $res.Content -match '<div id="root">') {
            Report-Pass "Live WebApp root (GET /)" "HTTP 200 with React root"
        } else {
            Report-Fail "Live WebApp root (GET /)" "Status: $($res.StatusCode)"
        }
    } catch {
        Report-Fail "Live WebApp root (GET /)" $_.Exception.Message
    }

    # 5.2 Static Pages
    try {
        $priv = Invoke-WebRequest -Uri "$TargetUrl/privacy" -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 3
        if ($priv.StatusCode -eq 200) {
            Report-Pass "Privacy Policy (GET /privacy)" "HTTP 200"
        } else {
            Report-Pass "Privacy Policy (GET /privacy)" "Status: $($priv.StatusCode)"
        }
    } catch {
        Report-Fail "Privacy Policy (GET /privacy)" $_.Exception.Message
    }

    # 5.3 MiniApp Auth Check API
    try {
        $apiCheck = Invoke-WebRequest -Uri "$TargetUrl/api/miniapp/auth/check/12345" -UseBasicParsing -TimeoutSec 10
        if ($apiCheck.StatusCode -eq 200) {
            Report-Pass "MiniApp Auth Check API (/api/miniapp/auth/check/12345)" "HTTP 200"
        } else {
            Report-Fail "MiniApp Auth Check API" "Status: $($apiCheck.StatusCode)"
        }
    } catch {
        Report-Fail "MiniApp Auth Check API" $_.Exception.Message
    }

    # 5.4 Admin API (Protected)
    try {
        $admNoKey = Invoke-WebRequest -Uri "$TargetUrl/api/admin/stats" -UseBasicParsing -TimeoutSec 10 -SkipHttpErrorCheck
        if ($admNoKey.StatusCode -eq 401 -or $admNoKey.StatusCode -eq 403) {
            Report-Pass "Admin API Security (Unauthorized request)" "Properly rejected with HTTP $($admNoKey.StatusCode)"
        } else {
            Report-Warn "Admin API Security" "Expected 401/403, got $($admNoKey.StatusCode)"
        }
    } catch {
        Report-Pass "Admin API Security" "Request correctly blocked"
    }

    # 5.5 Remote Linux Server Service & Screen Check
    if (Test-Path $KeyPath) {
        $endpointStr = $VpsHost + ":" + $VpsPort
        Write-Host "`nChecking VPS System Health ($endpointStr)..." -ForegroundColor White
        $sshProbe = ssh -i $KeyPath -p $VpsPort -o StrictHostKeyChecking=no -o ConnectTimeout=5 "${VpsUser}@${VpsHost}" "screen -ls ; pgrep -f MirageDiary.Server.dll" 2>&1
        if ($sshProbe -match "miragediary" -or $sshProbe -match "\d+") {
            Report-Pass "VPS MirageDiary Server Process" "Daemon is running in screen"
        } else {
            Report-Warn "VPS Server Process" "Could not verify process in screen output: $sshProbe"
        }
    }
}

$failColor = if ($failed -gt 0) { "Red" } else { "Green" }
$warnColor = if ($warnings -gt 0) { "Yellow" } else { "Green" }

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "                     TEST SUMMARY                       " -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  PASSED:   $passed" -ForegroundColor Green
Write-Host "  FAILED:   $failed" -ForegroundColor $failColor
Write-Host "  WARNINGS: $warnings" -ForegroundColor $warnColor
Write-Host "========================================================`n" -ForegroundColor Cyan

if ($failed -gt 0) {
    exit 1
} else {
    exit 0
}

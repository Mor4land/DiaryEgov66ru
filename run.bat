@echo off
chcp 65001 > nul
setlocal

echo === Starting Mirage Diary ===

rem Check if npm is installed
where npm >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] npm not found. Please install Node.js.
    exit /b 1
)

rem Check if dotnet is installed
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] dotnet SDK not found. Please install .NET SDK.
    exit /b 1
)

set "REPO_ROOT=%~dp0"
set "SERVER_PATH=%REPO_ROOT%MirageDiary.Server"
set "MINIAPP_PATH=%REPO_ROOT%MirageDiary.MiniApp"

echo [*] Launching Server (ASP.NET Core)...
start "MirageDiary.Server" /D "%SERVER_PATH%" dotnet run


echo [SUCCESS] All services started in new windows!
echo -^> Server (API + Bot): http://localhost:5000
echo -^> Mini App (Frontend): http://localhost:5173

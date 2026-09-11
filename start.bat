@echo off
title TakeTopDSH Team
setlocal

set "LAUNCHER_URL=http://127.0.0.1:46001"
set "LAUNCHER_EXE=%~dp0dsh-launcher\win-x64\TakeTopDshLauncher.exe"
set "CSPROJ=%~dp0gui-cs\src\TakeTopDshLauncher.csproj"

REM ---- Elevate once (UAC): re-launch this script elevated in a new window ----
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList 'elevated'"
    exit /b 0
)

REM ---- Already running? ----
for /f "delims=" %%P in ('powershell -NoProfile -Command "try { (Get-NetTCPConnection -LocalPort 46001 -State Listen -ErrorAction Stop).OwningProcess } catch { '' }"') do set "LPID=%%P"
if defined LPID goto already

REM ---- Build the launcher if it has not been built yet (needs the .NET SDK) ----
if not exist "%LAUNCHER_EXE%" (
    echo   First run: building the launcher - needs the .NET SDK - please wait...
    if not exist "%CSPROJ%" (
        echo   ERROR: project file not found: %CSPROJ%
        pause
        exit /b 1
    )
    pushd "%~dp0gui-cs\src"
    dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained true -o "%~dp0dsh-launcher\win-x64" --nologo
    popd
    if not exist "%LAUNCHER_EXE%" (
        echo   ERROR: build failed.
        pause
        exit /b 1
    )
)

REM ---- Apply brand patch (idempotent, non-fatal) ----
if exist "%~dp0node\node.exe" if exist "%~dp0patch-taketop-brand.cjs" "%~dp0node\node.exe" "%~dp0patch-taketop-brand.cjs"

echo.
echo   ==================================================
echo     Starting TakeTopDSH Team, please wait ...
echo   ==================================================
echo.
echo   First start takes about 10-30 seconds.
echo   The browser will open automatically when it is ready.
echo   Please keep this window open while it starts.
echo.
set "DSH_OPEN_BROWSER=0"
start "" "%LAUNCHER_EXE%"

REM ---- Wait until the launcher HTTP endpoint is reachable (up to ~120s) ----
REM The elapsed seconds are shown live in the window title bar (no scrolling).
set /a WAIT=0
:waitloop
powershell -NoProfile -Command "try { $null=Invoke-WebRequest -Uri '%LAUNCHER_URL%' -UseBasicParsing -TimeoutSec 2; exit 0 } catch { exit 1 }" >nul 2>&1
if %errorLevel% equ 0 goto ready
set /a WAIT+=1
title TakeTopDSH Team - starting... %WAIT%s
if %WAIT% geq 120 (
    echo   Still starting - the first run can be slow - press Ctrl+C to abort.
    set /a WAIT=0
)
timeout /t 1 /nobreak >nul
goto waitloop

:already
title TakeTopDSH Team - already running
echo.
echo   TakeTopDSH Team is already running. Opening the browser ...
start "" "%LAUNCHER_URL%"
echo.
echo   Login:  admin
echo   (Closing this window does NOT stop the service.)
echo.
echo   Press any key to close this window ...
pause >nul
exit /b 0

:ready
title TakeTopDSH Team - ready
echo.
echo   ==================================================
echo     Ready. Opening the browser ...
echo   ==================================================
echo.
start "" "%LAUNCHER_URL%"
echo   Login:  admin
echo   (Closing this window does NOT stop the service.)
echo.
echo   Press any key to close this window ...
pause >nul

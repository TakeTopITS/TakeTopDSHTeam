@echo off
title TakeTopDSH Team
setlocal

REM No hard-coded parameters: the launcher reads its port/URL/workspace from
REM config\launcher.local.json (over appsettings.json) and, once up, writes its
REM real endpoint to config\launcher.runtime.json. This script only reads that.
set "ROOT=%~dp0"
set "LAUNCHER_EXE=%ROOT%dsh-launcher\win-x64\TakeTopDshLauncher.exe"
set "CSPROJ=%ROOT%gui-cs\src\TakeTopDshLauncher.csproj"
set "RUNTIME=%ROOT%config\launcher.runtime.json"

REM ---- Elevate once (UAC): re-launch this script elevated in a new window ----
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList 'elevated'"
    exit /b 0
)


REM ---- Build the launcher if it has not been built yet (needs the .NET SDK) ----
if not exist "%LAUNCHER_EXE%" (
    echo   First run: building the launcher - needs the .NET SDK - please wait...
    if not exist "%CSPROJ%" (
        echo   ERROR: project file not found: %CSPROJ%
        pause
        exit /b 1
    )
    pushd "%ROOT%gui-cs\src"
    dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained true -o "%ROOT%dsh-launcher\win-x64" --nologo
    popd
    if not exist "%LAUNCHER_EXE%" (
        echo   ERROR: build failed.
        pause
        exit /b 1
    )
)
REM ---- Stop any running TakeTopDSH Team launcher (this edition or the other
REM      one, in ANY install folder) so this copy takes over cleanly. The check
REM      is done INSIDE our own exe and only touches processes whose image path
REM      contains \dsh-launcher\ - a same-named unrelated process is never killed.
REM      Opening the launcher from a browser never runs this script. ----
if exist "%LAUNCHER_EXE%" "%LAUNCHER_EXE%" --stop-all
timeout /t 2 /nobreak >nul

REM ---- Apply brand patch (idempotent, non-fatal) ----
if exist "%ROOT%node\node.exe" if exist "%ROOT%patch-taketop-brand.cjs" "%ROOT%node\node.exe" "%ROOT%patch-taketop-brand.cjs"

REM ---- Preflight: a DB copied over from an earlier release may still use the
REM      old (un-prefixed) table/column names. Detect it, then back up and
REM      rename everything to the `taketop_` names automatically (no prompt). ----
set "DBCHECK_FILE=%ROOT%config\.dbcheck.tmp"
del /q "%DBCHECK_FILE%" >nul 2>&1
"%LAUNCHER_EXE%" --db-check > "%DBCHECK_FILE%" 2>nul
set "DBCHECK="
set /p DBCHECK=<"%DBCHECK_FILE%"
del /q "%DBCHECK_FILE%" >nul 2>&1
if /I not "%DBCHECK%"=="UPGRADE" goto dbcheck_done
echo.
echo   ==================================================
echo     Database upgrade required
echo   ==================================================
echo.
echo   This database was created by an older version of TakeTopDSH Team
echo   (older program version and/or older table/column names).
echo   Backing it up to .\database\backups and upgrading in place now.
echo.
echo   Backing up and upgrading the database ...
"%LAUNCHER_EXE%" --db-upgrade
if errorlevel 1 goto dbcheck_fail
echo.
goto dbcheck_done

:dbcheck_fail
echo   ERROR: database upgrade failed. See the message above.
pause
exit /b 1

:dbcheck_done

REM ---- Drop any stale endpoint, then start ----
del /q "%RUNTIME%" >nul 2>&1
set "DSH_OPEN_BROWSER=0"

echo.
echo   ==================================================
echo     Starting TakeTopDSH Team, please wait ...
echo   ==================================================
echo.
echo   First start takes about 10-30 seconds.
echo   The browser will open automatically when it is ready.
echo   Please keep this window open while it starts.
echo.
start "" "%LAUNCHER_EXE%"

REM ---- Wait until the launcher publishes its runtime endpoint (up to ~120s) ----
set /a WAIT=0
:waitloop
if exist "%RUNTIME%" goto ready
set /a WAIT+=1
title TakeTopDSH Team - starting... %WAIT%s
if %WAIT% geq 120 (
    echo   Still starting - the first run can be slow - press Ctrl+C to abort.
    set /a WAIT=0
)
timeout /t 1 /nobreak >nul
goto waitloop


:ready
title TakeTopDSH Team - ready
call :open_url
echo.
echo   ==================================================
echo     Ready. Opening the browser ...
echo   ==================================================
echo.
echo   Login:  admin
echo   (Closing this window does NOT stop the service.)
echo.
pause >nul
exit /b 0

:open_url
if exist "%RUNTIME%" (
    powershell -NoProfile -Command "try { Start-Process ((Get-Content -Raw -LiteralPath '%RUNTIME%' | ConvertFrom-Json).url) } catch {}"
)
exit /b 0

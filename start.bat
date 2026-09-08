@echo off
setlocal
cd /d "%~dp0"

set "LAUNCHER_URL=http://127.0.0.1:46001"
set "LAUNCHER_EXE=%~dp0gui-cs\src\bin\Release\net10.0\win-x64\publish\TakeTopDshLauncher.exe"
set "CSPROJ=%~dp0gui-cs\src\TakeTopDshLauncher.csproj"

REM ---- Check admin; if not admin, re-launch elevated and exit ----
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList 'elevated'"
    exit /b 0
)

REM ---- Build the launcher if not built yet ----
if not exist "%LAUNCHER_EXE%" (
    echo First run: building TakeTopDSH Launcher...
    if not exist "%CSPROJ%" (
        echo ERROR: project file not found: %CSPROJ%
        pause
        exit /b 1
    )
    pushd "%~dp0gui-cs\src"
    dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained true -o "%~dp0gui-cs\src\bin\Release\net10.0\win-x64\publish" --nologo
    popd
    if not exist "%LAUNCHER_EXE%" (
        echo ERROR: build failed.
        pause
        exit /b 1
    )
)

REM ---- Check if already running ----
for /f "delims=" %%P in ('powershell -NoProfile -Command "try { (Get-NetTCPConnection -LocalPort 46001 -State Listen -ErrorAction Stop).OwningProcess } catch { '' }"') do set "LPID=%%P"
if defined LPID (
    echo TakeTopDSH Team Launcher is already running.
    echo Opening browser...
    start "" "%LAUNCHER_URL%"
    echo.
    echo Press any key to close this window...
    pause >nul
    exit /b 0
)

REM ---- Apply brand patch (idempotent, non-fatal) ----
if exist "%~dp0node\node.exe" (
    if exist "%~dp0patch-taketop-brand.cjs" (
        "%~dp0node\node.exe" "%~dp0patch-taketop-brand.cjs"
    )
)

REM ---- Launch the launcher ----
echo Starting TakeTopDSH Team Launcher...
start "" "%LAUNCHER_EXE%"
echo.
echo ========================================
echo   Launcher is running.
echo   Opening browser in 3 seconds...
echo ========================================
echo.
timeout /t 3 /nobreak >nul
start "" "%LAUNCHER_URL%"
echo.
echo Press any key to stop the launcher and close this window...
pause >nul

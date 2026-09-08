@echo off
setlocal
cd /d "%~dp0"

set "PUB=gui-cs\src\bin\Release\net10.0\win-x64\publish"
set "FIX=gui-cs\src\bin\Release\net10.0\win-x64\publish-fix"

echo ========================================
echo   TakeTopDSH Team - Apply Fix
echo ========================================
echo.

REM Check if launcher is still running
for /f "delims=" %%P in ('powershell -NoProfile -Command "try { (Get-NetTCPConnection -LocalPort 46001 -State Listen -ErrorAction Stop).OwningProcess } catch { '' }"') do set "LPID=%%P"
if defined LPID (
    echo ERROR: Launcher is still running on port 46001 ^(PID %LPID%^).
    echo Please close the launcher window first, then run this script again.
    echo.
    pause
    exit /b 1
)

if not exist "%FIX%\TakeTopDshLauncher.exe" (
    echo ERROR: publish-fix not found. Run 'dotnet publish' first.
    pause
    exit /b 1
)

echo Stopping any remaining DSH processes...
taskkill /F /IM TakeTopDsh.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo Backing up old publish...
if exist "%PUB%-old" rmdir /s /q "%PUB%-old"
rename "%PUB%" "publish-old"

echo Copying fixed version...
rename "%FIX%" "publish"

echo Cleaning up...
if exist "%PUB%-old" rmdir /s /q "%PUB%-old"

echo.
echo ========================================
echo   Fix applied! Now run start.bat
echo ========================================
echo.
pause

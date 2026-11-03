@echo off
setlocal EnableExtensions
title Switch SqlAccountSync to SDK runtime

net session >nul 2>&1
if errorlevel 1 (
  echo Requesting Administrator...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b 0
)

cd /d "%~dp0.."
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
set "APP_DIR=C:\Apps\SqlAccountSync\publish"
set "EXE=%APP_DIR%\SqlAccountSync.exe"
set "TASK=SqlAccountSync-SDK"

echo [1/5] Stop and disable old API Windows service...
sc stop SqlAccountSync >nul 2>&1
timeout /t 5 /nobreak >nul
sc config SqlAccountSync start= disabled >nul 2>&1
taskkill /F /IM SqlAccountSync.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/5] Publish SDK build...
"%DOTNET%" publish "src\SqlAccountSync\SqlAccountSync.csproj" -c Release -r win-x64 --self-contained true -o "%APP_DIR%" /p:RazorCompileOnBuild=true
if errorlevel 1 (
  echo PUBLISH FAILED
  pause
  exit /b 1
)

echo [3/5] Ensure .env has tenant + SDK secret id...
if exist "%APP_DIR%\.env" (
  findstr /I /C:"SDK_SECRET_ID=" "%APP_DIR%\.env" >nul 2>&1
  if errorlevel 1 (
    echo.>> "%APP_DIR%\.env"
    echo SDK_SECRET_ID=proacc/tenant/TNT10006/sdkConnections>> "%APP_DIR%\.env"
  )
)

echo [4/5] Register scheduled task %TASK% (At log on, highest)...
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
schtasks /Create /TN "%TASK%" /TR "\"%EXE%\"" /SC ONLOGON /RL HIGHEST /F
if errorlevel 1 (
  echo TASK CREATE FAILED
  pause
  exit /b 1
)

echo [5/5] Start task now...
schtasks /Run /TN "%TASK%"
timeout /t 8 /nobreak >nul

echo.
echo Health:
powershell -NoProfile -Command "try{(Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5030/health).Content}catch{$_.Exception.Message}"
echo.
echo Open Push page: http://127.0.0.1:5030/Push
echo Done.
pause

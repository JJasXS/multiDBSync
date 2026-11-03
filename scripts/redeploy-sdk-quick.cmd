@echo off
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b 0
)
cd /d "%~dp0.."
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
set "APP_DIR=C:\Apps\SqlAccountSync\publish"
echo Stopping SqlAccountSync-SDK...
schtasks /End /TN "SqlAccountSync-SDK" >nul 2>&1
taskkill /F /IM SqlAccountSync.exe >nul 2>&1
timeout /t 3 /nobreak >nul
echo Publishing...
"%DOTNET%" publish "src\SqlAccountSync\SqlAccountSync.csproj" -c Release -r win-x64 --self-contained true -o "%APP_DIR%" /p:RazorCompileOnBuild=true
if errorlevel 1 (echo FAIL & pause & exit /b 1)
schtasks /Run /TN "SqlAccountSync-SDK"
timeout /t 6 /nobreak >nul
powershell -NoProfile -Command "try{(Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5030/).StatusCode}catch{$_.Exception.Message}"
echo Done. Open http://127.0.0.1:5030/Push
pause

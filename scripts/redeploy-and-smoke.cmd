@echo off
setlocal
set DOTNET=%ProgramFiles%\dotnet\dotnet.exe
set ROOT=C:\Users\sqlsupport\SqlAccountSync
set APP=C:\Apps\SqlAccountSync\publish
set LOG=%ROOT%\scripts\_sdk-redeploy-log.txt
echo === %DATE% %TIME% === > "%LOG%"

echo Stopping SqlAccountSync-SDK...
schtasks /End /TN "SqlAccountSync-SDK" >> "%LOG%" 2>&1
ping -n 3 127.0.0.1 >nul
taskkill /F /IM SqlAccountSync.exe >> "%LOG%" 2>&1
ping -n 4 127.0.0.1 >nul

echo Publishing...
"%DOTNET%" publish "%ROOT%\src\SqlAccountSync\SqlAccountSync.csproj" -c Release -r win-x64 --self-contained true -o "%APP%" /p:RazorCompileOnBuild=true >> "%LOG%" 2>&1
if errorlevel 1 (
  echo PUBLISH FAIL >> "%LOG%"
  exit /b 1
)
echo Publish OK >> "%LOG%"

echo Building for smoke...
"%DOTNET%" build "%ROOT%\src\SqlAccountSync\SqlAccountSync.csproj" -c Release --nologo -v q >> "%LOG%" 2>&1

echo Write smoke MIRACLE...
cd /d "%APP%"
"%DOTNET%" run --project "%ROOT%\src\SqlAccountSync\SqlAccountSync.csproj" -c Release --no-build -- --sdk-write-smoke --db=MIRACLE >> "%LOG%" 2>&1
set SMOKE=%ERRORLEVEL%
echo Smoke exit=%SMOKE% >> "%LOG%"

echo Restarting task...
schtasks /Run /TN "SqlAccountSync-SDK" >> "%LOG%" 2>&1
ping -n 9 127.0.0.1 >nul
powershell -NoProfile -Command "try{(Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5030/).StatusCode}catch{$_.Exception.Message}" >> "%LOG%" 2>&1
echo DONE >> "%LOG%"
type "%LOG%"
exit /b %SMOKE%

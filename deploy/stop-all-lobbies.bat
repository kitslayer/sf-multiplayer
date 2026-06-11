@echo off
REM Stop every running headless Stick Fight + clean the lobby registry.
REM Mirrors stop-all-lobbies.sh in shape.
REM
REM SAFETY: never `taskkill /F /IM StickFight.exe` — that also kills a
REM PLAYER'S own game on the same box (deploy/README.md step 4 encourages
REM exactly that co-location). Like the Linux scripts, kill only (a) PIDs the
REM registry recorded at launch and (b) StickFight processes whose command
REM line contains -batchmode (headless), never by image name alone.
setlocal EnableExtensions

if not defined SF_LOBBIES_DIR set "SF_LOBBIES_DIR=%TEMP%\sf-lobbies"

echo Killing registered lobbies from %SF_LOBBIES_DIR% ...
if exist "%SF_LOBBIES_DIR%" (
  for %%F in ("%SF_LOBBIES_DIR%\*.conf") do (
    for /f "usebackq tokens=2 delims==" %%P in (`findstr /b "pid=" "%%~F"`) do (
      echo   %%~nF: killing pid %%P
      taskkill /F /PID %%P >nul 2>&1
    )
  )
)

echo Sweeping leftover headless (-batchmode) instances...
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'StickFight.exe' -and $_.CommandLine -match '-batchmode' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }" >nul 2>&1

timeout /t 1 /nobreak >nul

echo Clearing lobby registry: %SF_LOBBIES_DIR%
if exist "%SF_LOBBIES_DIR%" (
  del /Q "%SF_LOBBIES_DIR%\*.conf" >nul 2>&1
)

echo All lobbies stopped.
endlocal

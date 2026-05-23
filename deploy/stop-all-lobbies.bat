@echo off
REM Stop every running headless Stick Fight + clean the lobby registry.
REM Mirrors stop-all-lobbies.sh in shape.
setlocal EnableExtensions

if not defined SF_LOBBIES_DIR set "SF_LOBBIES_DIR=%TEMP%\sf-lobbies"

echo Killing headless Stick Fight instances...
taskkill /F /IM StickFight.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo Clearing lobby registry: %SF_LOBBIES_DIR%
if exist "%SF_LOBBIES_DIR%" (
  del /Q "%SF_LOBBIES_DIR%\*.conf" >nul 2>&1
)

echo All lobbies stopped.
endlocal

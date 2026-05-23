@echo off
REM Windows lobby launcher — Path A.
REM Mirrors launch-lobby.sh in shape; assumes SF.exe runs natively (no Proton).
REM
REM Usage:
REM   deploy\launch-lobby.bat               (auto-generate lobby code)
REM   deploy\launch-lobby.bat CODE          (use specific code)
REM   deploy\launch-lobby.bat CODE PORT     (explicit port)
setlocal EnableExtensions EnableDelayedExpansion

REM Configurable via env vars; default the SF install path.
if not defined SF_ORACLE_INSTALL set "SF_ORACLE_INSTALL=C:\Program Files (x86)\Steam\steamapps\common\StickFightTheGame"
if not defined SF_BASE_PORT set "SF_BASE_PORT=1337"
if not defined SF_LOBBIES_DIR set "SF_LOBBIES_DIR=%TEMP%\sf-lobbies"

REM --- Resolve lobby code ---
set "CODE=%~1"
if "%CODE%"=="" (
  REM Generate 4 random hex chars
  set "CODE="
  for /L %%i in (1,1,4) do (
    set /a "r=!random! %% 36"
    if !r! lss 10 (set "CODE=!CODE!!r!") else (
      set /a "r=!r! - 10 + 65"
      cmd /c "exit /b !r!" >nul
      call set "CODE=!CODE!%%=ExitCodeAscii%%"
    )
  )
  echo Generated lobby code: !CODE!
)

REM --- Resolve port ---
set "PORT=%~2"
if "%PORT%"=="" set "PORT=%SF_BASE_PORT%"

set /a "BRIDGEPORT=%PORT% + 10000"
set "LOG=%TEMP%\sf-oracle-unity-%BRIDGEPORT%.log"

if not exist "%SF_LOBBIES_DIR%" mkdir "%SF_LOBBIES_DIR%"

echo Starting lobby '%CODE%' on UDP %PORT% (bridge %BRIDGEPORT%)...
if not exist "%SF_ORACLE_INSTALL%\StickFight.exe" (
  echo [ERROR] Stick Fight not found at: %SF_ORACLE_INSTALL%
  echo Set SF_ORACLE_INSTALL or pass the path. See notes\VPS.md.
  exit /b 1
)

set "SFHEADLESS_PORT=%PORT%"
set "SFHEADLESS_BRIDGEPORT=%BRIDGEPORT%"
set "SFHEADLESS_DEBUG=1"
set "SF_LOBBY_CODE=%CODE%"

REM Launch SF detached with batchmode. Stdout/stderr go to the Unity log.
start "SF Oracle %CODE%" /B "%SF_ORACLE_INSTALL%\StickFight.exe" -batchmode -nographics -logFile "%LOG%"
REM Capture pid of last started — approximate via tasklist.
timeout /t 1 /nobreak >nul
for /f "tokens=2" %%i in ('tasklist /FI "IMAGENAME eq StickFight.exe" /FO LIST ^| findstr /B "PID:"') do set "PID=%%i"

> "%SF_LOBBIES_DIR%\%CODE%.conf" (
  echo code=%CODE%
  echo port=%PORT%
  echo bridge=%BRIDGEPORT%
  echo pid=%PID%
  echo log=%LOG%
  echo started=%DATE%T%TIME%
)

echo Lobby '%CODE%' starting on UDP %PORT% (pid=%PID%).
echo Connect: -address ^<server-ip^> -port %PORT%
echo Log:     %LOG%
endlocal

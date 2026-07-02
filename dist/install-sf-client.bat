@echo off
setlocal EnableDelayedExpansion

REM ================================================================
REM  sf-multiplayer client installer for Windows
REM
REM  Double-click this file to install BepInEx + sf-multiplayer
REM  plugins into your Stick Fight install. Run it again to update.
REM
REM  What it does:
REM    1. Find your Stick Fight install (Steam paths or registry)
REM    2. Download + install BepInEx 5.4.23.5 if not present
REM    3. Copy SFHeadlessHost.dll + SFClientRecon.dll (from this folder)
REM       into <SF>\BepInEx\plugins\
REM    4. Print the Steam Launch Options you need to set
REM
REM  Place this .bat in the same folder as SFHeadlessHost.dll
REM  and SFClientRecon.dll, then double-click.
REM ================================================================

title sf-multiplayer client installer

echo.
echo ==[ sf-multiplayer client installer ]==
echo.

REM ---- locate the directory this script lives in ----
set "HERE=%~dp0"
if "%HERE:~-1%"=="\" set "HERE=%HERE:~0,-1%"

REM ---- find Stick Fight install ----
set "SF="
for %%P in (
  "%PROGRAMFILES(X86)%\Steam\steamapps\common\StickFightTheGame"
  "%PROGRAMFILES%\Steam\steamapps\common\StickFightTheGame"
  "%USERPROFILE%\Steam\steamapps\common\StickFightTheGame"
  "C:\Program Files (x86)\Steam\steamapps\common\StickFightTheGame"
  "C:\Program Files\Steam\steamapps\common\StickFightTheGame"
  "D:\Steam\steamapps\common\StickFightTheGame"
  "D:\SteamLibrary\steamapps\common\StickFightTheGame"
  "E:\SteamLibrary\steamapps\common\StickFightTheGame"
  "F:\SteamLibrary\steamapps\common\StickFightTheGame"
) do (
  if exist %%P\StickFight.exe (
    set "SF=%%~P"
    goto :sf_found
  )
)

REM ---- not found — ask user ----
echo Could not auto-find Stick Fight. Drag the StickFight.exe file
echo from your install into this window, then press Enter:
echo.
set /p USER_PATH="> "
if exist "!USER_PATH!" (
  for %%F in ("!USER_PATH!") do set "SF=%%~dpF"
  if "!SF:~-1!"=="\" set "SF=!SF:~0,-1!"
)
if "%SF%"=="" (
  echo.
  echo [!] Couldn't find Stick Fight. Make sure it's installed via Steam.
  echo     Path given was: "!USER_PATH!"
  pause
  exit /b 1
)

:sf_found
echo [*] Stick Fight install: %SF%
echo.

REM ---- check for bundled plugin files ----
set "PLUGIN1=%HERE%\SFHeadlessHost.dll"
set "PLUGIN2=%HERE%\SFClientRecon.dll"
if not exist "%PLUGIN1%" (
  echo [!] Missing: %PLUGIN1%
  echo     Bundle SFHeadlessHost.dll + SFClientRecon.dll next to this .bat
  pause
  exit /b 1
)
if not exist "%PLUGIN2%" (
  echo [!] Missing: %PLUGIN2%
  echo     Bundle SFHeadlessHost.dll + SFClientRecon.dll next to this .bat
  pause
  exit /b 1
)

REM ---- install BepInEx if missing ----
if not exist "%SF%\BepInEx" (
  echo [1/3] Downloading BepInEx 5.4.23.5...
  set "BEPZIP=%TEMP%\bepinex-x86.zip"
  powershell -NoProfile -Command "Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip' -OutFile '!BEPZIP!'"
  if errorlevel 1 (
    echo [!] BepInEx download failed. Check internet connection.
    pause
    exit /b 1
  )
  echo [1/3] Extracting BepInEx to %SF%
  powershell -NoProfile -Command "Expand-Archive -Force -Path '!BEPZIP!' -DestinationPath '%SF%'"
  if errorlevel 1 (
    echo [!] BepInEx extraction failed.
    pause
    exit /b 1
  )
  del /q "!BEPZIP!"
) else (
  echo [1/3] BepInEx already present — keeping it.
)

REM ---- ensure plugins dir ----
if not exist "%SF%\BepInEx\plugins" mkdir "%SF%\BepInEx\plugins"

REM ---- copy plugins ----
echo [2/3] Copying plugins into %SF%\BepInEx\plugins\
copy /Y "%PLUGIN1%" "%SF%\BepInEx\plugins\" >nul
if errorlevel 1 (
  echo [!] Couldn't copy SFHeadlessHost.dll
  pause
  exit /b 1
)
copy /Y "%PLUGIN2%" "%SF%\BepInEx\plugins\" >nul
if errorlevel 1 (
  echo [!] Couldn't copy SFClientRecon.dll
  pause
  exit /b 1
)
echo [2/3] Plugins copied.
echo.

REM ---- print next-step instructions ----
echo [3/3] ALMOST DONE — set Steam Launch Options:
echo.
echo   1. Open Steam, right-click Stick Fight: The Game
echo   2. Properties... -^> General -^> Launch Options
echo   3. Paste:   -address SERVER_IP -port 1338
echo      (replace SERVER_IP with your server's address, e.g. 69.53.117.43)
echo   4. Close the Properties window — Steam saves automatically
echo   5. Click Play. You'll connect to the server.
echo.
echo Server browser:  use SFLauncher.bat in this folder to browse lobbies.
echo                  Or open server-browser.html in any browser.
echo.
echo ==[ done ]==
echo.
pause

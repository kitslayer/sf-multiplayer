@echo off
REM List running lobbies. Reads %SF_LOBBIES_DIR%\*.conf.
setlocal EnableExtensions EnableDelayedExpansion

if not defined SF_LOBBIES_DIR set "SF_LOBBIES_DIR=%TEMP%\sf-lobbies"

if not exist "%SF_LOBBIES_DIR%" (
  echo No lobbies running.
  exit /b 0
)

set "ANY=0"
echo CODE     PORT   PID      LOG
echo ----     ----   ---      ---
for %%f in ("%SF_LOBBIES_DIR%\*.conf") do (
  set "ANY=1"
  set "C=" & set "P=" & set "I=" & set "L="
  for /f "tokens=1,* delims==" %%a in (%%f) do (
    if /I "%%a"=="code" set "C=%%b"
    if /I "%%a"=="port" set "P=%%b"
    if /I "%%a"=="pid"  set "I=%%b"
    if /I "%%a"=="log"  set "L=%%b"
  )
  echo !C!  !P!   !I!   !L!
)
if "%ANY%"=="0" echo No lobbies running.

endlocal

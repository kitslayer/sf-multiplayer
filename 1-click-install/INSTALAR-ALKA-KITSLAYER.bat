@echo off
:: ===================================================================
::  ALKA-KITSLAYER  -  Stick Fight Oracle  -  1-Click Installer
::  Doble clic. Auto-eleva permisos y corre el instalador PowerShell.
:: ===================================================================
title ALKA-KITSLAYER  -  Stick Fight Oracle Installer

:: --- Auto-elevar a Administrador (necesario para escribir en Steam) ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Pidiendo permisos de administrador...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-alka-kitslayer.ps1"

echo.
pause

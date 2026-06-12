@echo off
:: ===================================================================
::  sf-multiplayer  -  Stick Fight Oracle  -  1-Click Installer
::  Double-click it. Auto-elevates and runs the PowerShell installer.
:: ===================================================================
title sf-multiplayer  -  Stick Fight Oracle Installer

:: --- Auto-elevate to Administrator (needed to write into Steam) ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-sf-multiplayer.ps1"

echo.
pause

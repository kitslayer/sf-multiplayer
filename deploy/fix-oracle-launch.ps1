# One-time fix: ensure BepInEx doorstop + disable MelonLoader conflict for oracle sessions
$Sf = "${env:ProgramFiles(x86)}\Steam\steamapps\common\StickFightTheGame"
Set-Location $Sf

$zip = "$env:TEMP\BepInEx_win_x86_5.4.23.5.zip"
$uri = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip'
if (-not (Test-Path 'winhttp.dll') -or (Get-Item 'winhttp.dll').Length -lt 20000) {
    Write-Host "Installing BepInEx doorstop (winhttp.dll)..."
    Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $zip
    Expand-Archive -Force -Path $zip -DestinationPath $Sf
}

$ini = Get-Content 'doorstop_config.ini' -Raw
$ini = $ini -replace 'enabled = false', 'enabled = true'
Set-Content 'doorstop_config.ini' $ini -NoNewline

$desk = [Environment]::GetFolderPath('Desktop')
$oraclePs1 = Join-Path $PSScriptRoot 'jugar-oracle.ps1'
$deskBat = Join-Path $desk 'Jugar Stick Fight Oracle.bat'
@"
@echo off
title Stick Fight Oracle
powershell -NoProfile -ExecutionPolicy Bypass -File "$oraclePs1"
pause
"@ | Set-Content -Path $deskBat -Encoding ASCII
Write-Host "OK. Usa el acceso directo del escritorio: Jugar Stick Fight Oracle.bat"

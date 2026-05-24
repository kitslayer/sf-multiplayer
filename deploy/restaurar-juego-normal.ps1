# Deja Stick Fight como antes: MelonLoader + Mods + Steam sin oracle forzado.
$ErrorActionPreference = 'Stop'
$Sf = "${env:ProgramFiles(x86)}\Steam\steamapps\common\StickFightTheGame"
Set-Location $Sf

# MelonLoader / Mods / Plugins
if (Test-Path '_MelonLoader_backup') {
    if (Test-Path 'MelonLoader') { Remove-Item 'MelonLoader' -Recurse -Force }
    Rename-Item '_MelonLoader_backup' 'MelonLoader'
}
foreach ($pair in @(@('Mods','_Mods_Melon_backup'), @('Plugins','_Plugins_Melon_backup'))) {
    $name, $bak = $pair
    if (Test-Path $bak) {
        if (Test-Path $name) { Remove-Item $name -Recurse -Force }
        Rename-Item $bak $name
    }
}

# BepInEx doorstop OFF = Steam abre con MelonLoader (skins Melon). Plugins BepInEx no cargan en ese modo.
$ini = Get-Content 'doorstop_config.ini' -Raw
$ini = $ini -replace 'enabled = true', 'enabled = false'
Set-Content 'doorstop_config.ini' $ini -NoNewline

$desk = [Environment]::GetFolderPath('Desktop')
@('Stick Fight Oracle.lnk', 'Jugar Stick Fight Oracle.bat') | ForEach-Object {
    $p = Join-Path $desk $_
    if (Test-Path $p) { Remove-Item $p -Force }
}

Write-Host ''
Write-Host '=== Juego normal (Steam + Melon) ==='
Write-Host '1. Steam -> Stick Fight -> Propiedades -> Opciones de lanzamiento: BORRA todo (dejalo vacio).'
Write-Host '   Si tenias -address 69.53.117.43 eso te conectaba al oracle sin querer.'
Write-Host '2. Abre el juego desde Steam. Mods en carpeta Mods\ vuelven a cargar.'
Write-Host ''
Write-Host '=== Volver a oracle (despues) ==='
Write-Host '   deploy\instalar-cliente-oracle.ps1'
Write-Host '   Steam launch: -address 69.53.117.43 -port 1337'
Write-Host '   O sesion sin Melon: deploy\jugar-oracle.ps1'

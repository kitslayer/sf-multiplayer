# Restaura MelonLoader y desactiva BepInEx doorstop (juego normal Steam + skins)
$Sf = "${env:ProgramFiles(x86)}\Steam\steamapps\common\StickFightTheGame"
Set-Location $Sf

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

$ini = Get-Content 'doorstop_config.ini' -Raw
$ini = $ini -replace 'enabled = true', 'enabled = false'
Set-Content 'doorstop_config.ini' $ini -NoNewline
Write-Host 'MelonLoader restaurado. Abre Stick Fight desde Steam (sin -address/-port en launch options si no quieres oracle).'

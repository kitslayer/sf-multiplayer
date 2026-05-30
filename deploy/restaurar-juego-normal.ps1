# Deja Stick Fight listo para Steam normal (vanilla + Melon si lo tenias).
# NO borra nada: solo renombra / restaura desde backups (.bak, .oracle-off).
# Team: kitslayer + AlkaDev
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'team-sf-multiplayer.ps1')
Show-SfMultiplayerTeamInfo

$Sf = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\StickFightTheGame'
if (-not (Test-Path (Join-Path $Sf 'StickFight.exe'))) {
    throw "No encuentro Stick Fight en $Sf"
}
Set-Location $Sf

function Move-AsideIfPresent {
    param([string]$Path, [string]$Suffix = '.steam-normal-off')
    if (-not (Test-Path $Path)) { return }
    $off = $Path + $Suffix
    if (Test-Path $off) { return }
    Rename-Item $Path $off -Force
    Write-Host "  Desactivado: $(Split-Path $Path -Leaf) -> $(Split-Path $off -Leaf)"
}

function Restore-FromOff {
    param([string]$Name, [string[]]$OffSuffixes = @('.oracle-off', '.off.oracle', '.steam-normal-off'))
    foreach ($suf in $OffSuffixes) {
        $off = Join-Path $Sf ($Name + $suf)
        if (Test-Path $off) {
            $live = Join-Path $Sf $Name
            if (Test-Path $live) { Move-AsideIfPresent $live '.steam-normal-off' }
            Rename-Item $off $Name -Force
            Write-Host "  Restaurado: $Name (desde $suf)"
            return $true
        }
    }
    return $false
}

Write-Host '=== [1/5] Assembly-CSharp.dll -> vanilla ===' -ForegroundColor Cyan
$Managed = Join-Path $Sf 'StickFight_Data\Managed'
$Asm = Join-Path $Managed 'Assembly-CSharp.dll'
$VanillaBak = Join-Path $Managed 'Assembly-CSharp.dll.vanilla.bak'
$SrvActive = Join-Path $Managed 'Assembly-CSharp.dll.srv-active.bak'
if (Test-Path $VanillaBak) {
    if (Test-Path $Asm) {
        $h = (Get-FileHash $Asm).Hash
        $vh = (Get-FileHash $VanillaBak).Hash
        if ($h -ne $vh) {
            if (-not (Test-Path $SrvActive)) { Copy-Item $Asm $SrvActive -Force }
            Copy-Item $VanillaBak $Asm -Force
            Write-Host '  Assembly-CSharp.dll restaurado desde .vanilla.bak (srv guardado en .srv-active.bak).'
        } else {
            Write-Host '  Assembly-CSharp.dll ya es vanilla.'
        }
    }
} else {
    Write-Host '  AVISO: no hay .vanilla.bak — si el juego raro en MP, reinstala Stick Fight en Steam.' -ForegroundColor Yellow
}

Write-Host '=== [2/5] BepInEx doorstop OFF (Steam sin oracle) ===' -ForegroundColor Cyan
$iniPath = Join-Path $Sf 'doorstop_config.ini'
if (Test-Path $iniPath) {
    $ini = Get-Content $iniPath -Raw
    $ini = $ini -replace 'enabled = true', 'enabled = false'
    Set-Content $iniPath $ini -NoNewline
    Write-Host '  doorstop_config.ini: enabled = false'
}

Write-Host '=== [3/5] Plugins BepInEx oracle fuera del camino ===' -ForegroundColor Cyan
$Plug = Join-Path $Sf 'BepInEx\plugins'
foreach ($dll in @('SFClientRecon.dll', 'SFHeadlessHost.dll', 'SFBoxFix.dll')) {
    $p = Join-Path $Plug $dll
    Move-AsideIfPresent $p '.oracle-off'
}
# Restaurar host si estaba en .oracle-client-off
$hostOff = Join-Path $Plug 'SFHeadlessHost.dll.oracle-client-off'
if (Test-Path $hostOff) { Move-AsideIfPresent (Join-Path $Plug 'SFHeadlessHost.dll') '.oracle-off' }

Write-Host '=== [4/5] MelonLoader / Mods / version.dll ===' -ForegroundColor Cyan
foreach ($dir in @('MelonLoader', 'MLLoader', 'Mods', 'Plugins')) {
    Restore-FromOff $dir | Out-Null
}
# Backup Melon desde restaurar antiguo
if (Test-Path '_MelonLoader_backup') {
    if (Test-Path 'MelonLoader') { Move-AsideIfPresent (Join-Path $Sf 'MelonLoader') '.steam-normal-off' }
    Rename-Item (Join-Path $Sf '_MelonLoader_backup') (Join-Path $Sf 'MelonLoader') -Force
    Write-Host '  Restaurado MelonLoader desde _MelonLoader_backup'
}
foreach ($pair in @(@('Mods','_Mods_Melon_backup'), @('Plugins','_Plugins_Melon_backup'))) {
    $name, $bak = $pair
    $bakPath = Join-Path $Sf $bak
    if (Test-Path $bakPath) {
        $live = Join-Path $Sf $name
        if (Test-Path $live) { Move-AsideIfPresent $live '.steam-normal-off' }
        Rename-Item $bakPath (Join-Path $Sf $name) -Force
        Write-Host "  Restaurado $name desde $bak"
    }
}
$verBak = Join-Path $Sf 'version.dll.pre-oracle.bak'
$ver = Join-Path $Sf 'version.dll'
if ((Test-Path $verBak) -and -not (Test-Path $ver)) {
    Copy-Item $verBak $ver -Force
    Write-Host '  Restaurado version.dll (Melon) desde .pre-oracle.bak'
}

Write-Host '=== [5/5] Steam launch options ===' -ForegroundColor Cyan
Write-Host ''
Write-Host '=== Listo: jugar en Steam (normal) ===' -ForegroundColor Green
Write-Host '1. Steam -> Stick Fight -> Propiedades -> Opciones de lanzamiento: DEJALO VACIO.'
Write-Host '   (Si tenias -address 69.53.117.43 -port 1337 quitalo o seguis yendo al oracle.)'
Write-Host '2. Abri Stick Fight desde Steam como siempre.'
Write-Host '3. Nada se borro: oracle en *.oracle-off, vanilla en *.vanilla.bak'
Write-Host ''
Write-Host 'Volver al oracle despues:'
Write-Host '  deploy\instalar-cliente-oracle.ps1'
Write-Host '  deploy\jugar-oracle.ps1'

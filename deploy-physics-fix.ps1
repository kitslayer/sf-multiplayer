# Build + deploy physics-fix plugins (SFHeadlessHost + SFClientRecon)
# Team: kitslayer + AlkaDev
param(
    [switch]$DeployVps,
    [switch]$InstallLocal
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
. (Join-Path $Root "deploy\team-sf-multiplayer.ps1")
Show-SfMultiplayerTeamInfo
$Sf = "${env:ProgramFiles(x86)}\Steam\steamapps\common\StickFightTheGame"
$Refs = Join-Path $Root "sf-headless-host\refs"

if (-not (Test-Path $Refs)) {
    New-Item -ItemType Directory -Force -Path $Refs | Out-Null
    Copy-Item "$Sf\StickFight_Data\Managed\Assembly-CSharp.dll" $Refs -Force
    Copy-Item "$Sf\StickFight_Data\Managed\UnityEngine.dll" $Refs -Force
    if (Test-Path "$Sf\BepInEx\core\BepInEx.dll") { Copy-Item "$Sf\BepInEx\core\BepInEx.dll" $Refs -Force }
    if (Test-Path "$Sf\BepInEx\core\0Harmony.dll") { Copy-Item "$Sf\BepInEx\core\0Harmony.dll" $Refs -Force }
}

$ClientRefs = Join-Path $Root "sf-client-recon\refs"
if (-not (Test-Path $ClientRefs)) {
    cmd /c ('mklink /J "{0}" "{1}"' -f $ClientRefs, $Refs)
}

Push-Location (Join-Path $Root "sf-headless-host")
dotnet build -c Release
Pop-Location
Push-Location (Join-Path $Root "sf-client-recon")
dotnet build -c Release
Pop-Location
Push-Location (Join-Path $Root "sf-box-fix")
dotnet build -c Release
Pop-Location

# Use ${HostDll}: bare $HostDll is parsed as $Host + "Dll" in PowerShell.
$HostDll = Join-Path $Root "sf-headless-host\bin\Release\SFHeadlessHost.dll"
$ClientDll = Join-Path $Root "sf-client-recon\bin\Release\SFClientRecon.dll"
$BoxFixDll = Join-Path $Root "sf-box-fix\bin\Release\SFBoxFix.dll"
$Dist = Join-Path $Root "dist"
if (Test-Path $Dist) {
    Copy-Item ${HostDll} $Dist -Force
    Copy-Item $ClientDll $Dist -Force
    Copy-Item $BoxFixDll $Dist -Force
}

if ($InstallLocal) {
    $Plug = Join-Path $Sf "BepInEx\plugins"
    Copy-Item $ClientDll $Plug -Force
    $cfgDir = Join-Path $Sf "BepInEx\config"
    New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
    @"
69.53.117.43
1337
"@ | Set-Content -Path (Join-Path $cfgDir "sf-oracle-endpoint.txt") -Encoding ASCII
    $hostOff = Join-Path $Plug 'SFHeadlessHost.dll.oracle-client-off'
    $hostPlug = Join-Path $Plug 'SFHeadlessHost.dll'
    if (Test-Path $hostPlug) {
        if (-not (Test-Path $hostOff)) { Move-Item $hostPlug $hostOff -Force }
        else { Remove-Item $hostPlug -Force -ErrorAction SilentlyContinue }
    }
    Write-Host "Installed SFClientRecon only (SFHeadlessHost disabled on client)."
}

if ($DeployVps) {
    $Key = Join-Path $env:USERPROFILE '.ssh\sf_oracle_alka'
    $Remote = 'sfdev@69.53.117.43:/home/miles/sf-oracle/install/BepInEx/plugins'
    scp -i $Key -P 2222 ${HostDll} "${Remote}/SFHeadlessHost.dll"
    scp -i $Key -P 2222 $BoxFixDll "${Remote}/SFBoxFix.dll"
    Write-Host 'Uploaded SFHeadlessHost.dll + SFBoxFix.dll — restart: sudo systemctl restart sf-oracle.service'
    Write-Host 'Verify log: grep -E "SFBoxFix v0.2.4|SFHeadlessHost" /tmp/sf-oracle-plugin-11337.log | tail -20'
}

Write-Host ('OK Host DLL:    ' + ${HostDll})
Write-Host ('OK BoxFix DLL:  ' + $BoxFixDll)
Write-Host ('OK Client DLL:  ' + $ClientDll)

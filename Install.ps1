[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$Upgrade
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$bundle = Join-Path $root 'tmp\PlantAutomationIsoTemplateRestore.bundle'
$destination = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\PlantAutomationIsoTemplateRestore.bundle'
if ((Get-Process acad -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*AutoCAD 2027*' })) {
    throw 'Close Plant 3D 2027 before changing the bundle or ISO style.'
}
if ($Uninstall) {
    if (Test-Path -LiteralPath $destination) {
        $plugins = [System.IO.Path]::GetFullPath((Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins')) + [System.IO.Path]::DirectorySeparatorChar
        if (-not ([System.IO.Path]::GetFullPath($destination)).StartsWith($plugins, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe uninstall path.' }
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    Write-Output 'Bundle removed. Style XML files retained.'
    return
}
if (-not (Test-Path -LiteralPath (Join-Path $bundle 'PackageContents.xml'))) { throw 'Run Build.ps1 first.' }
if ($Upgrade) {
    $installedDll = Join-Path $destination 'Contents\Win64\PlantAutomation.IsoTemplateRestore.dll'
    $newDll = Join-Path $bundle 'Contents\Win64\PlantAutomation.IsoTemplateRestore.dll'
    if (-not (Test-Path -LiteralPath $installedDll)) { throw "Installed DLL missing: $installedDll" }
    $backup = Join-Path $root ('tmp\backup\PlantAutomation.IsoTemplateRestore.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.dll')
    New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
    Copy-Item -LiteralPath $installedDll -Destination $backup
    Copy-Item -LiteralPath $newDll -Destination $installedDll -Force
    Write-Output "Upgraded DLL: $installedDll"
    Write-Output "Previous DLL: $backup"
    Write-Output 'Style XML files retained.'
    return
}
if (Test-Path -LiteralPath $destination) { throw "Existing bundle found; remove it explicitly before replacing: $destination" }
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
Copy-Item -LiteralPath $bundle -Destination $destination -Recurse
Write-Output "Installed bundle: $destination"
Write-Output 'Each ISO style creates PlantAutomation.IsoBlocks.xml beside its Iso.dwt.'

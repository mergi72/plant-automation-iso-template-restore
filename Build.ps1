[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$project = Join-Path $root 'src\PlantAutomation.IsoTemplateRestore\PlantAutomation.IsoTemplateRestore.csproj'
$bundle = Join-Path $root 'tmp\PlantAutomationIsoTemplateRestore.bundle'
& dotnet build $project -c Release --nologo -v quiet -p:NoWarn=MSB3277
if ($LASTEXITCODE -ne 0) { throw 'ISO template restore build failed.' }
if (Test-Path -LiteralPath $bundle) {
    $resolved = [System.IO.Path]::GetFullPath($bundle)
    $tmpRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'tmp')) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tmpRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe bundle path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
$win64 = Join-Path $bundle 'Contents\Win64'
New-Item -ItemType Directory -Path $win64 -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Bundle\PackageContents.xml') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $root 'src\PlantAutomation.IsoTemplateRestore\bin\Release\net10.0-windows\PlantAutomation.IsoTemplateRestore.dll') -Destination $win64
Write-Output "Bundle ready: $bundle"

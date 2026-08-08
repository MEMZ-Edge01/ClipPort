[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExternalContentDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestTemplate = Join-Path $repoRoot 'packaging\ShellIntegration\AppxManifest.xml.in'
$iconPath = Join-Path $repoRoot 'src\ClipPort\Assets\Icons\clipport-app-icon.png'
$externalContentPath = [IO.Path]::GetFullPath($ExternalContentDirectory)
$registrationDirectory = Join-Path $externalContentPath 'ShellIntegration.Development'
$packageName = 'MEMZEdge01.ClipPort.ShellIntegration'

if (-not (Test-Path -LiteralPath $externalContentPath -PathType Container)) {
    throw "External content directory does not exist: $externalContentPath"
}

foreach ($requiredFile in @('ClipPort.exe', 'ClipPort.ShellExtension.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $externalContentPath $requiredFile) -PathType Leaf)) {
        throw "External content is missing required file: $requiredFile"
    }
}

$existingPackage = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($null -ne $existingPackage) {
    $existingPackage | ForEach-Object {
        Remove-AppxPackage -Package $_.PackageFullName -ErrorAction Stop
    }
}

if (Test-Path -LiteralPath $registrationDirectory) {
    Remove-Item -LiteralPath $registrationDirectory -Recurse -Force
}

$assetsDirectory = Join-Path $registrationDirectory 'Assets'
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
$manifest = (Get-Content -LiteralPath $manifestTemplate -Raw -Encoding utf8).
    Replace('__PUBLISHER__', 'CN=ClipPort Development')
Set-Content -LiteralPath (Join-Path $registrationDirectory 'AppxManifest.xml') `
    -Value $manifest -Encoding utf8

foreach ($assetName in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
    Copy-Item -LiteralPath $iconPath -Destination (Join-Path $assetsDirectory $assetName)
}

# Loose-manifest registration is the Windows-supported development path for a
# sparse package. The production installer installs the signed MSIX instead.
Add-AppxPackage `
    -Register (Join-Path $registrationDirectory 'AppxManifest.xml') `
    -ExternalLocation $externalContentPath `
    -ErrorAction Stop

Get-AppxPackage -Name $packageName | Select-Object `
    Name, PackageFullName, Status, InstallLocation, IsDevelopmentMode, SignatureKind

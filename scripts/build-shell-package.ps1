[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExternalContentDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$Publisher,
    [string]$CertificateThumbprint,
    [switch]$UnsignedDevelopmentPackage
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestTemplate = Join-Path $repoRoot 'packaging\ShellIntegration\AppxManifest.xml.in'
$iconPath = Join-Path $repoRoot 'src\ClipPort\Assets\Icons\clipport-app-icon.png'
$externalContentPath = [IO.Path]::GetFullPath($ExternalContentDirectory)
if (-not (Test-Path -LiteralPath $externalContentPath -PathType Container)) {
    throw "External content directory does not exist: $externalContentPath"
}
foreach ($requiredFile in @('ClipPort.exe', 'ClipPort.ShellExtension.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $externalContentPath $requiredFile) -PathType Leaf)) {
        throw "External content is missing required file: $requiredFile"
    }
}

$kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$sdkVersionDirectory = Get-ChildItem -LiteralPath $kitsBin -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if ($null -eq $sdkVersionDirectory) {
    throw 'No Windows SDK tool directory was found.'
}
$makeAppx = Join-Path $sdkVersionDirectory.FullName 'x64\makeappx.exe'
$signTool = Join-Path $sdkVersionDirectory.FullName 'x64\signtool.exe'
foreach ($toolPath in @($makeAppx, $signTool)) {
    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
        throw "Required Windows SDK tool was not found: $toolPath"
    }
}

$operationId = [Guid]::NewGuid().ToString('N')
$stagingDirectory = Join-Path ([IO.Path]::GetTempPath()) "ClipPort-ShellPackage-$operationId"
try {
    $assetsDirectory = Join-Path $stagingDirectory 'Assets'
    New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
    if ($UnsignedDevelopmentPackage) {
        $Publisher = 'CN=ClipPort Development, OID.2.25.311729368913984317654407730594956997722=1'
    }
    elseif ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw 'A certificate thumbprint is required for a distributable shell package.'
    }
    $manifest = (Get-Content -LiteralPath $manifestTemplate -Raw -Encoding utf8).
        Replace('__PUBLISHER__', [Security.SecurityElement]::Escape($Publisher))
    Set-Content -LiteralPath (Join-Path $stagingDirectory 'AppxManifest.xml') `
        -Value $manifest -Encoding utf8
    foreach ($assetName in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
        Copy-Item -LiteralPath $iconPath -Destination (Join-Path $assetsDirectory $assetName)
    }

    $resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedOutputPath) -Force | Out-Null
    # Sparse packages reference executable and COM files in the external
    # installation directory, so MakeAppx path validation must be disabled.
    & $makeAppx pack /d $stagingDirectory /p $resolvedOutputPath /o /nv
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx failed with exit code $LASTEXITCODE."
    }
    if (-not $UnsignedDevelopmentPackage) {
        & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint $resolvedOutputPath
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool failed with exit code $LASTEXITCODE."
        }
        & $signTool verify /pa $resolvedOutputPath
        if ($LASTEXITCODE -ne 0) {
            throw "Signed package verification failed with exit code $LASTEXITCODE."
        }
    }
    Get-Item -LiteralPath $resolvedOutputPath
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

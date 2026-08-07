[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$ShellPackagePublisher = $env:CLIPPORT_SHELL_PACKAGE_PUBLISHER,
    [string]$ShellPackageCertificateThumbprint = $env:CLIPPORT_SHELL_PACKAGE_CERTIFICATE_THUMBPRINT,
    [switch]$UnsignedShellPackageForDevelopment
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot 'ClipPort.sln'
$projectPath = Join-Path $repoRoot 'src\ClipPort\ClipPort.csproj'
$artifactRoot = Join-Path $repoRoot 'artifacts'

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'ClipPort.csproj does not define a Version.'
}

$publishName = "ClipPort-$version-$Runtime"
$publishPath = Join-Path $artifactRoot $publishName
$operationId = [Guid]::NewGuid().ToString('N')
$stagingRoot = Join-Path $artifactRoot ".staging-$operationId"
$stagingPublishPath = Join-Path $stagingRoot $publishName
$backupPath = Join-Path $artifactRoot ".backup-$operationId-$publishName"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

function Remove-SafeArtifactChild {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedRoot = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $resolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $resolvedPath.StartsWith("$resolvedRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an item outside artifacts: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswherePath)) {
    throw 'Visual Studio Installer vswhere.exe was not found.'
}
$visualStudioPath = & $vswherePath -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($visualStudioPath)) {
    throw 'Visual Studio with the C++ x64 build tools was not found.'
}
$msbuildPath = Join-Path $visualStudioPath.Trim() 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuildPath)) {
    throw "MSBuild was not found: $msbuildPath"
}
$toolsetRoot = Get-ChildItem -LiteralPath (
    Join-Path $visualStudioPath.Trim() 'MSBuild\Microsoft\VC'
) -Directory -Filter 'v*' |
    Sort-Object Name -Descending |
    Select-Object -First 1 |
    ForEach-Object {
        Join-Path $_.FullName 'Platforms\x64\PlatformToolsets'
    }
$platformToolset = Get-ChildItem -LiteralPath $toolsetRoot -Directory -ErrorAction Stop |
    Where-Object Name -Match '^v\d+$' |
    Sort-Object Name -Descending |
    Select-Object -First 1 -ExpandProperty Name
if ([string]::IsNullOrWhiteSpace($platformToolset)) {
    throw 'No x64 C++ platform toolset was found.'
}

try {
    & $msbuildPath $solutionPath /restore /m `
        /p:Configuration=$Configuration /p:Platform=x64 `
        /p:PlatformToolset=$platformToolset
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build failed with exit code $LASTEXITCODE."
    }

    $managedBuildRoot = Join-Path $repoRoot "src\ClipPort\bin\x64\$Configuration"
    $requiredBuildFiles = @(
        'App.xbf',
        'MainWindow.xbf',
        'TraeWorkTheme.xbf',
        'SettingsView.xbf'
    )
    foreach ($fileName in $requiredBuildFiles) {
        $builtFile = Get-ChildItem -LiteralPath $managedBuildRoot -Recurse -File `
            -Filter $fileName -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $builtFile) {
            throw "Build validation failed; generated XBF is missing: $fileName"
        }
    }

    New-Item -ItemType Directory -Path $stagingPublishPath -Force | Out-Null
    dotnet publish $projectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:Platform=x64 `
        -p:PublishSingleFile=false `
        -p:RequireNativeCopyEngine=true `
        -o $stagingPublishPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $requiredFiles = @(
        'ClipPort.exe',
        'ClipPort.dll',
        'ClipPort.NativeCopy.dll',
        'ClipPort.ShellExtension.dll',
        'resources.pri',
        'Strings\zh-CN\Resources.resw',
        'Strings\en-US\Resources.resw'
    )
    foreach ($relativePath in $requiredFiles) {
        $requiredPath = Join-Path $stagingPublishPath $relativePath
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Publish validation failed; required file is missing: $relativePath"
        }
    }

    $hasShellPublisher = -not [string]::IsNullOrWhiteSpace($ShellPackagePublisher)
    $hasShellCertificate = -not [string]::IsNullOrWhiteSpace($ShellPackageCertificateThumbprint)
    if (-not $UnsignedShellPackageForDevelopment -and $hasShellPublisher -ne $hasShellCertificate) {
        throw 'Shell package publisher and certificate thumbprint must be supplied together.'
    }
    if ($UnsignedShellPackageForDevelopment) {
        & (Join-Path $PSScriptRoot 'build-shell-package.ps1') `
            -ExternalContentDirectory $stagingPublishPath `
            -OutputPath (Join-Path $stagingPublishPath 'ClipPort.ShellIntegration.msix') `
            -Publisher 'CN=ClipPort Development' `
            -UnsignedDevelopmentPackage
    }
    elseif ($hasShellPublisher) {
        & (Join-Path $PSScriptRoot 'build-shell-package.ps1') `
            -ExternalContentDirectory $stagingPublishPath `
            -OutputPath (Join-Path $stagingPublishPath 'ClipPort.ShellIntegration.msix') `
            -Publisher $ShellPackagePublisher `
            -CertificateThumbprint $ShellPackageCertificateThumbprint
        if ($LASTEXITCODE -ne 0) {
            throw "Shell integration package build failed with exit code $LASTEXITCODE."
        }
    }
    else {
        Write-Warning 'Shell integration MSIX was skipped because signing parameters were not supplied.'
    }

    if (Test-Path -LiteralPath $publishPath) {
        Move-Item -LiteralPath $publishPath -Destination $backupPath
    }
    try {
        Move-Item -LiteralPath $stagingPublishPath -Destination $publishPath
    }
    catch {
        if (Test-Path -LiteralPath $backupPath) {
            Move-Item -LiteralPath $backupPath -Destination $publishPath
        }
        throw
    }
    Remove-SafeArtifactChild -Path $backupPath
}
finally {
    Remove-SafeArtifactChild -Path $stagingRoot
}

Get-Item -LiteralPath (Join-Path $publishPath 'ClipPort.exe')

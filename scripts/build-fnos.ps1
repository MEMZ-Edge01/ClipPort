param(
    [switch]$SkipChecks
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsDirectory = Join-Path $repoRoot 'artifacts'
$stagingDirectory = Join-Path $artifactsDirectory 'fnos-staging'
$publishDirectory = Join-Path $artifactsDirectory 'fnos-publish'
$templateDirectory = Join-Path $repoRoot 'packaging/fnos/clipport'
$webDirectory = Join-Path $repoRoot 'src/ClipPort.FnOS.Web'
$serverProject = Join-Path $repoRoot 'src/ClipPort.FnOS.Server/ClipPort.FnOS.Server.csproj'
$iconSource = Join-Path $repoRoot 'src/ClipPort/Assets/Icons/clipport-app-icon.png'

function Assert-ArtifactPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($artifactsDirectory) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside artifacts: $resolved"
    }
}

function Reset-Directory([string]$Path) {
    Assert-ArtifactPath $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Invoke-Checked([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Command exited with code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

$projectText = Get-Content -LiteralPath (Join-Path $repoRoot 'src/ClipPort/ClipPort.csproj') -Raw
$versionMatch = [regex]::Match($projectText, '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) {
    throw 'Could not read the ClipPort version from ClipPort.csproj.'
}
$version = $versionMatch.Groups[1].Value

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
Reset-Directory $stagingDirectory
Reset-Directory $publishDirectory

$npm = if ($env:OS -eq 'Windows_NT') { 'npm.cmd' } else { 'npm' }
Invoke-Checked $npm @('ci') $webDirectory
if (-not $SkipChecks) {
    Invoke-Checked $npm @('run', 'lint') $webDirectory
    Invoke-Checked $npm @('run', 'typecheck') $webDirectory
    Invoke-Checked $npm @('test') $webDirectory
    Invoke-Checked 'dotnet' @('run', '--project', 'tests/ClipPort.CoreTests/ClipPort.CoreTests.csproj', '-c', 'Release') $repoRoot
    Invoke-Checked 'dotnet' @('test', 'tests/ClipPort.FnOS.Tests/ClipPort.FnOS.Tests.csproj', '-c', 'Release') $repoRoot
}
Invoke-Checked $npm @('run', 'build') $webDirectory
Invoke-Checked 'dotnet' @(
    'publish', $serverProject,
    '-c', 'Release',
    '-r', 'linux-x64',
    '--self-contained', 'true',
    '-o', $publishDirectory,
    '-p:PublishSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
) $repoRoot

# Project-reference PDBs can survive an incremental cross-OS build even when
# the entry project disables symbols. Keep the FPK deterministic and free of
# development artifacts by removing them only from the validated publish root.
Assert-ArtifactPath $publishDirectory
Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter '*.pdb' |
    Remove-Item -Force

Copy-Item -Path (Join-Path $templateDirectory '*') -Destination $stagingDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $stagingDirectory 'LICENSE') -Force
$manifestPath = Join-Path $stagingDirectory 'manifest'
$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifest = [regex]::Replace($manifest, '(?m)^version=.*$', "version=$version")
[IO.File]::WriteAllText($manifestPath, $manifest, [Text.UTF8Encoding]::new($false))

$serverDirectory = Join-Path $stagingDirectory 'app/server'
New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $serverDirectory -Recurse -Force
$wwwroot = Join-Path $serverDirectory 'wwwroot'
if (Test-Path -LiteralPath $wwwroot) {
    Remove-Item -LiteralPath $wwwroot -Recurse -Force
}
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Copy-Item -Path (Join-Path $webDirectory 'dist/*') -Destination $wwwroot -Recurse -Force

Invoke-Checked 'node' @(
    'scripts/generateIcons.mjs',
    $iconSource,
    $stagingDirectory
) $webDirectory

$gitKeep = Join-Path $stagingDirectory 'wizard/.gitkeep'
if (Test-Path -LiteralPath $gitKeep) {
    Remove-Item -LiteralPath $gitKeep -Force
}

& (Join-Path $repoRoot 'scripts/audit-fnos-package.ps1') -PackageRoot $stagingDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'fnOS package audit failed.'
}

$fnpackVersion = '1.2.3'
$onWindows = $env:OS -eq 'Windows_NT'
$fnpackAsset = if ($onWindows) { 'fnpack-1.2.3-windows-amd64' } else { 'fnpack-1.2.3-linux-amd64' }
$fnpackHash = if ($onWindows) {
    'D7AF4BD716B009C58F5BCD931615F39DB121E7D4B75DC759E575C4FB2879B6EE'
} else {
    '54B97FA7B70968C4D05C79840F5DAEFF508957D0BB2062FDB0376D00D9615C93'
}
$toolDirectory = Join-Path $artifactsDirectory "tools/fnpack/$fnpackVersion"
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
$fnpackPath = Join-Path $toolDirectory $(if ($onWindows) { 'fnpack.exe' } else { 'fnpack' })
if (-not (Test-Path -LiteralPath $fnpackPath) -or
    (Get-FileHash -LiteralPath $fnpackPath -Algorithm SHA256).Hash -ne $fnpackHash) {
    Invoke-WebRequest -UseBasicParsing -Uri "https://static2.fnnas.com/fnpack/$fnpackAsset" -OutFile $fnpackPath
}
if ((Get-FileHash -LiteralPath $fnpackPath -Algorithm SHA256).Hash -ne $fnpackHash) {
    throw "fnpack $fnpackVersion SHA-256 verification failed."
}
if (-not $onWindows) {
    & chmod 755 $fnpackPath
}

$rawPackage = Join-Path $artifactsDirectory 'clipport.fpk'
if (Test-Path -LiteralPath $rawPackage) {
    Remove-Item -LiteralPath $rawPackage -Force
}
Invoke-Checked $fnpackPath @('build', '--directory', $stagingDirectory) $artifactsDirectory
if (-not (Test-Path -LiteralPath $rawPackage -PathType Leaf)) {
    throw 'fnpack did not produce an FPK in the artifacts directory.'
}

$finalPackage = Join-Path $artifactsDirectory "ClipPort-$version-fnos-x86.fpk"
Move-Item -LiteralPath $rawPackage -Destination $finalPackage -Force
$hash = (Get-FileHash -LiteralPath $finalPackage -Algorithm SHA256).Hash.ToLowerInvariant()
$hashFile = "$finalPackage.sha256"
[IO.File]::WriteAllText(
    $hashFile,
    "$hash  $([IO.Path]::GetFileName($finalPackage))`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "Created $finalPackage"
Write-Host "Created $hashFile"

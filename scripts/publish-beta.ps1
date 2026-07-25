[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\EZDIT\EZDIT.csproj'
$artifactRoot = Join-Path $repoRoot 'artifacts'

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'EZDIT.csproj does not define a Version.'
}

$publishName = "EZDIT-$version-$Runtime"
$publishPath = Join-Path $artifactRoot $publishName
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Platform=x64 `
    -p:PublishSingleFile=false `
    -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$resolvedRoot = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
$resolvedKeep = [System.IO.Path]::GetFullPath($publishPath).TrimEnd('\')
$oldArtifacts = @(Get-ChildItem -LiteralPath $artifactRoot -Force | Where-Object {
    [System.IO.Path]::GetFullPath($_.FullName).TrimEnd('\') -ne $resolvedKeep
})

foreach ($item in $oldArtifacts) {
    $resolvedItem = [System.IO.Path]::GetFullPath($item.FullName).TrimEnd('\')
    $resolvedParent = [System.IO.Path]::GetDirectoryName($resolvedItem).TrimEnd('\')
    if ($resolvedParent -ne $resolvedRoot) {
        throw "Refusing to remove an item outside artifacts: $resolvedItem"
    }
    Remove-Item -LiteralPath $item.FullName -Recurse -Force
}

Get-Item -LiteralPath (Join-Path $publishPath 'EZDIT.exe')

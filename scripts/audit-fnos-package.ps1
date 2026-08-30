param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot
)

$ErrorActionPreference = 'Stop'
$packagePath = [IO.Path]::GetFullPath($PackageRoot)

function Require-File([string]$RelativePath) {
    $path = Join-Path $packagePath $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required fnOS package file is missing: $RelativePath"
    }
}

@(
    'manifest',
    'config/privilege',
    'config/resource',
    'cmd/main',
    'app/ui/config',
    'app/server/ClipPort.FnOS.Server',
    'app/server/wwwroot/index.html',
    'app/server/wwwroot/callback.html',
    'ICON.PNG',
    'ICON_256.PNG',
    'app/ui/images/icon_64.png',
    'app/ui/images/icon_256.png'
) | ForEach-Object { Require-File $_ }

$manifest = Get-Content -LiteralPath (Join-Path $packagePath 'manifest') -Raw
@(
    'appname=clipport',
    'platform=x86',
    'micro_app=true',
    'os_min_version=1.2.0401',
    'disable_authorization_path=false'
) | ForEach-Object {
    if ($manifest -notmatch "(?m)^$([regex]::Escape($_))$") {
        throw "fnOS manifest requirement is missing: $_"
    }
}

$privilege = Get-Content -LiteralPath (Join-Path $packagePath 'config/privilege') -Raw | ConvertFrom-Json
if ($privilege.defaults.'run-as' -ne 'package') {
    throw 'ClipPort fnOS must run as the dedicated package user.'
}

$resource = Get-Content -LiteralPath (Join-Path $packagePath 'config/resource') -Raw | ConvertFrom-Json
$expectedScopes = @('trim.file.sharedAccess', 'trim.file.userAcl', 'trim.file.path')
$actualScopes = @($resource.'api-scope' | Sort-Object)
if (($actualScopes -join '|') -ne (($expectedScopes | Sort-Object) -join '|')) {
    throw 'config/resource must declare exactly the three reviewed file scopes.'
}

$ui = Get-Content -LiteralPath (Join-Path $packagePath 'app/ui/config') -Raw | ConvertFrom-Json
$entry = $ui.'.url'.'clipport.main'
if ($entry.gatewayPrefix -ne '/app/clipport' -or
    $entry.gatewaySocket -ne 'app.sock' -or
    $entry.allUsers -ne $false -or
    $entry.control.accessPerm -ne 'readonly') {
    throw 'The admin-only unified gateway entry is invalid.'
}

$forbidden = Get-ChildItem -LiteralPath $packagePath -Recurse -File | Where-Object {
    $_.Extension -in @('.pdb', '.map') -or $_.FullName -match '[\\/]node_modules[\\/]'
}
if ($forbidden) {
    throw "Development-only files entered the FPK: $($forbidden.FullName -join ', ')"
}

if ($env:TRIM_API_TOKEN) {
    $leaks = Get-ChildItem -LiteralPath $packagePath -Recurse -File | Select-String -SimpleMatch $env:TRIM_API_TOKEN -ErrorAction SilentlyContinue
    if ($leaks) {
        throw 'The current TRIM_API_TOKEN value was found inside the package.'
    }
}

Write-Host "fnOS package audit passed: $packagePath"

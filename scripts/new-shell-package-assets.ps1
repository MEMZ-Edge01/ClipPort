[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$DestinationDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$resolvedSourcePath = [IO.Path]::GetFullPath($SourcePath)
$resolvedDestinationDirectory = [IO.Path]::GetFullPath($DestinationDirectory)
if (-not (Test-Path -LiteralPath $resolvedSourcePath -PathType Leaf)) {
    throw "Shell package icon source does not exist: $resolvedSourcePath"
}

New-Item -ItemType Directory -Path $resolvedDestinationDirectory -Force | Out-Null
$assetSizes = [ordered]@{
    'StoreLogo.png' = 50
    'Square44x44Logo.png' = 44
    'Square150x150Logo.png' = 150
}

$sourceImage = [Drawing.Image]::FromFile($resolvedSourcePath)
try {
    foreach ($asset in $assetSizes.GetEnumerator()) {
        $size = [int]$asset.Value
        $bitmap = New-Object Drawing.Bitmap($size, $size)
        try {
            $bitmap.SetResolution(96, 96)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
                $destinationRectangle = New-Object Drawing.Rectangle(0, 0, $size, $size)
                $graphics.DrawImage(
                    $sourceImage,
                    $destinationRectangle,
                    0,
                    0,
                    $sourceImage.Width,
                    $sourceImage.Height,
                    [Drawing.GraphicsUnit]::Pixel)
            }
            finally {
                $graphics.Dispose()
            }

            $destinationPath = Join-Path $resolvedDestinationDirectory $asset.Key
            $bitmap.Save($destinationPath, [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $sourceImage.Dispose()
}

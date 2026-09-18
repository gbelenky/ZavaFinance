#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates shared Zava Finance native Activity icons for Teams and Microsoft 365.

.DESCRIPTION
    Writes only appPackage\icons\color.png (192x192) and outline.png (32x32)
    by default. Requires Windows and System.Drawing; no external assets or fonts.

    The custom numeral has a rising shoulder and an ascending diagonal facet:
    one connected financial story.
    The color icon uses a full-bleed teal (#087F8C) background, a white numeral,
    and a navy (#102A43) lower facet. Use #087F8C as the app accent color.
    The outline retains the complete numeral as flat white on transparency.

.EXAMPLE
    .\scripts\new-icons.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'appPackage\icons')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$accentTeal = [System.Drawing.Color]::FromArgb(255, 8, 127, 140)
$navy = [System.Drawing.Color]::FromArgb(255, 16, 42, 67)

# Original vector geometry on a 32-unit grid, shared by both icon sizes.
$onePoints = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(8, 12)
    [System.Drawing.PointF]::new(15, 6)
    [System.Drawing.PointF]::new(20, 6)
    [System.Drawing.PointF]::new(20, 23)
    [System.Drawing.PointF]::new(25, 23)
    [System.Drawing.PointF]::new(25, 27)
    [System.Drawing.PointF]::new(9, 27)
    [System.Drawing.PointF]::new(9, 23)
    [System.Drawing.PointF]::new(15, 23)
    [System.Drawing.PointF]::new(15, 12)
    [System.Drawing.PointF]::new(10.5, 16)
)

$facetPoints = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(15, 20)
    [System.Drawing.PointF]::new(20, 15)
    [System.Drawing.PointF]::new(20, 23)
    [System.Drawing.PointF]::new(25, 23)
    [System.Drawing.PointF]::new(25, 27)
    [System.Drawing.PointF]::new(9, 27)
    [System.Drawing.PointF]::new(9, 23)
    [System.Drawing.PointF]::new(15, 23)
)

function New-OneIcon {
    param(
        [string] $Path,
        [int] $Size,
        [switch] $Outline
    )

    $supersampling = 4
    $renderSize = $Size * $supersampling
    $format = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
    $large = [System.Drawing.Bitmap]::new($renderSize, $renderSize, $format)
    $result = [System.Drawing.Bitmap]::new($Size, $Size, $format)
    $graphics = $null
    $outputGraphics = $null
    $facetBrush = $null

    try {
        $graphics = [System.Drawing.Graphics]::FromImage($large)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $background = if ($Outline) { [System.Drawing.Color]::Transparent } else { $accentTeal }
        $graphics.Clear($background)
        $scale = [float]($renderSize / 32.0)
        $graphics.ScaleTransform($scale, $scale)
        $graphics.FillPolygon([System.Drawing.Brushes]::White, $onePoints)

        if (-not $Outline) {
            # Clip the facet to the numeral so its shared edges remain a clean silhouette.
            $clipPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
            try {
                $clipPath.AddPolygon($onePoints)
                $graphics.SetClip($clipPath)
                $facetBrush = [System.Drawing.SolidBrush]::new($navy)
                $graphics.FillPolygon($facetBrush, $facetPoints)
            }
            finally {
                $clipPath.Dispose()
            }
        }

        $graphics.Dispose()
        $graphics = $null
        $outputGraphics = [System.Drawing.Graphics]::FromImage($result)
        $outputGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $outputGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $outputGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $destination = [System.Drawing.Rectangle]::new(0, 0, $Size, $Size)
        $outputGraphics.DrawImage(
            $large, $destination, 0, 0, $renderSize, $renderSize,
            [System.Drawing.GraphicsUnit]::Pixel
        )
        $outputGraphics.Dispose()
        $outputGraphics = $null

        if ($Outline) {
            # Preserve antialiasing in alpha only; Teams must receive pure white RGB.
            for ($y = 0; $y -lt $Size; $y++) {
                for ($x = 0; $x -lt $Size; $x++) {
                    $alpha = $result.GetPixel($x, $y).A
                    $result.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, 255, 255, 255))
                }
            }
        }
        else {
            # Bicubic sampling at the canvas edge can introduce partial alpha.
            for ($y = 0; $y -lt $Size; $y++) {
                for ($x = 0; $x -lt $Size; $x++) {
                    $pixel = $result.GetPixel($x, $y)
                    $result.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $pixel.R, $pixel.G, $pixel.B))
                }
            }
        }

        $result.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        if ($null -ne $graphics) { $graphics.Dispose() }
        if ($null -ne $outputGraphics) { $outputGraphics.Dispose() }
        if ($null -ne $facetBrush) { $facetBrush.Dispose() }
        $large.Dispose()
        $result.Dispose()
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$colorPath = Join-Path $OutputDirectory 'color.png'
$outlinePath = Join-Path $OutputDirectory 'outline.png'

New-OneIcon -Path $colorPath -Size 192
New-OneIcon -Path $outlinePath -Size 32 -Outline

Write-Host "Wrote $colorPath (192x192; teal #087F8C, navy #102A43)"
Write-Host "Wrote $outlinePath (32x32; flat white on transparency)"

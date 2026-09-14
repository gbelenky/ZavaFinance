#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates the Teams / Microsoft 365 Copilot app icons.

.DESCRIPTION
    Writes appPackage/color.png (192x192) and appPackage/outline.png (32x32).

    The design is an ascending three-bar chart with a trend arrow. Bars were chosen
    over a lettermark because the outline icon is rendered at 32 pixels and
    monochrome, where two or three glyphs of text are illegible.

    Teams renders outline.png as a silhouette and applies its own tint, so that file
    must be a single flat colour on transparency with no interior detail.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'appPackage')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Matches "accentColor" in appPackage/manifest.json. Teams draws the colour icon on
# that accent behind transparent pixels, so keeping them equal avoids a visible seam.
$brandBlue = [System.Drawing.Color]::FromArgb(255, 15, 108, 189)

function New-RoundedRectPath {
    param([float] $X, [float] $Y, [float] $W, [float] $H, [float] $Radius)

    $d = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Canvas {
    param([int] $Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    return @{ Bitmap = $bitmap; Graphics = $graphics }
}

# Bar geometry as fractions of the canvas, so both sizes stay visually identical.
$bars = @(
    @{ X = 0.180; Y = 0.560; W = 0.150; H = 0.260 },
    @{ X = 0.385; Y = 0.430; W = 0.150; H = 0.390 },
    @{ X = 0.590; Y = 0.270; W = 0.150; H = 0.550 }
)

function Add-Bars {
    param(
        $Graphics,
        [int] $Size,
        [System.Drawing.Brush] $Brush,
        [float] $CornerFraction,
        [float] $OffsetX = 0.0,
        [float] $OffsetY = 0.0
    )

    foreach ($b in $bars) {
        $x = [float](($b.X + $OffsetX) * $Size)
        $y = [float](($b.Y + $OffsetY) * $Size)
        $w = [float]($b.W * $Size)
        $h = [float]($b.H * $Size)
        $r = [Math]::Min([float]($CornerFraction * $Size), $w / 2)

        if ($r -lt 1) {
            $Graphics.FillRectangle($Brush, $x, $y, $w, $h)
        }
        else {
            $path = New-RoundedRectPath -X $x -Y $y -W $w -H $h -Radius $r
            $Graphics.FillPath($Brush, $path)
            $path.Dispose()
        }
    }
}

function New-ColorIcon {
    param([string] $Path, [int] $Size = 192)

    $c = New-Canvas -Size $Size
    $g = $c.Graphics

    $bg = New-Object System.Drawing.SolidBrush($brandBlue)
    $bgPath = New-RoundedRectPath -X 0 -Y 0 -W $Size -H $Size -Radius ([float]($Size * 0.22))
    $g.FillPath($bg, $bgPath)
    $bgPath.Dispose()
    $bg.Dispose()

    $white = [System.Drawing.Brushes]::White
    Add-Bars -Graphics $g -Size $Size -Brush $white -CornerFraction 0.022

    # Trend arrow: the agent's job is explaining what moved, so the mark shows movement.
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [float]($Size * 0.045))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $p1 = New-Object System.Drawing.PointF([float]($Size * 0.225), [float]($Size * 0.400))
    $p2 = New-Object System.Drawing.PointF([float]($Size * 0.430), [float]($Size * 0.285))
    $p3 = New-Object System.Drawing.PointF([float]($Size * 0.665), [float]($Size * 0.150))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@($p1, $p2, $p3))

    # Arrowhead as two strokes rather than a filled polygon; it stays crisp when scaled.
    $h1 = New-Object System.Drawing.PointF([float]($Size * 0.530), [float]($Size * 0.150))
    $h2 = New-Object System.Drawing.PointF([float]($Size * 0.665), [float]($Size * 0.285))
    $g.DrawLine($pen, $p3, $h1)
    $g.DrawLine($pen, $p3, $h2)
    $pen.Dispose()

    $g.Dispose()
    $c.Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $c.Bitmap.Dispose()
}

function New-OutlineIcon {
    param([string] $Path, [int] $Size = 32)

    $c = New-Canvas -Size $Size
    $g = $c.Graphics

    # Flat white on transparency. Teams recolours the silhouette, so anything other
    # than one solid colour is discarded or renders as noise.
    #
    # The bars are recentred here because the colour icon reserves its upper third for
    # the trend arrow, which the outline drops. Reusing that layout unchanged would
    # leave the silhouette visibly low and left in its 32-pixel box.
    $white = [System.Drawing.Brushes]::White
    Add-Bars -Graphics $g -Size $Size -Brush $white -CornerFraction 0.0 `
        -OffsetX 0.040 -OffsetY -0.045

    $g.Dispose()
    $c.Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $c.Bitmap.Dispose()
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$colorPath = Join-Path $OutputDirectory 'color.png'
$outlinePath = Join-Path $OutputDirectory 'outline.png'

New-ColorIcon -Path $colorPath
New-OutlineIcon -Path $outlinePath

Write-Host "Wrote $colorPath (192x192)"
Write-Host "Wrote $outlinePath (32x32)"

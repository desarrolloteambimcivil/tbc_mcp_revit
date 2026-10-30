<#
.SYNOPSIS
  Regenerates the installer wizard images (wizard-large.bmp, wizard-small.bmp) from the site's
  fence logo, so the installer and the website share one look. Run after changing brand colours.
#>
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$navy = [System.Drawing.Color]::FromArgb(0x12, 0x35, 0x5b)
$green = [System.Drawing.Color]::FromArgb(0x3c, 0xc4, 0x89)
$sky = [System.Drawing.Color]::FromArgb(0x8f, 0xb6, 0xe3)

# The site favicon: top rail, three posts, ground line, on a 32-unit grid.
function Draw-Logo([System.Drawing.Graphics]$g, [float]$x, [float]$y, [float]$size, [bool]$withTile) {
    $u = $size / 32
    if ($withTile) {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = 8 * $u
        $path.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90); $path.AddArc($x + $size - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
        $path.AddArc($x + $size - 2 * $r, $y + $size - 2 * $r, 2 * $r, 2 * $r, 0, 90); $path.AddArc($x, $y + $size - 2 * $r, 2 * $r, 2 * $r, 90, 90)
        $g.FillPath((New-Object System.Drawing.SolidBrush $navy), $path)
    }
    $pen = { param($c, $w) $p = New-Object System.Drawing.Pen $c, ($w * $u); $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p }
    $g.DrawLine((& $pen $green 2.5), $x + 6 * $u, $y + 11 * $u, $x + 26 * $u, $y + 11 * $u)
    foreach ($px in 9, 16, 23) { $g.DrawLine((& $pen ([System.Drawing.Color]::White) 2.5), $x + $px * $u, $y + 11 * $u, $x + $px * $u, $y + 25 * $u) }
    $g.DrawLine((& $pen $sky 2), $x + 6 * $u, $y + 25 * $u, $x + 26 * $u, $y + 25 * $u)
}

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    return $bmp, $g
}

# Large image (left side of the welcome and finish pages): 164x314 at 2x.
$bmp, $g = New-Canvas 328 628
$g.FillRectangle((New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(0, 628)), $navy, ([System.Drawing.Color]::FromArgb(0x0b, 0x22, 0x3b))), 0, 0, 328, 628)
Draw-Logo $g 84 120 160 $false
$center = New-Object System.Drawing.StringFormat; $center.Alignment = 'Center'
$g.DrawString('tbc-revit-mcp', (New-Object System.Drawing.Font 'Segoe UI Semibold', 30), [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 320, 328, 60), $center)
$g.DrawString('AI  ·  Revit 2022–2027', (New-Object System.Drawing.Font 'Segoe UI', 17), (New-Object System.Drawing.SolidBrush $green), (New-Object System.Drawing.RectangleF 0, 382, 328, 40), $center)
$g.DrawString('Nour Rouas · TeamBIMCivil', (New-Object System.Drawing.Font 'Segoe UI', 14), (New-Object System.Drawing.SolidBrush $sky), (New-Object System.Drawing.RectangleF 0, 560, 328, 36), $center)
$bmp.Save((Join-Path $PSScriptRoot 'wizard-large.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $g.Dispose(); $bmp.Dispose()

# Small image (top-right of the inner pages): 55x55 at 2x, on the white header background.
$bmp, $g = New-Canvas 110 110
$g.Clear([System.Drawing.Color]::White)
Draw-Logo $g 7 7 96 $true
$bmp.Save((Join-Path $PSScriptRoot 'wizard-small.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $g.Dispose(); $bmp.Dispose()
'wizard-large.bmp, wizard-small.bmp written'

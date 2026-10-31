<#
.SYNOPSIS
  Regenerates the installer wizard images (wizard-large.bmp, wizard-small.bmp) from the
  TeamBIMCivil logo files, so the installer and the website share the company look.
  Brand rules (GAIA brand guide): the logo is used as delivered, on white; red #E4332B only
  for accents without text; text in #4A4A4A and #808080. Run after changing the logo files.
#>
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$red = [System.Drawing.Color]::FromArgb(0xE4, 0x33, 0x2B)
$text = [System.Drawing.Color]::FromArgb(0x4A, 0x4A, 0x4A)
$grey = [System.Drawing.Color]::FromArgb(0x80, 0x80, 0x80)
$rule = [System.Drawing.Color]::FromArgb(0xE0, 0xE0, 0xE0)
$icon = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'teambimcivil_icono.png'))
$logo = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'teambimcivil_logo_h.png'))

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::White)
    return $bmp, $g
}

# Large image (left side of the welcome and finish pages): 164x314 at 2x.
$bmp, $g = New-Canvas 328 628
$g.FillRectangle((New-Object System.Drawing.SolidBrush $red), 0, 0, 8, 628)
$g.DrawImage($icon, 94, 96, 140, 140)
$center = New-Object System.Drawing.StringFormat; $center.Alignment = 'Center'
$g.DrawString('tbc-revit-mcp', (New-Object System.Drawing.Font 'Segoe UI Semibold', 25), (New-Object System.Drawing.SolidBrush $text), (New-Object System.Drawing.RectangleF 8, 272, 320, 50), $center)
$g.DrawString('Revit 2022–2027', (New-Object System.Drawing.Font 'Segoe UI', 15), (New-Object System.Drawing.SolidBrush $grey), (New-Object System.Drawing.RectangleF 8, 322, 320, 34), $center)
$g.FillRectangle((New-Object System.Drawing.SolidBrush $rule), 48, 520, 240, 2)
$w = 236; $h = [int]($logo.Height * $w / $logo.Width)
$g.DrawImage($logo, 50, 544, $w, $h)
$bmp.Save((Join-Path $PSScriptRoot 'wizard-large.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $g.Dispose(); $bmp.Dispose()

# Small image (top-right of the inner pages): 55x55 at 2x, on the white header background.
$bmp, $g = New-Canvas 110 110
$g.DrawImage($icon, 12, 12, 86, 86)
$bmp.Save((Join-Path $PSScriptRoot 'wizard-small.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $g.Dispose(); $bmp.Dispose()
$icon.Dispose(); $logo.Dispose()
'wizard-large.bmp, wizard-small.bmp written'

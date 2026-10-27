param([string]$OutDir = "$PSScriptRoot\..\installer")
# Installer artwork (own design, no game textures): a dark dirt-like pixel backdrop with the app's
# grass block, for Inno Setup's side panel (WizardImageFile) and header (WizardSmallImageFile),
# at 100/150/200% so it stays sharp on high-DPI screens.
Add-Type -AssemblyName System.Drawing

$icon = New-Object Drawing.Icon "$PSScriptRoot\..\app.ico", 256, 256
$block = $icon.ToBitmap()
$icon.Dispose()

function New-Canvas($w, $h) {
    $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'NearestNeighbor'
    $g.PixelOffsetMode = 'Half'
    return $bmp, $g
}

function Draw-Dirt($g, $w, $h, $cell) {
    $rnd = [Random]::new(4242)
    $shades = @(0x2E2118, 0x34261B, 0x281C14, 0x3A2A1E, 0x241911)
    for ($y = 0; $y -lt $h; $y += $cell) {
        for ($x = 0; $x -lt $w; $x += $cell) {
            $c = $shades[$rnd.Next(0, $shades.Count)]
            $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(($c -shr 16) -band 255, ($c -shr 8) -band 255, $c -band 255))
            $g.FillRectangle($brush, $x, $y, $cell, $cell)
            $brush.Dispose()
        }
    }
}

function Save-Bmp($bmp, $name) {
    $bmp.Save((Join-Path $OutDir $name), [Drawing.Imaging.ImageFormat]::Bmp)
}

foreach ($scale in 100, 150, 200) {
    $f = $scale / 100

    # Side panel: dirt backdrop, the block, the name.
    $w = [int](164 * $f); $h = [int](314 * $f)
    $bmp, $g = New-Canvas $w $h
    Draw-Dirt $g $w $h ([int](6 * $f))
    $size = [int](96 * $f)
    $g.DrawImage($block, [int](($w - $size) / 2), [int](70 * $f), $size, $size)
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $font = New-Object Drawing.Font 'Segoe UI Semibold', (15 * $f), ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'
    $rect = New-Object Drawing.RectangleF 0, (184 * $f), $w, (30 * $f)
    $shadow = New-Object Drawing.RectangleF ($f), (185 * $f), $w, (30 * $f)
    $g.DrawString('McServerTray', $font, [Drawing.Brushes]::Black, $shadow, $fmt)
    $g.DrawString('McServerTray', $font, [Drawing.Brushes]::White, $rect, $fmt)
    $small = New-Object Drawing.Font 'Segoe UI', (10 * $f), ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
    $grey = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0xB0, 0xB0, 0xB0))
    $g.DrawString('Minecraft servers in your tray', $small, $grey, (New-Object Drawing.RectangleF 0, (206 * $f), $w, (20 * $f)), $fmt)
    $g.Dispose()
    Save-Bmp $bmp "wizard-$scale.bmp"
    $bmp.Dispose()

    # Header: the block on the header's white.
    $s = [int](55 * $f)
    $bmp, $g = New-Canvas $s $s
    $g.Clear([Drawing.Color]::White)
    $pad = [int](5 * $f)
    $g.DrawImage($block, $pad, $pad, $s - 2 * $pad, $s - 2 * $pad)
    $g.Dispose()
    Save-Bmp $bmp "wizard-small-$scale.bmp"
    $bmp.Dispose()
}
$block.Dispose()

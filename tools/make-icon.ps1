param([string]$Out)
# An original 16x16 pixel-art "grass block" (own design, seeded noise), packed as a PNG-based .ico.
Add-Type -AssemblyName System.Drawing
$rnd = [Random]::new(1337)
$grass = @(0x5DA130, 0x6BB33A, 0x4F8F28, 0x77C044)
$dirt  = @(0x866043, 0x79553A, 0x96704E, 0x6B4A31)
$base = New-Object Drawing.Bitmap 16, 16
for ($y = 0; $y -lt 16; $y++) {
    for ($x = 0; $x -lt 16; $x++) {
        # Grass band with a ragged edge hanging into the dirt.
        $edge = 4 + ($rnd.Next(0, 3))
        $c = if ($y -lt $edge -or ($y -lt 7 -and $rnd.Next(0, 5) -eq 0)) { $grass[$rnd.Next(0, 4)] } else { $dirt[$rnd.Next(0, 4)] }
        $base.SetPixel($x, $y, [Drawing.Color]::FromArgb(255, ($c -shr 16) -band 255, ($c -shr 8) -band 255, $c -band 255))
    }
}
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $g.DrawImage($base, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [IO.File]::Create($Out)
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

param([string]$OutFile, [string]$PreviewFile)
Add-Type -AssemblyName System.Drawing

# Alles in einem 256er-Raster entworfen und 4-fach überabgetastet gezeichnet, dann herunterskaliert.
function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}
function Argb($a, $hex) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($a, $c) }

function Render([int]$size) {
    $ss = 4; $px = $size * $ss
    $big = New-Object System.Drawing.Bitmap $px, $px
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($px / 256, $px / 256)

    # Hintergrund: Blau -> Violett -> Pink
    $bgPath = RoundRect 0 0 256 256 58
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 256, 256), (Argb 255 '#2563EB'), (Argb 255 '#DB2777')
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @((Argb 255 '#2563EB'), (Argb 255 '#7C3AED'), (Argb 255 '#DB2777'))
    $blend.Positions = @([single]0, [single]0.55, [single]1)
    $bg.InterpolationColors = $blend
    $g.FillPath($bg, $bgPath)

    # Glanz in der oberen Hälfte
    $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, -1), (New-Object System.Drawing.PointF 0, 257), (Argb 70 '#FFFFFF'), (Argb 0 '#FFFFFF')
    $glossBlend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $glossBlend.Colors = @((Argb 64 '#FFFFFF'), (Argb 0 '#FFFFFF'), (Argb 0 '#FFFFFF'))
    $glossBlend.Positions = @([single]0, [single]0.6, [single]1)
    $gloss.InterpolationColors = $glossBlend
    $g.FillPath($gloss, $bgPath)

    $pad = 52; $gap = 20; $tile = 66; $r = 17
    $x1 = $pad; $x2 = $pad + $tile + $gap
    $shadow = New-Object System.Drawing.SolidBrush (Argb 45 '#1E1B4B')

    $tiles = @(
        @{ X = $x1; Y = $x1; A = 255 },
        @{ X = $x1; Y = $x2; A = 215 },
        @{ X = $x2; Y = $x2; A = 175 }
    )
    foreach ($t in $tiles) { $g.FillPath($shadow, (RoundRect $t.X ($t.Y + 5) $tile $tile $r)) }
    $g.FillEllipse($shadow, $x2, $x1 + 5, $tile, $tile)
    foreach ($t in $tiles) {
        $g.FillPath((New-Object System.Drawing.SolidBrush (Argb $t.A '#FFFFFF')), (RoundRect $t.X $t.Y $tile $tile $r))
    }

    # Kachel oben rechts: Play-Knopf für Spiele
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Argb 255 '#FFFFFF')), $x2, $x1, $tile, $tile)
    $cx = $x2 + $tile / 2; $cy = $x1 + $tile / 2
    $tri = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($cx - 9), ($cy - 15)),
        (New-Object System.Drawing.PointF ($cx - 9), ($cy + 15)),
        (New-Object System.Drawing.PointF ($cx + 16), $cy))
    $triPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $triPath.AddPolygon($tri)
    $triBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF ($cx - 10), ($cy - 15)), (New-Object System.Drawing.PointF ($cx + 16), ($cy + 15)), (Argb 255 '#6D28D9'), (Argb 255 '#DB2777')
    $pen = New-Object System.Drawing.Pen $triBrush, 6
    $pen.LineJoin = 'Round'
    $g.FillPath($triBrush, $triPath)
    $g.DrawPath($pen, $triPath)
    $g.Dispose()

    $small = New-Object System.Drawing.Bitmap $size, $size
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = 'HighQualityBicubic'
    $g2.PixelOffsetMode = 'HighQuality'
    $g2.DrawImage($big, 0, 0, $size, $size)
    $g2.Dispose(); $big.Dispose()
    return $small
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = Render $s
    if ($PreviewFile) { $bmp.Save($PreviewFile.Replace('.png', "-$s.png")) }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

$out = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($OutFile))
$out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($k = 0; $k -lt $sizes.Count; $k++) {
    $s = $sizes[$k]; $len = $images[$k].Length
    $out.Write([byte]($s % 256)); $out.Write([byte]($s % 256)); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([uint16]1); $out.Write([uint16]32); $out.Write([uint32]$len); $out.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $out.Write($img) }
$out.Close()

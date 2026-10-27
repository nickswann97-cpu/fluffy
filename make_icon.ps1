# Draws the Fluffy icon and writes Fluffy.ico (PNG-compressed entries, 16-256px).
Add-Type -AssemblyName System.Drawing
function Draw([int]$n) {
  $big = New-Object System.Drawing.Bitmap 256, 256
  $g = [System.Drawing.Graphics]::FromImage($big)
  $g.SmoothingMode = 'AntiAlias'
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $r = 56; $d = $r * 2; $x = 8; $y = 8; $w = 240
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $w - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $w - $d, $d, $d, 90, 90); $p.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 176, 128))), $p)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 24
  $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
  $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 60, 108), (New-Object System.Drawing.Point 60, 60), (New-Object System.Drawing.Point 108, 60)))
  $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 148, 60), (New-Object System.Drawing.Point 196, 60), (New-Object System.Drawing.Point 196, 108)))
  $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 196, 148), (New-Object System.Drawing.Point 196, 196), (New-Object System.Drawing.Point 148, 196)))
  $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 108, 196), (New-Object System.Drawing.Point 60, 196), (New-Object System.Drawing.Point 60, 148)))
  $g.FillEllipse([System.Drawing.Brushes]::White, 100, 100, 56, 56)
  $g.Dispose()
  $out = New-Object System.Drawing.Bitmap $n, $n
  $g2 = [System.Drawing.Graphics]::FromImage($out); $g2.InterpolationMode = 'HighQualityBicubic'
  $g2.DrawImage($big, 0, 0, $n, $n); $g2.Dispose(); $big.Dispose()
  $ms = New-Object System.IO.MemoryStream; $out.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $out.Dispose()
  return ,$ms.ToArray()
}
$sizes = 16, 24, 32, 48, 64, 256
$pngs = @(); foreach ($s in $sizes) { $pngs += ,(Draw $s) }
$f = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $f
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write($png) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'Fluffy.ico'), $f.ToArray())

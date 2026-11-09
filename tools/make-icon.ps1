# Generates src/RouteScope.App/Assets/app.ico from the RouteScope logo geometry.
# Run once; the generated icon is committed with the source.
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src\RouteScope.App\Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square with the brand gradient
    $pad = [Math]::Max(1, [int]($size * 0.04))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $radius = [int]($size * 0.26)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 124, 107, 255),
        [System.Drawing.Color]::FromArgb(255, 69, 196, 245),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPath($brush, $path)

    # route curve + endpoints
    $penWidth = [Math]::Max(1.2, $size * 0.075)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $p1 = New-Object System.Drawing.PointF(($size * 0.30), ($size * 0.71))
    $c1 = New-Object System.Drawing.PointF(($size * 0.56), ($size * 0.71))
    $c2 = New-Object System.Drawing.PointF(($size * 0.44), ($size * 0.29))
    $p2 = New-Object System.Drawing.PointF(($size * 0.70), ($size * 0.29))
    $g.DrawBezier($pen, $p1, $c1, $c2, $p2)

    $dotR = [Math]::Max(1.0, $size * 0.052)
    $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillEllipse($dotBrush, ($p1.X - $dotR), ($p1.Y - $dotR), ($dotR * 2), ($dotR * 2))
    $g.FillEllipse($dotBrush, ($p2.X - $dotR), ($p2.Y - $dotR), ($dotR * 2), ($dotR * 2))

    $g.Dispose()
    $pen.Dispose(); $brush.Dispose(); $dotBrush.Dispose(); $path.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-LogoBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $s; Bytes = $ms.ToArray() }
    $bmp.Dispose()
    $ms.Dispose()
}

# Assemble an ICO with PNG-compressed entries (supported since Vista).
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)             # reserved
$bw.Write([UInt16]1)             # type: icon
$bw.Write([UInt16]$pngs.Count)   # image count

$offset = 6 + (16 * $pngs.Count)
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([Byte]$dim)        # width
    $bw.Write([Byte]$dim)        # height
    $bw.Write([Byte]0)           # palette
    $bw.Write([Byte]0)           # reserved
    $bw.Write([UInt16]1)         # planes
    $bw.Write([UInt16]32)        # bpp
    $bw.Write([UInt32]$p.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }

$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets "app.ico"), $out.ToArray())
$bw.Dispose(); $out.Dispose()

# Also keep a large PNG for the readme / store listings.
$big = New-LogoBitmap 512
$big.Save((Join-Path $assets "logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()

Write-Host "Wrote $assets\app.ico ($((Get-Item (Join-Path $assets 'app.ico')).Length) bytes)"

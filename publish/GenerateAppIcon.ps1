Add-Type -AssemblyName System.Drawing

$outDir = "C:\Users\ThanosMilios\projects\vdesk-tracker\VirtualDesktopTracker\Resources"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$icoPath = Join-Path $outDir "app.ico"
$pngPath = Join-Path $outDir "app-256.png"

function New-AppIconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [single]$size

    $bgRect = New-Object System.Drawing.RectangleF(0, 0, $r, $r)
    $bgPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cornerRadius = [single]($size * 0.22)
    $bgPath.AddArc($bgRect.X, $bgRect.Y, $cornerRadius * 2, $cornerRadius * 2, 180, 90)
    $bgPath.AddArc($bgRect.Right - $cornerRadius * 2, $bgRect.Y, $cornerRadius * 2, $cornerRadius * 2, 270, 90)
    $bgPath.AddArc($bgRect.Right - $cornerRadius * 2, $bgRect.Bottom - $cornerRadius * 2, $cornerRadius * 2, $cornerRadius * 2, 0, 90)
    $bgPath.AddArc($bgRect.X, $bgRect.Bottom - $cornerRadius * 2, $cornerRadius * 2, $cornerRadius * 2, 90, 90)
    $bgPath.CloseFigure()

    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($r, $r)),
        [System.Drawing.Color]::FromArgb(255, 70, 130, 230),
        [System.Drawing.Color]::FromArgb(255, 130, 80, 220))
    $g.FillPath($bgBrush, $bgPath)
    $bgBrush.Dispose()

    $g.SetClip($bgPath)
    $hlRect = New-Object System.Drawing.RectangleF(0, 0, $r, [single]($r * 0.5))
    $hlBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF(0, [single]($r * 0.5))),
        [System.Drawing.Color]::FromArgb(120, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillRectangle($hlBrush, $hlRect)
    $hlBrush.Dispose()
    $g.ResetClip()

    $padding = [single]($size * 0.18)
    $faceSize = $r - 2 * $padding
    $faceRect = New-Object System.Drawing.RectangleF($padding, $padding, $faceSize, $faceSize)

    $faceBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.FillEllipse($faceBrush, $faceRect)
    $faceBrush.Dispose()

    $ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 70, 130, 230), [Math]::Max([single]1, $r * 0.018))
    $g.DrawEllipse($ringPen, $faceRect)
    $ringPen.Dispose()

    $centerX = $faceRect.X + $faceRect.Width / 2
    $centerY = $faceRect.Y + $faceRect.Height / 2
    $radius = $faceRect.Width / 2

    $tickPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 70, 130, 230), [Math]::Max([single]1, $r * 0.025))
    foreach ($i in 0..11) {
        $angle = $i * 30 * [Math]::PI / 180
        $innerR = $radius * 0.84
        $outerR = $radius * 0.95
        $x1 = $centerX + [single]([Math]::Cos($angle - [Math]::PI / 2) * $innerR)
        $y1 = $centerY + [single]([Math]::Sin($angle - [Math]::PI / 2) * $innerR)
        $x2 = $centerX + [single]([Math]::Cos($angle - [Math]::PI / 2) * $outerR)
        $y2 = $centerY + [single]([Math]::Sin($angle - [Math]::PI / 2) * $outerR)
        $g.DrawLine($tickPen, $x1, $y1, $x2, $y2)
    }
    $tickPen.Dispose()

    $hourPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 50, 60, 90), [Math]::Max([single]1.5, $r * 0.045))
    $hourPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hourPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hourLen = $radius * 0.5
    $hourAngle = (10 * 60 + 10) * 0.5 * [Math]::PI / 180
    $hx = $centerX + [single]([Math]::Sin($hourAngle) * $hourLen)
    $hy = $centerY - [single]([Math]::Cos($hourAngle) * $hourLen)
    $g.DrawLine($hourPen, $centerX, $centerY, $hx, $hy)
    $hourPen.Dispose()

    $minPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 70, 130, 230), [Math]::Max([single]1, $r * 0.032))
    $minPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $minPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $minLen = $radius * 0.75
    $minAngle = 10 * 6 * [Math]::PI / 180
    $mx = $centerX + [single]([Math]::Sin($minAngle) * $minLen)
    $my = $centerY - [single]([Math]::Cos($minAngle) * $minLen)
    $g.DrawLine($minPen, $centerX, $centerY, $mx, $my)
    $minPen.Dispose()

    $centerDot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 50, 60, 90))
    $dotR = [Math]::Max([single]1.5, $r * 0.05)
    $g.FillEllipse($centerDot, $centerX - $dotR / 2, $centerY - $dotR / 2, $dotR, $dotR)
    $centerDot.Dispose()

    $g.Dispose()
    return $bmp
}

function New-IcoFromBitmaps([System.Drawing.Bitmap[]]$bitmaps) {
    $stream = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($stream)

    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$bitmaps.Length)

    $pngBytes = @()
    foreach ($bmp in $bitmaps) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBytes += , $ms.ToArray()
        $ms.Dispose()
    }

    $offset = 6 + 16 * $bitmaps.Length
    for ($i = 0; $i -lt $bitmaps.Length; $i++) {
        $bmp = $bitmaps[$i]
        $w = if ($bmp.Width -ge 256) { 0 } else { [byte]$bmp.Width }
        $h = if ($bmp.Height -ge 256) { 0 } else { [byte]$bmp.Height }
        $bw.Write([byte]$w)
        $bw.Write([byte]$h)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$pngBytes[$i].Length)
        $bw.Write([uint32]$offset)
        $offset += $pngBytes[$i].Length
    }
    foreach ($bytes in $pngBytes) {
        $bw.Write($bytes)
    }
    $bw.Flush()
    return $stream.ToArray()
}

$master = New-AppIconBitmap -size 256
$master.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$bitmaps = @()
foreach ($s in $sizes) {
    $b = New-AppIconBitmap -size $s
    $bitmaps += $b
}

$icoBytes = New-IcoFromBitmaps -bitmaps $bitmaps
[System.IO.File]::WriteAllBytes($icoPath, $icoBytes)

foreach ($b in $bitmaps) { $b.Dispose() }
$master.Dispose()

Write-Host "Generated: $icoPath"
Get-Item $icoPath | Select-Object Name, Length, LastWriteTime
Get-Item $pngPath | Select-Object Name, Length, LastWriteTime

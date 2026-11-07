Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'src\metGameHDR\assets\icon.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::Transparent)

$r = 56
$d = $r * 2
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $d, $d, 180, 90)
$path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
$path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
$path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
$path.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 16, 32, 80))), $path)

$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = 'Center'
$fmt.LineAlignment = 'Center'
$small = New-Object System.Drawing.Font 'Segoe UI', 54, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('met', $small, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 18, $size, 70), $fmt)
$font = New-Object System.Drawing.Font 'Segoe UI', 84, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('HDR', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 76, $size, 100), $fmt)

$bar = New-Object System.Drawing.Rectangle 48, 182, 160, 14
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $bar, ([System.Drawing.Color]::Red), ([System.Drawing.Color]::Blue), 0.0
$blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
$blend.Colors = @([System.Drawing.Color]::Red, [System.Drawing.Color]::Lime, [System.Drawing.Color]::Blue)
$blend.Positions = @(0.0, 0.5, 1.0)
$grad.InterpolationColors = $blend
$g.FillRectangle($grad, $bar)
$g.Dispose()

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$bmp.Dispose()

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)
$w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0)
$w.Write([uint16]1); $w.Write([uint16]32)
$w.Write([uint32]$png.Length); $w.Write([uint32]22)
$w.Write($png)
$w.Dispose()

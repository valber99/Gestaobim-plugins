param(
    [string]$Source = (Join-Path $PSScriptRoot '..\lib\gestaobim.png'),
    [string]$Output = (Join-Path $PSScriptRoot '..\lib\eyedropper.cur')
)
Add-Type -AssemblyName System.Drawing

$sizes = @(32, 48, 64)
$work = 1024
$src = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))

$tipX = 0; $tipY = 0; $best = -1000000
for ($y = 0; $y -lt $src.Height; $y++) {
    for ($x = 0; $x -lt $src.Width; $x++) {
        if ($src.GetPixel($x, $y).A -gt 128 -and ($y - $x) -gt $best) {
            $best = $y - $x; $tipX = $x; $tipY = $y
        }
    }
}

function New-Silhouette($bitmap, [System.Drawing.Color]$color) {
    $result = New-Object System.Drawing.Bitmap $bitmap.Width, $bitmap.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    for ($y = 0; $y -lt $bitmap.Height; $y++) {
        for ($x = 0; $x -lt $bitmap.Width; $x++) {
            $a = $bitmap.GetPixel($x, $y).A
            if ($a -gt 0) { $result.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $color)) }
        }
    }
    return $result
}

$white = New-Silhouette $src ([System.Drawing.Color]::White)
$dark = New-Silhouette $src ([System.Drawing.Color]::FromArgb(14, 42, 48))

function Render-Size([int]$size) {
    $pad = [math]::Round($size * 0.09)
    $inner = $size - 2 * $pad
    $scale = ($work / $size)
    $unit = $inner * $scale / $src.Width
    $offset = $pad * $scale
    $canvas = New-Object System.Drawing.Bitmap $work, $work, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $w = $src.Width * $unit; $h = $src.Height * $unit
    $ring = {
        param($image, [double]$radius, [float]$opacity)
        $attr = New-Object System.Drawing.Imaging.ImageAttributes
        $matrix = New-Object System.Drawing.Imaging.ColorMatrix
        $matrix.Matrix33 = $opacity
        $attr.SetColorMatrix($matrix)
        for ($i = 0; $i -lt 16; $i++) {
            $angle = 2 * [math]::PI * $i / 16
            $dx = $radius * [math]::Cos($angle); $dy = $radius * [math]::Sin($angle)
            $rect = New-Object System.Drawing.Rectangle ([int]($offset + $dx)), ([int]($offset + $dy)), ([int]$w), ([int]$h)
            $g.DrawImage($image, $rect, 0, 0, $image.Width, $image.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
        }
    }
    $px = $scale
    & $ring $dark ($px * 1.9) 0.22
    & $ring $dark ($px * 1.5) 0.35
    & $ring $white ($px * 0.95) 1.0
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle ([int]$offset), ([int]$offset), ([int]$w), ([int]$h)))
    $g.Dispose()
    $final = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $fg = [System.Drawing.Graphics]::FromImage($final)
    $fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $fg.DrawImage($canvas, 0, 0, $size, $size)
    $fg.Dispose(); $canvas.Dispose()
    $hotX = [int][math]::Round($pad + ($tipX + 0.5) * $unit / $scale)
    $hotY = [int][math]::Round($pad + ($tipY + 0.5) * $unit / $scale)
    $hotX = [math]::Max(0, [math]::Min($size - 1, $hotX))
    $hotY = [math]::Max(0, [math]::Min($size - 1, $hotY))
    $stream = New-Object System.IO.MemoryStream
    $final.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $final.Dispose()
    return @{ Size = $size; HotX = $hotX; HotY = $hotY; Data = $stream.ToArray() }
}

$images = $sizes | ForEach-Object { Render-Size $_ }
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]2); $bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $bw.Write([byte]$image.Size); $bw.Write([byte]$image.Size); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]$image.HotX); $bw.Write([uint16]$image.HotY)
    $bw.Write([uint32]$image.Data.Length); $bw.Write([uint32]$offset)
    $offset += $image.Data.Length
}
foreach ($image in $images) { $bw.Write($image.Data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Output), $out.ToArray())
$src.Dispose()
foreach ($image in $images) { 'size {0} hotspot {1},{2}' -f $image.Size, $image.HotX, $image.HotY }

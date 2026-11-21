param(
    [string]$Panel = (Join-Path $PSScriptRoot '..\GestaoBIM.tab\Materiais.panel')
)
Add-Type -AssemblyName System.Drawing

$size = 64
$themes = @{
    'icon.png'      = @{ Main = '#155E63'; Soft = '#5FB3AE'; Pale = '#CFE6E3' }
    'icon.dark.png' = @{ Main = '#7FD3CC'; Soft = '#4FA39D'; Pale = '#2B5558' }
}

function Brush([string]$hex) { New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex)) }
function Pen([string]$hex, [float]$width) {
    $p = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml($hex)), $width
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $p.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $p
}
function RoundRect($g, $brush, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90); $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
}

# Organizar: a list whose rows are being tidied, with a check mark.
function Draw-Organize($g, $c) {
    foreach ($i in 0..2) {
        $y = 10 + $i * 16
        $g.FillEllipse((Brush $c.Soft), 8, $y + 2, 8, 8)
        RoundRect $g (Brush $c.Main) 22 ($y + 2) (34 - $i * 6) 8 4
    }
    $g.DrawLines((Pen $c.Main 5), @(
        (New-Object System.Drawing.PointF 36, 52), (New-Object System.Drawing.PointF 43, 59), (New-Object System.Drawing.PointF 56, 43)))
}

# Aplicar em tipo: wall layers, the middle one being swapped.
function Draw-Layers($g, $c) {
    RoundRect $g (Brush $c.Soft) 6 8 12 48 3
    RoundRect $g (Brush $c.Pale) 22 8 10 48 3
    RoundRect $g (Brush $c.Main) 34 8 12 48 3
    RoundRect $g (Brush $c.Soft) 48 8 10 48 3
    $g.DrawLines((Pen '#FFFFFF' 4), @(
        (New-Object System.Drawing.PointF 28, 32), (New-Object System.Drawing.PointF 40, 32)))
    $g.DrawLines((Pen '#FFFFFF' 4), @(
        (New-Object System.Drawing.PointF 36, 26), (New-Object System.Drawing.PointF 41, 32), (New-Object System.Drawing.PointF 36, 38)))
}

# Editor fixo: a window frame with a pin.
function Draw-Pinned($g, $c) {
    RoundRect $g (Brush $c.Pale) 6 12 44 42 5
    RoundRect $g (Brush $c.Main) 6 12 44 10 5
    $g.FillRectangle((Brush $c.Main), 6, 17, 44, 5)
    foreach ($i in 0..2) { RoundRect $g (Brush $c.Soft) 12 (28 + $i * 8) (26 - $i * 6) 4 2 }
    $g.FillEllipse((Brush $c.Main), 40, 6, 18, 18)
    $g.FillEllipse((Brush '#FFFFFF'), 46, 12, 6, 6)
}

$icons = @{
    'Organizar.pushbutton'  = ${function:Draw-Organize}
    'AplicarTipo.pushbutton' = ${function:Draw-Layers}
    'EditorFixo.pushbutton' = ${function:Draw-Pinned}
}
foreach ($folder in $icons.Keys) {
    foreach ($file in $themes.Keys) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        & $icons[$folder] $g $themes[$file]
        $g.Dispose()
        $target = Join-Path (Join-Path $Panel $folder) $file
        $bitmap.Save([System.IO.Path]::GetFullPath($target), [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        'wrote ' + $target
    }
}

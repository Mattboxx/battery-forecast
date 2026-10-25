# Genera l'icona dell'app da windows/assets/icon.xaml (stessa geometria di icon.svg):
#   - windows/assets/app.ico con le dimensioni che usa Windows (16–256 px, PNG dentro l'ICO)
#   - windows/assets/icon-256.png e icon-1024.png per README e sito
#   powershell -NoProfile -STA -File tools/make_icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'windows/assets'
$image = [System.Windows.Markup.XamlReader]::Parse((Get-Content (Join-Path $assets 'icon.xaml') -Raw))

function Render([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawImage($image, (New-Object System.Windows.Rect 0, 0, $size, $size))
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

foreach ($s in 256, 1024) { [System.IO.File]::WriteAllBytes((Join-Path $assets "icon-$s.png"), (Render $s)) }

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { , (Render $s) }
$out = Join-Path $assets 'app.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)     # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {                                      # ICONDIRENTRY
    $s = $sizes[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"$out ($((Get-Item $out).Length) byte, $($sizes.Count) dimensioni) + icon-256.png, icon-1024.png"

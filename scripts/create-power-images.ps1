$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/DailyToolkit.Desktop/Assets'))
# Four-times resolution keeps the same 28 DIP surface sharp at Windows display scales.
$size = 112
$bounds = [Windows.Rect]::new(0,0,28,28)
$power = [Windows.Media.Geometry]::Parse('M10.4 10.15 A5.5 5.5 0 1 0 17.6 10.15 M14 7 V13.8')
$power.Freeze()
$pen = [Windows.Media.Pen]::new([Windows.Media.Brushes]::White,1.05)
$pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
$pen.Freeze()
foreach ($state in @(
    @{ Name='power-on.png'; Color='#268354' },
    @{ Name='power-off.png'; Color='#C4414D' }
)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $draw = $visual.RenderOpen()
    $draw.PushTransform([Windows.Media.ScaleTransform]::new(4,4))
    $brush = [Windows.Media.BrushConverter]::new().ConvertFromString($state.Color)
    $draw.DrawRoundedRectangle($brush,$null,$bounds,10,10)
    $draw.DrawGeometry($null,$pen,$power)
    $draw.Pop(); $draw.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $file = [IO.File]::Create((Join-Path $assetDirectory $state.Name))
    try { $encoder.Save($file) } finally { $file.Dispose() }
}

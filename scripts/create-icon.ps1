$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/DailyToolkit.Desktop/Assets'))
$dPath = 'M38 76 L52 62 H83 C130 62 155 86 155 128 C155 170 130 194 83 194 H38 Z M66 88 V168 H82 C110 168 128 153 128 128 C128 103 110 88 82 88 Z'
$tPath = 'M111 62 H222 V88 H194 V179 L167 194 V88 H111 Z'
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <defs><linearGradient id="jade" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#2D8B68"/><stop offset="1" stop-color="#174A3B"/></linearGradient></defs>
  <rect width="256" height="256" rx="56" fill="url(#jade)"/>
  <path d="$dPath" fill="white" fill-rule="evenodd"/>
  <path d="$tPath" fill="white"/>
</svg>
"@
[IO.File]::WriteAllText((Join-Path $assetDirectory 'dt.svg'), $svg)
$frames = @()
foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
    $green = [Windows.Media.LinearGradientBrush]::new(
        [Windows.Media.ColorConverter]::ConvertFromString('#2D8B68'),
        [Windows.Media.ColorConverter]::ConvertFromString('#174A3B'),45)
    $drawing.DrawRoundedRectangle($green,$null,[Windows.Rect]::new(0,0,256,256),56,56)
    $drawing.DrawGeometry([Windows.Media.Brushes]::White,$null,[Windows.Media.Geometry]::Parse('F0 ' + $dPath))
    $drawing.DrawGeometry([Windows.Media.Brushes]::White,$null,[Windows.Media.Geometry]::Parse($tPath))
    $drawing.Pop(); $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new(); $encoder.Save($stream)
    $bytes = $stream.ToArray(); $stream.Dispose()
    $frames += @{ Size=$size; Bytes=$bytes }
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetDirectory 'dt.png'),$bytes) }
}
$file = [IO.File]::Create((Join-Path $assetDirectory 'dt.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16*$frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output 'DT SVG, PNG and nine-size ICO created.'

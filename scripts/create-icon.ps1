$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/DailyToolkit.Desktop/Assets'))
$dPath = 'M42 72 Q42 64 50 64 H88 C133 64 159 87 159 128 C159 169 133 192 88 192 H50 Q42 192 42 184 Z M69 90 V166 H88 C116 166 132 153 132 128 C132 103 116 90 88 90 Z'
$tPath = 'M88 64 H214 Q220 64 220 70 V84 Q220 90 214 90 H196 V184 Q196 192 188 192 H178 Q170 192 170 184 V90 H88 Z'
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <defs><linearGradient id="jade" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#2E8268"/><stop offset="1" stop-color="#245C4C"/></linearGradient></defs>
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
        [Windows.Media.ColorConverter]::ConvertFromString('#2E8268'),
        [Windows.Media.ColorConverter]::ConvertFromString('#245C4C'),45)
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

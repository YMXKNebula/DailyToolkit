$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/DailyToolkit.Desktop/Assets'))
$svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><rect width="256" height="256" rx="52" fill="#267A5D"/><path d="M42 62H77C119 62 137 84 137 128S119 194 77 194H42ZM67 87V169H78C101 169 111 157 111 128S101 87 78 87Z" fill="white" fill-rule="evenodd"/><path d="M139 62H222V87H193V194H168V87H139Z" fill="white"/></svg>'
[IO.File]::WriteAllText((Join-Path $assetDirectory 'dt.svg'), $svg)
$frames = @()
foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
    $green = [Windows.Media.BrushConverter]::new().ConvertFromString('#267A5D')
    $drawing.DrawRoundedRectangle($green,$null,[Windows.Rect]::new(0,0,256,256),52,52)
    $drawing.DrawGeometry([Windows.Media.Brushes]::White,$null,[Windows.Media.Geometry]::Parse('F0 M42,62 L77,62 C119,62 137,84 137,128 C137,172 119,194 77,194 L42,194 Z M67,87 L67,169 L78,169 C101,169 111,157 111,128 C111,99 101,87 78,87 Z'))
    $drawing.DrawGeometry([Windows.Media.Brushes]::White,$null,[Windows.Media.Geometry]::Parse('M139,62 L222,62 L222,87 L193,87 L193,194 L168,194 L168,87 L139,87 Z'))
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

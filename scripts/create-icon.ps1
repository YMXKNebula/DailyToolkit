$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/DailyToolkit.Desktop/Assets'))
$dPath = 'M42 72 Q42 64 50 64 H88 C133 64 159 87 159 128 C159 169 133 192 88 192 H50 Q42 192 42 184 Z M69 90 V166 H88 C116 166 132 153 132 128 C132 103 116 90 88 90 Z'
$tPath = 'M88 64 H214 Q220 64 220 70 V84 Q220 90 214 90 H196 V184 Q196 192 188 192 H178 Q170 192 170 184 V90 H88 Z'
$spectrum = @(
    '#E44459', '#D88437', '#B7A12F', '#2FAE64',
    '#29A5BC', '#3F68DA', '#8B50D1', '#CB48A0'
)
$colors = @($spectrum | ForEach-Object { [Windows.Media.ColorConverter]::ConvertFromString($_) })
$centerColor = [Windows.Media.ColorConverter]::ConvertFromString('#3F4875')
$sectorCount = 256
$radius = 190.0
$radialSteps = 24
$sectors = @()
$gradientDefinitions = [Text.StringBuilder]::new()
$backgroundPaths = [Text.StringBuilder]::new()
function Format-Coordinate([double]$value) {
    $value.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
}
function Get-SpiralPoint([double]$angle, [double]$distance) {
    $rotation = 2.65 * [Math]::Pow($distance / $radius, 1.15)
    (Format-Coordinate (128 + $distance * [Math]::Cos($angle + $rotation))) + ' ' +
        (Format-Coordinate (128 + $distance * [Math]::Sin($angle + $rotation)))
}
for ($index = 0; $index -lt $sectorCount; $index++) {
    $colorPosition = $index * $colors.Count / $sectorCount
    $first = [int][Math]::Floor($colorPosition)
    $weight = $colorPosition - $first
    $next = ($first + 1) % $colors.Count
    $color = [Windows.Media.Color]::FromRgb(
        [byte][Math]::Round($colors[$first].R * (1 - $weight) + $colors[$next].R * $weight),
        [byte][Math]::Round($colors[$first].G * (1 - $weight) + $colors[$next].G * $weight),
        [byte][Math]::Round($colors[$first].B * (1 - $weight) + $colors[$next].B * $weight)
    )
    $colorHex = '#{0:X2}{1:X2}{2:X2}' -f $color.R,$color.G,$color.B
    $start = -[Math]::PI / 2 + 2 * [Math]::PI * $index / $sectorCount
    # Overlapping neighboring sectors keeps the rendered gradient free of seams.
    $end = $start + 2 * [Math]::PI / $sectorCount + 0.04
    $path = [Text.StringBuilder]::new('M128 128')
    for ($step = 1; $step -le $radialSteps; $step++) {
        [void]$path.Append(' L' + (Get-SpiralPoint $start ($radius * $step / $radialSteps)))
    }
    [void]$path.Append(' L' + (Get-SpiralPoint $end $radius))
    for ($step = $radialSteps - 1; $step -ge 1; $step--) {
        [void]$path.Append(' L' + (Get-SpiralPoint $end ($radius * $step / $radialSteps)))
    }
    [void]$path.Append(' Z')
    $geometry = [Windows.Media.Geometry]::Parse($path.ToString())
    $geometry.Freeze()
    $brush = [Windows.Media.RadialGradientBrush]::new()
    $brush.MappingMode = [Windows.Media.BrushMappingMode]::Absolute
    $brush.Center = $brush.GradientOrigin = [Windows.Point]::new(128,128)
    $brush.RadiusX = $brush.RadiusY = $radius
    $brush.ColorInterpolationMode = [Windows.Media.ColorInterpolationMode]::SRgbLinearInterpolation
    $brush.GradientStops.Add([Windows.Media.GradientStop]::new($centerColor,0))
    $brush.GradientStops.Add([Windows.Media.GradientStop]::new($color,0.85))
    $brush.Freeze()
    $sectors += @{ Geometry=$geometry; Brush=$brush }
    [void]$gradientDefinitions.AppendLine('    <radialGradient id="s' + $index + '" gradientUnits="userSpaceOnUse" cx="128" cy="128" r="190" color-interpolation="sRGB"><stop stop-color="#3F4875"/><stop offset="0.85" stop-color="' + $colorHex + '"/></radialGradient>')
    [void]$backgroundPaths.AppendLine('    <path d="' + $path.ToString() + '" fill="url(#s' + $index + ')"/>')
}
$backgroundClip = [Windows.Media.RectangleGeometry]::new([Windows.Rect]::new(0,0,256,256),56,56)
$backgroundClip.Freeze()
$backgroundBase = [Windows.Media.SolidColorBrush]::new($centerColor)
$backgroundBase.Freeze()
$dGeometry = [Windows.Media.Geometry]::Parse('F0 ' + $dPath)
$tGeometry = [Windows.Media.Geometry]::Parse($tPath)
foreach ($geometry in @($dGeometry,$tGeometry)) { $geometry.Freeze() }
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <defs>
    <clipPath id="tile"><rect width="256" height="256" rx="56"/></clipPath>
$gradientDefinitions  </defs>
  <g clip-path="url(#tile)">
    <rect width="256" height="256" fill="#3F4875"/>
$backgroundPaths  </g>
  <path d="$dPath" fill="white" fill-rule="evenodd"/>
  <path d="$tPath" fill="white"/>
</svg>
"@
[IO.File]::WriteAllText((Join-Path $assetDirectory 'dt.svg'), $svg.Replace("`r`n","`n"))
$frames = @()
foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
    $drawing.PushClip($backgroundClip)
    $drawing.DrawRectangle($backgroundBase,$null,[Windows.Rect]::new(0,0,256,256))
    foreach ($sector in $sectors) { $drawing.DrawGeometry($sector.Brush,$null,$sector.Geometry) }
    $drawing.Pop()
    $drawing.DrawGeometry([Windows.Media.Brushes]::White,$null,$dGeometry)
    $drawing.DrawGeometry([Windows.Media.Brushes]::White,$null,$tGeometry)
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

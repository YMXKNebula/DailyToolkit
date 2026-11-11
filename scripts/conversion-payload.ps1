# Dot-source from publish/package/test scripts. No downloads or system changes.
function Get-ConversionPayloadFiles([string]$Directory) {
    $result = [Collections.Generic.List[string]]::new()
    foreach ($engine in @('ffmpeg','libreoffice')) {
        $prefix = "tools/$engine/"
        $base = Join-Path $Directory $prefix
        $manifest = Join-Path $base 'bundle.json'
        if (-not (Test-Path -LiteralPath $manifest)) { throw "Missing prepared $engine inventory." }
        $spec = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($spec.schema -ne 1 -or $spec.component -ne $engine -or $spec.files.Count -lt 3) { throw "Invalid $engine inventory." }
        foreach ($entry in $spec.files) {
            if ($entry.path -notmatch '^[A-Za-z0-9_\-/ .+]+$' -or $entry.path.StartsWith('/') -or
                @($entry.path.Split('/') | Where-Object { $_ -in @('','.', '..') }).Count -or
                $entry.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid component inventory entry.' }
            $file = Join-Path $base $entry.path
            $item = Get-Item -LiteralPath $file -Force
            if ($item.PSIsContainer -or $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
                $item.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() -ne $entry.sha256) {
                throw "Component inventory mismatch: $($entry.path)"
            }
            $result.Add(($prefix + $entry.path).Replace('/', '\'))
        }
        $result.Add(($prefix + 'bundle.json').Replace('/', '\'))
    }
    if (@($result | Sort-Object -Unique).Count -ne $result.Count) { throw 'Duplicate component inventory paths.' }
    $result.ToArray()
}

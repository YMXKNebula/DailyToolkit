# Dot-source from publish/package/test scripts. No downloads or system changes.
function Get-ApplicationPayloadFiles([string]$Directory) {
    @('DailyToolkit.exe','LICENSE-DailyToolkit.txt','README.txt','THIRD_PARTY_NOTICES.txt',
        'microsoft.netcore.app.runtime-LICENSE.TXT','microsoft.netcore.app.runtime-THIRD-PARTY-NOTICES.TXT',
        'microsoft.windowsdesktop.app.runtime-LICENSE','licenses\Windows-SDK-License.rtf',
        'tools\7zip\7za.exe','tools\7zip\License.txt','tools\7zip\Readme.txt','tools\7zip\SOURCE.txt',
        'tools\7zip\COPYING.txt','tools\7zip\7z2604-src.7z')
    Get-ConversionPayloadFiles $Directory
}

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

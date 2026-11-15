# Downloads the pinned libmpv build (see tools/manifest.json "libmpv"), verifies it and
# extracts libmpv-2.dll to src/Tune.App/native/. The DLL (~120 MB) is not committed to git.
# Needs 7-Zip (C:\Program Files\7-Zip\7z.exe).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$m = (Get-Content (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json).libmpv
$target = Join-Path $root 'src\Tune.App\native\libmpv-2.dll'
if ((Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash -eq $m.dllSha256)) { "libmpv already present"; exit 0 }

$tmp = Join-Path $env:TEMP 'tune-libmpv.7z'
"Downloading $($m.file)"
Invoke-WebRequest -UseBasicParsing -Uri $m.url -OutFile $tmp -UserAgent 'Wget'
$hash = (Get-FileHash $tmp -Algorithm SHA256).Hash
if ($hash -ne $m.archiveSha256) { Remove-Item $tmp; throw "libmpv archive hash mismatch: $hash" }

New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
& 'C:\Program Files\7-Zip\7z.exe' e -y "-o$(Split-Path $target)" $tmp libmpv-2.dll | Out-Null
Remove-Item $tmp
if ((Get-FileHash $target -Algorithm SHA256).Hash -ne $m.dllSha256) { throw 'libmpv-2.dll hash mismatch' }
"libmpv ready: $target"

# Builds the shareable Windows release: one self-contained Tune.exe (no .NET install needed; libmpv packed inside).
# Output: dist\Tune.exe  (first run downloads yt-dlp + deno into %LOCALAPPDATA%\Tune\tools, like a normal install)
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'fetch-libmpv.ps1')

$tmp = Join-Path $env:TEMP 'tune-release'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
& 'C:\Program Files\dotnet\dotnet.exe' publish (Join-Path $root 'src\Tune.App\Tune.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $tmp
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

New-Item -ItemType Directory -Force $Out | Out-Null
Copy-Item (Join-Path $tmp 'Tune.exe') (Join-Path $Out 'Tune.exe') -Force
$size = [math]::Round((Get-Item (Join-Path $Out 'Tune.exe')).Length / 1MB, 1)
"Release: $(Join-Path $Out 'Tune.exe') ($size MB)"

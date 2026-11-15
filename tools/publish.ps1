# Builds Tune (Release, framework-dependent, ReadyToRun) into D:\Apps\Tune and adds a Start-menu shortcut.
# Requires the .NET 10 Desktop Runtime (installed with the SDK) and src\Tune.App\native\libmpv-2.dll (tools\fetch-libmpv.ps1).
param([string]$Out = 'D:\Apps\Tune')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'fetch-libmpv.ps1')

if (Get-Process Tune -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Out*" }) {
    throw "Tune is running from $Out - quit it from the tray first."
}

& 'C:\Program Files\dotnet\dotnet.exe' publish (Join-Path $root 'src\Tune.App\Tune.App.csproj') `
    -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $Out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# Windows caches shortcut icons by path: give every new logo its own file name so pinned/Start icons refresh
$ico = Join-Path $root 'src\Tune.App\Assets\Tune.ico'
$hash = (Get-FileHash $ico -Algorithm SHA256).Hash.Substring(0, 8)
$icoOut = Join-Path $Out "Tune-$hash.ico"
Get-ChildItem $Out -Filter 'Tune-*.ico' | Where-Object { $_.FullName -ne $icoOut } | Remove-Item -Force
Copy-Item $ico $icoOut -Force

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Tune.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = Join-Path $Out 'Tune.exe'
$s.WorkingDirectory = $Out
$s.Description = 'Tune - ad-free YouTube Music player'
$s.IconLocation = "$icoOut,0"
$s.Save()

# the taskbar pin (if Tune is pinned) keeps its own copy of the shortcut
$pinned = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Tune.lnk'
if (Test-Path $pinned) {
    $p = $shell.CreateShortcut($pinned)
    if ($p.TargetPath -like "$Out*") { $p.IconLocation = "$icoOut,0"; $p.Save() }
}
& ie4uinit.exe -show
"Published to $Out; Start-menu shortcut: $lnk"

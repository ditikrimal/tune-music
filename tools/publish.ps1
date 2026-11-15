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

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Tune.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = Join-Path $Out 'Tune.exe'
$s.WorkingDirectory = $Out
$s.Description = 'Tune - ad-free YouTube Music player'
$s.Save()
"Published to $Out; Start-menu shortcut: $lnk"

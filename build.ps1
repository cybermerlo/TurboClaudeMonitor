# Compiles TurboClaudeMonitor.exe with the C# compiler that ships with Windows (.NET Framework 4.8): nothing to install.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin = Join-Path $root 'bin'
$exe = Join-Path $bin 'TurboClaudeMonitor.exe'
$ico = Join-Path $root 'assets\app.ico'
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

if (Get-Process TurboClaudeMonitor -ErrorAction SilentlyContinue) {
    throw "TurboClaudeMonitor is running: quit it (tray menu > Esci) and build again."
}
New-Item -ItemType Directory -Force $bin | Out-Null

function Invoke-Csc([bool]$withIcon) {
    $cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/warn:4', '/codepage:65001',
        "/out:$exe", "/win32manifest:$(Join-Path $root 'src\app.manifest')",
        '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
        '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll')
    if ($withIcon) { $cscArgs += "/win32icon:$ico" }
    & $csc @cscArgs @sources
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

# The app draws its own .ico: first build without it, export it, then rebuild with it embedded.
if (-not (Test-Path $ico)) {
    Invoke-Csc $false
    Start-Process -FilePath $exe -ArgumentList @('--export-ico', "`"$ico`"") -Wait
}
Invoke-Csc $true
Write-Host "Built $exe"

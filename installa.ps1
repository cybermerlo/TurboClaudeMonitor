# Sets up TurboClaudeMonitor end to end: builds it if needed, installs the Claude Code CLI if missing,
# signs in (the browser opens and you complete the login there), checks the usage read, starts the monitor.
# Safe to run again: every step already done is skipped.
#   -Relogin      sign in again even if a login is already there
#   -NoAutostart  do not offer to start the monitor with Windows
param([switch]$Relogin, [switch]$NoAutostart)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'bin\TurboClaudeMonitor.exe'
$localBin = Join-Path $env:USERPROFILE '.local\bin'
$configDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
$credPath = Join-Path $configDir '.credentials.json'

function Step([int]$n, [string]$text) { Write-Host ''; Write-Host "[$n/5] $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "  OK  $text" -ForegroundColor Green }
function Info([string]$text) { Write-Host "      $text" }
function Warn([string]$text) { Write-Host "  !!  $text" -ForegroundColor Yellow }
function Ask([string]$question) {
    $answer = Read-Host "  ?   $question [S/n]"
    return ($answer -eq '' -or $answer -match '^[sSyY]')
}

# The plan of the claude.ai login in Claude Code's file, or $null when there is none. Never prints the token.
function Get-LoginPlan {
    if (-not (Test-Path $credPath)) { return $null }
    try { $json = Get-Content $credPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
    $oauth = $json.claudeAiOauth
    if (-not $oauth -or -not $oauth.accessToken) { return $null }
    if ($oauth.subscriptionType) { return [string]$oauth.subscriptionType }
    return 'claude.ai'
}

function Find-Claude {
    $candidate = Join-Path $localBin 'claude.exe'
    if (Test-Path $candidate) { return $candidate }
    $cmd = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    return $null
}

# Win11 lists every tray icon under NotifyIconSettings once shown; IsPromoted=1 keeps it out of the ^ overflow.
function Show-TrayIcon {
    $base = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path $base)) { return $false }
    for ($i = 0; $i -lt 20; $i++) {
        $found = $false
        foreach ($key in Get-ChildItem $base -ErrorAction SilentlyContinue) {
            $path = (Get-ItemProperty $key.PSPath -Name ExecutablePath -ErrorAction SilentlyContinue).ExecutablePath
            if ($path -and $path -like '*\TurboClaudeMonitor.exe') {
                Set-ItemProperty $key.PSPath -Name IsPromoted -Value 1 -Type DWord
                $found = $true
            }
        }
        if ($found) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Write-Host 'TurboClaudeMonitor: installazione' -ForegroundColor White

# When launched from inside the Claude desktop app, these variables point Claude Code at the app's own session.
# They are dropped for this script only, so the login below is a normal one.
if ($env:CLAUDECODE -or $env:CLAUDE_CODE_ENTRYPOINT) {
    Get-ChildItem Env: |
        Where-Object { $_.Name -match '^(CLAUDECODE|CLAUDE_CODE_.*|CLAUDE_AGENT_SDK_.*|ANTHROPIC_BASE_URL|USE_LOCAL_OAUTH|USE_STAGING_OAUTH)$' -and $_.Name -ne 'CLAUDE_CODE_GIT_BASH_PATH' } |
        ForEach-Object { Remove-Item "Env:$($_.Name)" }
}

# ---- 1. The monitor itself ----
Step 1 'Programma del monitor'
$src = Join-Path $root 'src'
$needBuild = -not (Test-Path $exe)
if (-not $needBuild -and (Test-Path $src)) {
    $exeTime = (Get-Item $exe).LastWriteTime
    $needBuild = [bool](Get-ChildItem $src -File | Where-Object { $_.LastWriteTime -gt $exeTime })
}
if ($needBuild) {
    if (-not (Test-Path $src)) { throw "Non trovo ne' $exe ne' i sorgenti in $src." }
    $running = Get-Process TurboClaudeMonitor -ErrorAction SilentlyContinue
    if ($running) {
        Info 'Chiudo il monitor in esecuzione per aggiornarlo.'
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
    Info 'Compilo...'
    & (Join-Path $root 'build.ps1')
}
Ok "Pronto: $exe"

# ---- 2. Claude Code from the terminal ----
Step 2 'Claude Code da terminale'
if (-not (Get-Command git -ErrorAction SilentlyContinue) -and -not (Test-Path "$env:ProgramFiles\Git\bin\bash.exe")) {
    Warn 'Claude Code su Windows si appoggia a Git for Windows, che non trovo.'
    Info 'Se il login non parte, installalo da https://git-scm.com/download/win e rilancia questo script.'
}
$claude = Find-Claude
if ($claude) {
    Ok "Gia' installato: $claude"
}
else {
    Info "Non e' installato. Uso l'installer ufficiale di Anthropic (https://claude.ai/install.ps1)."
    Info "Non tocca l'app desktop di Claude: installa un programma separato in $localBin."
    if (-not (Ask 'Procedo con il download e l''installazione?')) {
        Warn 'Installazione annullata. Senza Claude Code da terminale il monitor non ha un login da leggere.'
        exit 1
    }
    # In a child process, so nothing the installer does can end this script.
    & powershell -NoProfile -ExecutionPolicy Bypass -Command 'irm https://claude.ai/install.ps1 | iex'
    $claude = Find-Claude
    if (-not $claude) { throw "L'installazione non ha prodotto claude.exe in $localBin. Controlla i messaggi qui sopra." }
    Ok "Installato: $claude"
}
if ($claude -like "$localBin\*") {
    if (($env:Path -split ';') -notcontains $localBin) { $env:Path = "$env:Path;$localBin" }
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ((("$userPath") -split ';') -notcontains $localBin) {
        [Environment]::SetEnvironmentVariable('Path', ("$userPath".TrimEnd(';') + ";$localBin").TrimStart(';'), 'User')
        Ok "Aggiunto $localBin al PATH: il comando claude funziona anche nei terminali che aprirai."
    }
}

# ---- 3. Login ----
Step 3 'Login con il tuo account Claude'
$plan = Get-LoginPlan
if ($plan -and -not $Relogin) {
    Ok "Login gia' presente (piano: $plan). Per rifarlo: installa.cmd -Relogin"
}
else {
    Info 'Si apre il browser: accedi con il tuo account Claude e autorizza Claude Code.'
    Info 'Poi torna qui: lo script prosegue da solo.'
    & $claude auth login --claudeai
    $plan = Get-LoginPlan
    if (-not $plan) {
        throw "Login non completato: non trovo il login in $credPath. Rilancia lo script per riprovare."
    }
    Ok "Login riuscito (piano: $plan)."
}

# ---- 4. A first read of the limits ----
Step 4 'Verifica della lettura dei limiti'
$checkFile = Join-Path $env:TEMP 'turboclaudemonitor-check.txt'
$check = Start-Process -FilePath $exe -ArgumentList @('--check', "`"$checkFile`"") -Wait -PassThru
$report = if (Test-Path $checkFile) { Get-Content $checkFile -Encoding UTF8 } else { @() }
Remove-Item $checkFile -ErrorAction SilentlyContinue
if ($check.ExitCode -eq 0) {
    Ok 'Limiti letti correttamente:'
    $report | Where-Object { $_ -match '^(five_hour|seven_day|model:)' } | ForEach-Object { Info $_ }
}
else {
    Warn 'La lettura non e'' riuscita. Il monitor parte comunque e riprova da solo; ecco il dettaglio:'
    $report | ForEach-Object { Info $_ }
}

# ---- 5. Start ----
Step 5 'Avvio del monitor'
if (Get-Process TurboClaudeMonitor -ErrorAction SilentlyContinue) {
    Ok "Gia' in esecuzione: rilegge il login entro un minuto."
}
else {
    Start-Process -FilePath $exe
    Ok 'Avviato.'
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$wanted = '"' + $exe + '"'
$current = (Get-ItemProperty $runKey -Name TurboClaudeMonitor -ErrorAction SilentlyContinue).TurboClaudeMonitor
if ($current -eq $wanted) {
    Ok "Avvio con Windows gia' attivo."
}
elseif (-not $NoAutostart -and (Ask 'Vuoi che il monitor parta da solo all''accensione di Windows?')) {
    Set-ItemProperty $runKey -Name TurboClaudeMonitor -Value $wanted
    Ok 'Partira'' con Windows (si spegne dal menu dell''icona).'
}

if (Show-TrayIcon) {
    Ok "Icona fissata nella barra, fuori dal menu nascosto (^). Se non la vedi subito, comparira' al prossimo accesso."
}
else {
    Warn "Non sono riuscito a fissare l'icona: se e' nel menu nascosto (^), trascinala sulla barra."
}

Write-Host ''
Write-Host 'Fatto. Passa il mouse sulle tre barre per i numeri, clic per i dettagli, clic destro per le opzioni.' -ForegroundColor White

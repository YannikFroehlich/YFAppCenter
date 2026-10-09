<#
.SYNOPSIS
    Baut YF AppCenter, packt es mit Velopack und veröffentlicht es als GitHub-Release.
    Installierte Versionen finden das Release automatisch und aktualisieren sich.
.EXAMPLE
    .\scripts\New-Release.ps1 -Version 0.3.0
.EXAMPLE
    .\scripts\New-Release.ps1 -Version 0.3.0 -NoUpload   # nur lokal packen, nichts veröffentlichen
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [switch]$NoUpload
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repoUrl = 'https://github.com/YannikFroehlich/YFAppCenter'
$publishDir = Join-Path $root "artifacts\publish\$Version"
$outputDir = Join-Path $root 'artifacts\velopack'

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Exe $($Arguments -join ' ') ist fehlgeschlagen (Exitcode $LASTEXITCODE)."
    }
}

if (-not $NoUpload -and (git -C $root status --porcelain)) {
    throw 'Es gibt nicht committete Änderungen. Erst committen, dann veröffentlichen.'
}

Invoke-Checked dotnet @('test', (Join-Path $root 'YFAppCenter.Tests'))
Invoke-Checked dotnet @(
    'publish', (Join-Path $root 'YFAppCenter'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    "-p:Version=$Version", '-o', $publishDir)

$token = $null
if (-not $NoUpload) {
    $token = (gh auth token).Trim()
    # Vorherige Version laden, damit vpk kleine Delta-Updates bauen kann. Beim ersten Release gibt es keine.
    & vpk download github --repoUrl $repoUrl --token $token --outputDir $outputDir
}

# --framework: Fehlt die .NET-Desktop-Laufzeit, installiert das Setup sie mit.
# --shortcuts StartMenuRoot: nur Startmenü, bewusst keine Desktop-Verknüpfung.
Invoke-Checked vpk @(
    'pack',
    '--packId', 'YFAppCenter.App',
    '--packVersion', $Version,
    '--packDir', $publishDir,
    '--mainExe', 'YFAppCenter.exe',
    '--packTitle', 'YF AppCenter',
    '--packAuthors', 'YannikFroehlich',
    '--icon', (Join-Path $root 'YFAppCenter\AppCenter.ico'),
    '--framework', 'net10.0-x64-desktop',
    '--shortcuts', 'StartMenuRoot',
    '--outputDir', $outputDir)

# Eigener Installer (Ordnerauswahl, Verknüpfungen, Deinstallation) statt Velopacks Ein-Klick-Setup.
# Er enthält das Portable-Paket; entpackt ist das eine normale Velopack-Installation mit Updates.
$portable = Join-Path $outputDir 'YFAppCenter.App-win-Portable.zip'
Invoke-Checked dotnet @(
    'build', (Join-Path $root 'YFAppCenter.Setup'),
    '-c', 'Release', "-p:Version=$Version", "-p:AppPackage=$portable")
Copy-Item (Join-Path $root 'YFAppCenter.Setup\bin\Release\net48\YFAppCenter-Setup.exe') (Join-Path $outputDir 'YFAppCenter-Setup.exe') -Force
Remove-Item (Join-Path $outputDir 'YFAppCenter.App-win-Setup.exe') -ErrorAction SilentlyContinue
$assetsFile = Join-Path $outputDir 'assets.win.json'
$assetsJson = [IO.File]::ReadAllText($assetsFile).Replace('"YFAppCenter.App-win-Setup.exe"', '"YFAppCenter-Setup.exe"')
[IO.File]::WriteAllText($assetsFile, $assetsJson, (New-Object Text.UTF8Encoding $false))

if ($NoUpload) {
    Write-Host "Fertig. Setup und Pakete liegen in $outputDir"
    return
}

Invoke-Checked git @('-C', $root, 'push', 'origin', 'HEAD')
Invoke-Checked vpk @(
    'upload', 'github',
    '--repoUrl', $repoUrl,
    '--token', $token,
    '--outputDir', $outputDir,
    '--publish',
    '--releaseName', "YF AppCenter v$Version",
    '--tag', "v$Version",
    '--targetCommitish', (git -C $root rev-parse HEAD).Trim())
Write-Host "Veröffentlicht: $repoUrl/releases/tag/v$Version"

<#
.SYNOPSIS
    Baut YF AppCenter, packt es mit Velopack und veröffentlicht es als GitHub-Release.
    Installierte Versionen finden das Release automatisch und aktualisieren sich.
    Veröffentlicht wird bei jedem Push auf main über GitHub Actions (.github/workflows/release.yml).
.DESCRIPTION
    Ohne -Version nimmt das Skript den neuesten Tag vX.Y.Z und zählt die letzte Stelle hoch. Für einen größeren
    Sprung (etwa 0.5.0) die Version in YFAppCenter\YFAppCenter.csproj eintragen; ist sie höher, wird sie genommen.
.EXAMPLE
    .\scripts\New-Release.ps1 -NoUpload   # lokal Setup und Pakete der nächsten Version bauen, nichts veröffentlichen
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [switch]$NoUpload
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repoUrl = 'https://github.com/YannikFroehlich/YFAppCenter'
$outputDir = Join-Path $root 'artifacts\velopack'

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Exe $($Arguments -join ' ') ist fehlgeschlagen (Exitcode $LASTEXITCODE)."
    }
}

# Veröffentlicht wird nur aus GitHub Actions; ein lokales Release käme zu dem des Pushs noch hinzu.
if (-not $NoUpload -and -not $env:GITHUB_ACTIONS) {
    throw 'Releases entstehen automatisch bei jedem Push auf main. Lokal nur mit -NoUpload bauen.'
}

if (-not $Version) {
    $last = git -C $root tag --list 'v*' |
        Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } |
        ForEach-Object { [version]$_.Substring(1) } |
        Sort-Object -Descending |
        Select-Object -First 1
    $base = [version](Select-Xml -Path (Join-Path $root 'YFAppCenter\YFAppCenter.csproj') -XPath '//Version').Node.InnerText
    $Version = if (-not $last -or $base -gt $last) { $base.ToString(3) } else { '{0}.{1}.{2}' -f $last.Major, $last.Minor, ($last.Build + 1) }
}

Write-Host "Version $Version"
$publishDir = Join-Path $root "artifacts\publish\$Version"

# Spielerkennung aus YFTimeTracker; fehlt nach einem Klon ohne --recursive.
Invoke-Checked git @('-C', $root, 'submodule', 'update', '--init')
Invoke-Checked dotnet @('test', (Join-Path $root 'YFAppCenter.Tests'))
Invoke-Checked dotnet @(
    'publish', (Join-Path $root 'YFAppCenter'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    "-p:Version=$Version", '-o', $publishDir)

$token = $env:GITHUB_TOKEN
if (-not $NoUpload) {
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

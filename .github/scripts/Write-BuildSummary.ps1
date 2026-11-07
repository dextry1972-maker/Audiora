<#
.SYNOPSIS
    Checks the output of build.ps1 and describes it in Markdown.
.DESCRIPTION
    Fails (exit 1) if a file is missing or if the installer does not embed exactly the payload.zip that build.ps1 just
    made (an installer carrying an older app would otherwise go unnoticed). Then writes the versions, sizes and SHA-256
    hashes to the run page ($env:GITHUB_STEP_SUMMARY) or prints them. Run it with Windows PowerShell 5.1: the installer
    targets .NET Framework 4.8, and that runtime is the one that can open it to read the embedded payload.
.EXAMPLE
    powershell -NoProfile -File .github\scripts\Write-BuildSummary.ps1 -Artifacts artifacts
#>
param([string] $Artifacts = 'artifacts')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Artifacts).Path
$setup = Join-Path $root 'SonosFlow-Setup.exe'
$payload = Join-Path $root 'payload.zip'
$appExe = Join-Path $root 'app\SonosFlow.exe'
foreach ($f in $setup, $payload, $appExe) {
    if (-not (Test-Path -LiteralPath $f)) { Write-Error "Missing build output: $f" }
}

function Get-Sha256Hex([IO.Stream] $Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($Stream) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $sha.Dispose() }
}

# The installer embeds the payload as the manifest resource "payload.zip". Loading from bytes keeps the file unlocked.
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($setup))
$embeddedStream = $assembly.GetManifestResourceStream('payload.zip')
if ($null -eq $embeddedStream) { Write-Error 'The installer has no embedded payload.zip.' }
try { $embedded = Get-Sha256Hex $embeddedStream } finally { $embeddedStream.Dispose() }
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
if ($embedded -ne $payloadHash) {
    Write-Error "The installer embeds a different payload ($embedded) than artifacts\payload.zip ($payloadHash)."
}

$setupInfo = Get-Item -LiteralPath $setup
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appExe).ProductVersion
$setupVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($setup).ProductVersion
$appFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'app') -Recurse -File).Count

$lines = @(
    '## Windows installer',
    '',
    '**Built and checked**: the installer embeds exactly the app built in this run.',
    '',
    '| | |',
    '|---|---|',
    "| App version | ``$appVersion`` |",
    "| Installer version | ``$setupVersion`` |",
    ('| Installer size | {0:N1} MB |' -f ($setupInfo.Length / 1MB)),
    "| Installer SHA-256 | ``$setupHash`` |",
    "| Files in the portable app | $appFiles |",
    '',
    'Download them under **Artifacts** at the bottom of this page: **Audiora-Setup-win-x64** (installer) and',
    '**Audiora-portable-win-x64** (open `SonosFlow.exe`). GitHub delivers each artifact as a .zip; they are kept 14 days.',
    'These are unsigned test builds, not a release.'
)
$text = ($lines -join "`n") + "`n"
if ($env:GITHUB_STEP_SUMMARY) { [IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $text, (New-Object Text.UTF8Encoding($false))) }
else { $text }

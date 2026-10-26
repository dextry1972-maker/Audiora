# Builds Sonos Flow and its installer.
#   .\build.ps1            -> artifacts\SonosFlow-Setup.exe
# Requires the .NET 8 SDK. Nothing is installed on the system by this script.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = 'dotnet'
$artifacts = Join-Path $root 'artifacts'
$appOut = Join-Path $artifacts 'app'
$payload = Join-Path $artifacts 'payload.zip'

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $artifacts | Out-Null

Write-Host '1/3 Publicando la app (autocontenida, win-x64)...'
& $dotnet publish (Join-Path $root 'src\SonosFlow.App\SonosFlow.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=false -p:DebugType=none -o $appOut -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación de la app' }

Write-Host '2/3 Empaquetando...'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($appOut, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host '3/3 Compilando el instalador...'
& $dotnet build (Join-Path $root 'src\SonosFlow.Setup\SonosFlow.Setup.csproj') -c Release "-p:PayloadZip=$payload" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del instalador' }
Copy-Item (Join-Path $root 'src\SonosFlow.Setup\bin\Release\net48\SonosFlow-Setup.exe') (Join-Path $artifacts 'SonosFlow-Setup.exe')

$size = [math]::Round((Get-Item (Join-Path $artifacts 'SonosFlow-Setup.exe')).Length / 1MB, 1)
Write-Host "Listo: artifacts\SonosFlow-Setup.exe ($size MB)"

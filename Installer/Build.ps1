param(
    [string] $MakeNsis = 'makensis.exe',
    [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'Release' }

$compiler = (Get-Command $MakeNsis -CommandType Application -ErrorAction Stop).Source
$output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$build = Join-Path $repo 'Installer/obj'
$payload = Join-Path $build 'publish'
$setup = Join-Path $output 'ThumbnailExplorer-Setup-x64.exe'

dotnet publish (Join-Path $repo 'App/ThumbnailExplorer.csproj') -c Release -r win-x64 --self-contained false --artifacts-path (Join-Path $build 'artifacts') -o $payload
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

& $compiler /V3 "/DPayloadDir=$payload" "/DSetupOutput=$setup" (Join-Path $PSScriptRoot 'Setup.nsi')
if ($LASTEXITCODE -ne 0) { throw "NSIS failed ($LASTEXITCODE)." }

Get-Item -LiteralPath $setup

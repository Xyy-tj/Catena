$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$temporarySource = Join-Path $repo 'tests/Catena.Tests/ReadmeExport.cs'
if (Test-Path -LiteralPath $temporarySource) { throw 'ReadmeExport.cs already exists; nothing was overwritten.' }
$previousOutput = $env:CATENA_README_IMAGES
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReadmeExport.cs') -Destination $temporarySource
    $env:CATENA_README_IMAGES = Join-Path $repo 'docs/images'
    $env:DOTNET_CLI_HOME = Join-Path $repo '.tools/cli'
    $env:NUGET_PACKAGES = Join-Path $repo '.tools/packages'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $dotnet = Join-Path $repo '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
    $env:DOTNET_ROOT = Split-Path (Get-Command $dotnet).Source -Parent
    & $dotnet test (Join-Path $repo 'tests/Catena.Tests/Catena.Tests.csproj') -c Release --no-restore -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~ReadmeExport
    if ($LASTEXITCODE -ne 0) { throw 'Screenshot rendering failed.' }
}
finally {
    Remove-Item -LiteralPath $temporarySource -ErrorAction SilentlyContinue
    $env:CATENA_README_IMAGES = $previousOutput
}

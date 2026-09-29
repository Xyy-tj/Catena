param([string]$Version = '0.6.3', [string]$Compiler = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
if (!$Compiler) {
    foreach ($candidate in @((Join-Path $repo '.tools/innosetup/ISCC.exe'), "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe")) {
        if (Test-Path -LiteralPath $candidate) { $Compiler = $candidate; break }
    }
}
if (!$Compiler -or !(Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 6.7+ or pass -Compiler with the full ISCC.exe path.' }
& (Join-Path $PSScriptRoot 'setup-everything.ps1')
& (Join-Path $PSScriptRoot 'dev.ps1') publish
foreach ($required in @('Catena.App.exe','tools/everything/es.exe','tools/everything/Everything-Setup.exe','tools/everything/Everything-License.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $repo "artifacts/win-x64/$required"))) { throw "Package missing $required" }
}
& $Compiler "/DAppVersion=$Version" (Join-Path $repo 'installer/Catena.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
Write-Output (Join-Path $repo "artifacts/installer/Catena-$Version-win-x64-Setup.exe")

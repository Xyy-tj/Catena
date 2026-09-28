$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$downloads = Join-Path $repo '.tools/everything'
$destination = Join-Path $repo 'vendor/everything'
New-Item -ItemType Directory -Force $downloads, $destination | Out-Null
$archive = Join-Path $downloads 'es.zip'
$expected = '5E0C70CBF4F694080C34AA7C6C745E606C16FE76A4B5423B93EBF9DC34274C99'
if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) {
    Invoke-WebRequest 'https://www.voidtools.com/ES-1.1.0.38.x64.zip' -OutFile $archive -MaximumRetryCount 2
}
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) { throw 'ES archive hash mismatch.' }
Expand-Archive $archive -DestinationPath (Join-Path $downloads 'es') -Force
Copy-Item (Join-Path $downloads 'es/es.exe') (Join-Path $destination 'es.exe') -Force
$installer = Join-Path $downloads 'Everything-Setup.exe'
$installerHash = 'C42EFAD041D4C0BB4D4AC97AE7CBE89F153EC1FE078772392E749C7F5D5282D3'
if (!(Test-Path -LiteralPath $installer) -or (Get-FileHash $installer -Algorithm SHA256).Hash -ne $installerHash) {
    Invoke-WebRequest 'https://www.voidtools.com/Everything-1.4.1.1032.x64-Setup.exe' -OutFile $installer -MaximumRetryCount 2
}
if ((Get-FileHash $installer -Algorithm SHA256).Hash -ne $installerHash) { throw 'Everything installer hash mismatch.' }
Copy-Item -LiteralPath $installer -Destination (Join-Path $destination 'Everything-Setup.exe') -Force
Write-Output 'ES and the offline Everything installer are ready for packaging. No runtime service was installed on this computer.'

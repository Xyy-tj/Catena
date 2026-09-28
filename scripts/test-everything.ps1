param(
    [Parameter(Mandatory)][string]$EverythingExecutable,
    [int]$FixtureCount = 10000
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$client = (Resolve-Path -LiteralPath $EverythingExecutable).Path
$version = (Get-Item -LiteralPath $client).VersionInfo.FileVersion
if ($version -notlike '1.5.*' -and $version -notlike '1.4.*') { throw '需要 Everything 1.4 或 1.5 便携版。' }
$es = Join-Path $repo 'vendor/everything/es.exe'
if (!(Test-Path -LiteralPath $es)) { throw '请先运行 scripts/setup-everything.ps1。' }
$instance = 'CatenaTest-' + [Guid]::NewGuid().ToString('N')
$fixtureDirectory = Join-Path $repo ".data/everything fixture/$instance"
[void](New-Item -ItemType Directory -Path $fixtureDirectory -Force)
$list = Join-Path $fixtureDirectory 'fixture.efu'
$config = Join-Path $fixtureDirectory 'Everything.ini'
$escapedList = $list.Replace('\', '\\')
[IO.File]::WriteAllText($config, @"
[Everything]
show_tray_icon=0
run_as_admin=0
auto_include_fixed_volumes=0
auto_include_removable_volumes=0
auto_include_fixed_refs_volumes=0
auto_include_removable_refs_volumes=0
filelists="$escapedList"
index_size=1
index_date_modified=1
index_attributes=1
"@)
$fileTime = [DateTime]::UtcNow.ToFileTimeUtc()
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('Filename,Size,Date Modified,Date Created,Attributes')
$lines.Add(('"{0}",2048,{1},,32' -f (Join-Path $fixtureDirectory 'catena 预算,2026.xlsx'), $fileTime))
$lines.Add(('"{0}",123,{1},,32' -f (Join-Path $fixtureDirectory 'literal [预算] # ; ! (v1) 😀.txt'), $fileTime))
$lines.Add(('"{0}",,,,{1}' -f (Join-Path $fixtureDirectory '测试目录'), 16))
$lines.Add(('"{0}",100,{1},,32' -f (Join-Path $fixtureDirectory 'scope-a/deep/recursive-marker.txt'), $fileTime))
$lines.Add(('"{0}",200,{1},,32' -f (Join-Path $fixtureDirectory 'scope-b/recursive-marker.txt'), $fileTime))
$lines.Add(('"{0}",300,{1},,32' -f (Join-Path $fixtureDirectory 'scope-a-other/recursive-marker.txt'), $fileTime))
for ($i = 0; $i -lt $FixtureCount; $i++) {
    $lines.Add(('"{0}",100,{1},,32' -f (Join-Path $fixtureDirectory "document-$i.txt"), $fileTime))
}
[IO.File]::WriteAllLines($list, $lines, [Text.UTF8Encoding]::new($true))
$names = @('CATENA_EVERYTHING_TEST_INSTANCE','CATENA_EVERYTHING_TEST_ES','CATENA_EVERYTHING_TEST_ROOT','CATENA_TEST_ARTIFACTS','CATENA_EVERYTHING_TEST_COUNT')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$process = $null
try {
    $arguments = @('-instance', $instance, '-config', ('"' + $config + '"'), '-no-db', '-startup')
    if ($version -like '1.5.*') { $arguments += @('-no-auto-index', '-filelists', ('"' + $list + '"')) }
    $process = Start-Process -FilePath $client -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        # 1.4 also materializes parent folders from an EFU list; compare file count only.
        $count = & $es -instance $instance -get-result-count -search 'file:'
        if ($LASTEXITCODE -eq 0 -and $count -eq ($FixtureCount + 5).ToString()) { $ready = $true; break }
    }
    if (!$ready) { throw '隔离 Everything 测试索引未就绪。' }
    $env:CATENA_EVERYTHING_TEST_INSTANCE = $instance
    $env:CATENA_EVERYTHING_TEST_ES = $es
    $env:CATENA_EVERYTHING_TEST_ROOT = $fixtureDirectory
    $env:CATENA_EVERYTHING_TEST_COUNT = ($FixtureCount + 6).ToString()
    $env:CATENA_TEST_ARTIFACTS = Join-Path $repo 'artifacts/screenshots'
    & (Join-Path $PSScriptRoot 'dev.ps1') test
}
finally {
    if ($null -ne $process -and !$process.HasExited) {
        & $es -instance $instance -exit | Out-Null
        if (!$process.WaitForExit(5000)) { $process.Kill() }
    }
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}

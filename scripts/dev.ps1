param([ValidateSet('restore', 'build', 'test', 'run', 'publish')][string]$Action = 'run')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$env:DOTNET_CLI_HOME = Join-Path $repo '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $repo '.tools/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.tools/nuget-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$dotnet = Join-Path $repo '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_ROOT = Split-Path (Get-Command $dotnet).Source -Parent
switch ($Action) {
    'restore' { & $dotnet restore Catena.slnx --configfile NuGet.Config --locked-mode --disable-parallel -m:1 }
    'build' { & $dotnet build Catena.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false }
    'test' { & $dotnet test Catena.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false --logger 'trx;LogFileName=results.trx' --results-directory artifacts/test-results }
    'run' { & $dotnet run --project src/Catena.App -c Release --no-build --no-restore }
    'publish' {
        $publishDirectory = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/win-x64'))
        $application = Join-Path $publishDirectory 'Catena.App.exe'
        $running = @(Get-Process Catena.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $application })
        foreach ($process in $running) {
            if (!$process.CloseMainWindow() -or !$process.WaitForExit(15000)) { throw 'Catena 尚未正常退出，已停止更新以保留未保存工作区。' }
        }
        & $dotnet publish src/Catena.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64 -p:RestoreConfigFile="$repo/NuGet.Config" -m:1 -p:UseSharedCompilation=false
        if ($LASTEXITCODE -eq 0) {
            # Older publishes included native debug symbols. Keep them in build output only.
            Get-ChildItem -LiteralPath $publishDirectory -File -Filter '*.pdb' -Recurse | ForEach-Object {
                if (!$_.FullName.StartsWith($publishDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected symbol path.' }
                Remove-Item -LiteralPath $_.FullName
            }
        }
    }
}
if ($LASTEXITCODE -ne 0) { throw "dotnet $Action failed ($LASTEXITCODE)" }

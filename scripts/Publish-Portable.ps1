param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\portable-win-x64'),
    [string]$OfflineConfig = ''
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishPath = [IO.Path]::GetFullPath($OutputDirectory)
$projectPath = Join-Path $repoPath 'src\ProjectLens.App\ProjectLens.App.csproj'
New-Item -ItemType Directory -Path (Join-Path $repoPath 'artifacts') -Force | Out-Null
$zipPath = $publishPath.TrimEnd('\', '/') + '.zip'
if (Test-Path -LiteralPath $zipPath) { throw "ZIPは既に存在します。別のOutputDirectoryを指定してください: $zipPath" }
$restoreArguments = @('restore', $projectPath, '-r', 'win-x64', '-p:SelfContained=true', "-p:RuntimeLockDirectory=$repoPath\artifacts")
if ($OfflineConfig) { $restoreArguments += @('--configfile', [IO.Path]::GetFullPath($OfflineConfig)) }
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw '配布用パッケージの復元に失敗しました。' }

# WPFはトリミングしない。ランタイム・Roslyn・日本語リソースを含むフォルダーを配布する。
& dotnet publish $projectPath -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Self-contained発行に失敗しました。' }
Copy-Item -LiteralPath (Join-Path $repoPath 'README.md') -Destination $publishPath -Force
Copy-Item -LiteralPath (Join-Path $repoPath 'docs\THIRD-PARTY-NOTICES.md') -Destination $publishPath -Force
Copy-Item -LiteralPath (Join-Path $repoPath 'docs\licenses') -Destination $publishPath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoPath 'docs') -Destination $publishPath -Recurse -Force
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $publishPath -Force | ForEach-Object FullName) -DestinationPath $zipPath
Write-Host "ZIPを展開し、ProjectLens.App.exeを起動してください: $zipPath"

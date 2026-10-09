param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\artifacts\offline-kit')
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$kitPath = [IO.Path]::GetFullPath($Destination)
$cachePath = Join-Path $kitPath 'package-cache'
$feedPath = Join-Path $kitPath 'feed'
New-Item -ItemType Directory -Path $feedPath -Force | Out-Null

# 許可されたオンライン開発PCでのみ実行する。会社の解析対象プロジェクトは一切扱わない。
& dotnet restore (Join-Path $repoPath 'ProjectLens.sln') --locked-mode --packages $cachePath
if ($LASTEXITCODE -ne 0) { throw 'ソース用パッケージの復元に失敗しました。' }
& dotnet restore (Join-Path $repoPath 'src\ProjectLens.App\ProjectLens.App.csproj') -r win-x64 --packages $cachePath --force -p:SelfContained=true "-p:RuntimeLockDirectory=$kitPath"
if ($LASTEXITCODE -ne 0) { throw '配布用ランタイムパックの復元に失敗しました。' }

# 専用キャッシュに復元したnupkgだけをローカルフィードへコピーし、ハッシュを残す。
$packagesToCopy = Get-ChildItem -LiteralPath $cachePath -Recurse -Filter '*.nupkg' -File
foreach ($package in $packagesToCopy) {
    Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $feedPath $package.Name) -Force
}
$configText = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="offline" value="feed" /></packageSources>
  <config><add key="globalPackagesFolder" value="restored-packages" /></config>
</configuration>
'@
[IO.File]::WriteAllText((Join-Path $kitPath 'NuGet.Offline.Config'), $configText, [Text.UTF8Encoding]::new($false))
Get-ChildItem -LiteralPath $feedPath -Filter '*.nupkg' -File | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    [PSCustomObject]@{ File = $_.Name; SHA256 = $hash.Hash }
} | Export-Csv -LiteralPath (Join-Path $kitPath 'SHA256.csv') -NoTypeInformation -Encoding UTF8
Write-Host "オフラインフィード: $feedPath ($($packagesToCopy.Count) packages)"
Write-Host 'feed、NuGet.Offline.Config、SHA256.csvを一緒に持ち込んでください。SDKとVisual Studioは別途必要です。'

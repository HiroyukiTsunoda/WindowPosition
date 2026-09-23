param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist'))
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\WindowPosition.App\WindowPosition.App.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:DebugSymbols=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw '単一 EXE の発行に失敗しました。' }
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -File)
$unexpected = @($files | Where-Object { $_.Name -ne 'WindowPosition.exe' -and $_.Name -ne 'settings.cfg' -and $_.Name -notlike 'settings.cfg.corrupt.*' })
if ($unexpected.Count -ne 0 -or -not (Test-Path (Join-Path $OutputDirectory 'WindowPosition.exe'))) { throw '単一 EXE 以外の配布ファイルが生成されました。' }
Get-Item (Join-Path $OutputDirectory 'WindowPosition.exe') | Select-Object FullName, Length

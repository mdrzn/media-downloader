# Compila dist\MediaDownloader.exe: un solo file, .NET incluso (non serve installare niente).
# Niente EnableCompressionInSingleFile: l'.exe pesa la meta' ma decomprime tutto in memoria
# (molta piu' RAM per un'app che resta sempre aperta).
#   powershell -ExecutionPolicy Bypass -File app\build.ps1 [-Dotnet <percorso di dotnet.exe>]
param([string]$Dotnet = "dotnet")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

& $Dotnet publish MediaDownloader.csproj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o ..\dist
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Item ..\dist\MediaDownloader.exe | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet publish (Join-Path $root "src/ControllerSync.App/ControllerSync.App.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=embedded `
  -o (Join-Path $root "dist/win-x64")
Write-Host "Built $(Join-Path $root 'dist/win-x64/ControllerSync.exe')"

#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet publish "$root/src/ControllerSync.App/ControllerSync.App.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=embedded \
  -o "$root/dist/win-x64"
echo "Built $root/dist/win-x64/ControllerSync.exe"

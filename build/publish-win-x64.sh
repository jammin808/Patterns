#!/usr/bin/env bash
# Builds the portable single-file Windows exe into dist/win-x64.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet publish src/Patterns.App/Patterns.App.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=embedded \
  -o dist/win-x64

# Web pages inside the engine: WebView2's loader must sit beside the exe (the package puts it
# under runtimes/win-x64/native, which the single-file exe does not always look in).
loader=$(find dist/win-x64/runtimes/win-x64/native -name WebView2Loader.dll 2>/dev/null | head -1)
if [ -n "$loader" ]; then cp "$loader" dist/win-x64/WebView2Loader.dll; fi

echo
echo "Portable app: dist/win-x64/Patterns.exe"
echo "Copy the exe anywhere (USB stick included) — settings, presets and logs live beside it."

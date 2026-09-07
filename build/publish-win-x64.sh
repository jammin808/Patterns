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
  -p:EnableCompressionInSingleFile=false \
  -p:DebugType=embedded \
  -o dist/win-x64

# Web pages inside the engine: WebView2's loader must sit beside the exe (the package puts it
# under runtimes/win-x64/native, which the single-file exe does not always look in).
loader=$(find dist/win-x64/runtimes/win-x64/native -name WebView2Loader.dll 2>/dev/null | head -1)
if [ -n "$loader" ]; then cp "$loader" dist/win-x64/WebView2Loader.dll; fi

# Mini-dumps of a native crash: the runtime writes one only when createdump.exe sits beside the
# exe. The single-file publish folds it into the bundle, so the copy comes from the runtime pack.
createdump=$(find ~/.nuget/packages/microsoft.netcore.app.runtime.win-x64 -ipath '*runtimes/win-x64/native/createdump.exe' 2>/dev/null | sort -V | tail -1)
if [ -n "$createdump" ]; then
  cp "$createdump" dist/win-x64/createdump.exe
  echo "createdump.exe placed beside the exe: the watchdog keeps a mini-dump of a native crash."
else
  echo "createdump.exe not found in the runtime pack: native crashes leave no mini-dump (the note and log still say what happened)."
fi

echo
echo "Portable app: dist/win-x64/Patterns.exe"
echo "Copy the exe anywhere (USB stick included) — settings, presets and logs live beside it."

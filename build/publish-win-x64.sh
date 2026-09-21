#!/usr/bin/env bash
# Builds the portable single-file Windows exe into dist/win-x64.
set -euo pipefail
cd "$(dirname "$0")/.."

# Round 79: the build says which build it is — 0.<round>.0 from the changelog's newest header, with the
# round and the commit in the informational version (0.79.0+round-79.9410292), as CI does with its run number.
round=$(grep -m1 -oE '^## Round [0-9]+' CHANGELOG.md | grep -oE '[0-9]+' || true)
sha=$(git rev-parse --short=7 HEAD 2>/dev/null || echo local)
version="0.${round:-0}.0"
echo "Building $version+round-${round:-0}.$sha"

dotnet publish src/Patterns.App/Patterns.App.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=false \
  -p:DebugType=embedded \
  "-p:Version=$version" \
  "-p:InformationalVersion=$version+round-${round:-0}.$sha" \
  -o dist/win-x64

# Web pages inside the engine: WebView2's loader must sit beside the exe (the package puts it
# under runtimes/win-x64/native, which the single-file exe does not always look in).
loader=$(find dist/win-x64/runtimes/win-x64/native -name WebView2Loader.dll 2>/dev/null | head -1)
if [ -n "$loader" ]; then cp "$loader" dist/win-x64/WebView2Loader.dll; fi

# Mini-dumps of a native crash: the runtime writes one only when createdump.exe sits beside the exe.
# Round 79: the project's own post-publish target (Patterns.App.csproj, PlaceCreatedumpBesideTheExe)
# copies it from the exact runtime pack the publish resolved — here, in the full bundle's script, in
# the .cmd scripts and in CI alike — so this script only checks the result.
if [ ! -f dist/win-x64/createdump.exe ]; then
  echo "ERROR: createdump.exe is not beside the exe — the publish target did not place it (native crashes would leave no mini-dump)." >&2
  exit 1
fi

echo
echo "Portable app: dist/win-x64/Patterns.exe"
echo "Copy the exe anywhere (USB stick included) — settings, presets and logs live beside it."

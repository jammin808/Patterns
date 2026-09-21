#!/usr/bin/env bash
# Builds the portable Windows app WITH libVLC bundled (video plays out of the box)
# into dist/win-x64-full: Patterns.exe + a libvlc folder beside it.
#
# The exe is the same lean single-file publish; the libvlc payload is copied straight
# from the restored VideoLAN.LibVLC.Windows package (its build-time copy items don't
# survive a single-file publish). Works from Linux/macOS cross-publish hosts too.
set -euo pipefail
cd "$(dirname "$0")/.."

# Round 79: the build identity, as build/publish-win-x64.sh and CI stamp it.
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
  -o dist/win-x64-full

# Round 79: createdump.exe comes from the project's post-publish target; a bundle without it leaves no mini-dump.
if [ ! -f dist/win-x64-full/createdump.exe ]; then
  echo "ERROR: createdump.exe is not beside the exe — the publish target did not place it." >&2
  exit 1
fi

# Web pages inside the engine: WebView2's loader must sit beside the exe (the package puts it
# under runtimes/win-x64/native, which the single-file exe does not always look in).
loader=$(find dist/win-x64-full/runtimes/win-x64/native -name WebView2Loader.dll 2>/dev/null | head -1)
if [ -n "$loader" ]; then cp "$loader" dist/win-x64-full/WebView2Loader.dll; fi

dotnet restore src/Patterns.App/Patterns.App.csproj -p:BundleVlc=true

root=$(dotnet nuget locals global-packages --list | sed 's/^[[:space:]]*global-packages:[[:space:]]*//')
pkg=$(find "$root/videolan.libvlc.windows" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | sort | tail -1)
if [ -z "$pkg" ]; then
  echo "ERROR: VideoLAN.LibVLC.Windows not found in the NuGet cache after restore." >&2
  exit 1
fi

dest=dist/win-x64-full/libvlc/win-x64
echo "Copying libvlc from package $pkg..."
mkdir -p "$dest"
cp -r "$pkg/build/x64/." "$dest/"
rm -rf "$dest/include"
rm -f "$dest"/*.lib

if [ ! -f "$dest/libvlc.dll" ] || [ ! -d "$dest/plugins" ]; then
  echo "ERROR: libvlc.dll/plugins missing from $dest" >&2
  exit 1
fi

echo
echo "Portable app with video support: dist/win-x64-full/ (keep Patterns.exe and the libvlc folder together)"

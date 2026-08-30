#!/usr/bin/env bash
# Builds the portable Windows app WITH libVLC bundled (video plays out of the box)
# into dist/win-x64-full: Patterns.exe + a libvlc folder beside it.
#
# The exe is the same lean single-file publish; the libvlc payload is copied straight
# from the restored VideoLAN.LibVLC.Windows package (its build-time copy items don't
# survive a single-file publish). Works from Linux/macOS cross-publish hosts too.
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
  -o dist/win-x64-full

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

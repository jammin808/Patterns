#!/usr/bin/env bash
# Builds the portable Windows app WITH libVLC bundled (video plays out of the box)
# into dist/win-x64-full: Patterns.exe + a libvlc folder beside it.
#
# NOTE: the VideoLAN.LibVLC.Windows package only copies libvlc.dll/plugins correctly on a
# WINDOWS build host (its targets use Windows path globs). Use publish-win-x64-full.cmd on
# Windows, or take the "Patterns-portable-win-x64-full" artifact from CI. On other hosts this
# script produces an incomplete libvlc folder and says so.
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
  -p:BundleVlc=true \
  -o dist/win-x64-full

echo
if [ -f dist/win-x64-full/libvlc/win-x64/libvlc.dll ] && [ -d dist/win-x64-full/libvlc/win-x64/plugins ]; then
  echo "Portable app with video support: dist/win-x64-full/ (keep Patterns.exe and the libvlc folder together)"
else
  echo "WARNING: libvlc.dll/plugins missing — build the full variant on Windows (publish-win-x64-full.cmd) or use the CI artifact."
fi

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

echo
echo "Portable app: dist/win-x64/Patterns.exe"
echo "Copy the exe anywhere (USB stick included) — settings, presets and logs live beside it."

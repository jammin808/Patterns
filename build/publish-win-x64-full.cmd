@echo off
rem Builds the portable Windows app WITH libVLC bundled (video plays out of the box)
rem into dist\win-x64-full: Patterns.exe + a libvlc folder beside it.
pushd "%~dp0.."

dotnet publish src\Patterns.App\Patterns.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=embedded ^
  -p:BundleVlc=true ^
  -o dist\win-x64-full

if errorlevel 1 (
  popd
  exit /b 1
)

echo.
echo Portable app with video support: dist\win-x64-full\ (keep Patterns.exe and the libvlc folder together)
popd

@echo off
rem Builds the portable Windows app WITH libVLC bundled (video plays out of the box)
rem into dist\win-x64-full: Patterns.exe + a libvlc folder beside it.
rem
rem The exe is the same lean single-file publish; the libvlc payload is copied straight
rem from the restored VideoLAN.LibVLC.Windows package (its build-time copy items don't
rem survive a single-file publish).
pushd "%~dp0.."

dotnet publish src\Patterns.App\Patterns.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=embedded ^
  -o dist\win-x64-full

if errorlevel 1 (
  popd
  exit /b 1
)

rem Web pages inside the engine: WebView2's loader must sit beside the exe (the package puts it
rem under runtimes\win-x64\native, which the single-file exe does not always look in).
if exist "dist\win-x64-full\runtimes\win-x64\native\WebView2Loader.dll" copy /Y "dist\win-x64-full\runtimes\win-x64\native\WebView2Loader.dll" "dist\win-x64-full\WebView2Loader.dll" >nul

dotnet restore src\Patterns.App\Patterns.App.csproj -p:BundleVlc=true
if errorlevel 1 (
  popd
  exit /b 1
)

set "VLCPKG="
for /d %%V in ("%USERPROFILE%\.nuget\packages\videolan.libvlc.windows\*") do set "VLCPKG=%%V"
if not defined VLCPKG (
  echo ERROR: VideoLAN.LibVLC.Windows not found in the NuGet cache after restore.
  popd
  exit /b 1
)

echo Copying libvlc from package %VLCPKG%...
xcopy /E /I /Q /Y "%VLCPKG%\build\x64" "dist\win-x64-full\libvlc\win-x64" >nul
rem C++ headers and import libs are useless at runtime — keep the bundle lean.
rd /s /q "dist\win-x64-full\libvlc\win-x64\include" 2>nul
del /q "dist\win-x64-full\libvlc\win-x64\*.lib" 2>nul

if not exist "dist\win-x64-full\libvlc\win-x64\libvlc.dll" (
  echo ERROR: libvlc.dll missing from dist\win-x64-full\libvlc\win-x64
  popd
  exit /b 1
)
if not exist "dist\win-x64-full\libvlc\win-x64\plugins" (
  echo ERROR: libvlc plugins missing from dist\win-x64-full\libvlc\win-x64
  popd
  exit /b 1
)

echo.
echo Portable app with video support: dist\win-x64-full\ (keep Patterns.exe and the libvlc folder together)
popd

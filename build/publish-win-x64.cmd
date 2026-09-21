@echo off
rem Builds the portable single-file Windows exe into dist\win-x64.
pushd "%~dp0.."

rem Round 79: the build says which build it is - 0.<round>.0 from the changelog's newest header, with the
rem round and the commit in the informational version, as CI does with its run number.
set "ROUND=0"
for /f "tokens=3" %%r in ('findstr /r /b /c:"## Round " CHANGELOG.md') do (
  set "ROUND=%%r"
  goto :round_found
)
:round_found
set "SHA=local"
for /f %%s in ('git rev-parse --short=7 HEAD 2^>nul') do set "SHA=%%s"
set "VERSION=0.%ROUND%.0"
echo Building %VERSION%+round-%ROUND%.%SHA%

dotnet publish src\Patterns.App\Patterns.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=false ^
  -p:DebugType=embedded ^
  "-p:Version=%VERSION%" ^
  "-p:InformationalVersion=%VERSION%+round-%ROUND%.%SHA%" ^
  -o dist\win-x64

if errorlevel 1 (
  popd
  exit /b 1
)

rem Web pages inside the engine: WebView2's loader must sit beside the exe (the package puts it
rem under runtimes\win-x64\native, which the single-file exe does not always look in).
if exist "dist\win-x64\runtimes\win-x64\native\WebView2Loader.dll" copy /Y "dist\win-x64\runtimes\win-x64\native\WebView2Loader.dll" "dist\win-x64\WebView2Loader.dll" >nul

rem Round 79: createdump.exe comes from the project's post-publish target; without it a native crash leaves no mini-dump.
if not exist "dist\win-x64\createdump.exe" (
  echo ERROR: createdump.exe is not beside the exe - the publish target did not place it.
  popd
  exit /b 1
)

echo.
echo Portable app: dist\win-x64\Patterns.exe
echo Copy the exe anywhere (USB stick included) - settings, presets and logs live beside it.
popd

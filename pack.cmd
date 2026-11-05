@echo off
setlocal
rem ---------------------------------------------------------------------------
rem Pack LikeShot into one single-file Windows executable under dist\.
rem Usage:  pack.cmd [version]     (default 0.1.0, keep it in sync with the csproj)
rem Needs the .NET 8 SDK. Output runs on any x64 Windows 10/11, no runtime needed.
rem ---------------------------------------------------------------------------
cd /d "%~dp0"

set VER=%1
if "%VER%"=="" set VER=0.1.4

echo publishing single-file exe (this takes a minute) ...
dotnet publish src\LikeShot\LikeShot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o build\singlefile
if errorlevel 1 (
    echo publish FAILED
    exit /b 1
)

if not exist dist mkdir dist
copy /y build\singlefile\LikeShot.exe "dist\LikeShot-%VER%-win-x64.exe" >nul
if errorlevel 1 exit /b 1

for %%F in ("dist\LikeShot-%VER%-win-x64.exe") do echo done: %%~nxF  %%~zF bytes
exit /b 0

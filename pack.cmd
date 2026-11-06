@echo off
setlocal
rem ---------------------------------------------------------------------------
rem Pack LikeShot for Windows: a single-file exe plus a real installer.
rem Usage:  pack.cmd [version]     (default below, keep it in sync with the csproj)
rem Needs the .NET 8 SDK. The installer additionally needs Inno Setup:
rem   winget install JRSoftware.InnoSetup
rem Without Inno Setup the exe is still produced and the setup step is skipped.
rem ---------------------------------------------------------------------------
cd /d "%~dp0"

set VER=%1
if "%VER%"=="" set VER=0.1.6

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

rem ---- installer (Inno Setup) ----------------------------------------------
set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe
if defined ISCC_PATH set ISCC=%ISCC_PATH%
if exist "%ISCC%" goto have_iscc

rem Inno does not always land under Program Files (this machine has it on D:),
rem so fall back to the InstallLocation it records in the uninstall key.
set INNODIR=
for /f "tokens=1,2,*" %%A in ('reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" /v InstallLocation 2^>nul ^| findstr /i "InstallLocation"') do if not "%%C"=="" set INNODIR=%%C
for /f "tokens=1,2,*" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" /v InstallLocation 2^>nul ^| findstr /i "InstallLocation"') do if not "%%C"=="" set INNODIR=%%C
for /f "tokens=1,2,*" %%A in ('reg query "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" /v InstallLocation 2^>nul ^| findstr /i "InstallLocation"') do if not "%%C"=="" set INNODIR=%%C
if defined INNODIR if exist "%INNODIR%\ISCC.exe" set ISCC=%INNODIR%\ISCC.exe

:have_iscc
if not exist "%ISCC%" (
    echo NOTE: Inno Setup not found, setup.exe skipped.
    echo       Install it once with:  winget install JRSoftware.InnoSetup
    exit /b 0
)

echo building installer with "%ISCC%" ...
"%ISCC%" /Q /DMyAppVersion=%VER% installer\LikeShot.iss
if errorlevel 1 (
    echo installer build FAILED
    exit /b 1
)

for %%F in ("dist\LikeShot-%VER%-win-x64-setup.exe") do echo done: %%~nxF  %%~zF bytes
exit /b 0

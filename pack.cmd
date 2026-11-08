@echo off
setlocal
rem ---------------------------------------------------------------------------
rem Pack LikeShot into the three files that go on the GitHub release:
rem   dist\LikeShot-<ver>-win-x64-setup.exe          the installer (main asset)
rem   dist\LikeShot-<ver>-win-x64-selfcontained.zip  green build, no runtime needed
rem   dist\LikeShot-<ver>-win-x64-portable.zip       green build, needs .NET 8 desktop runtime
rem The bare single-file exe is deliberately not copied into dist any more:
rem it is the same bytes as the one inside selfcontained.zip and nobody downloads it.
rem Usage:  pack.cmd [version]     (keep it in sync with the csproj)
rem Needs the .NET 8 SDK. The installer additionally needs Inno Setup:
rem   winget install JRSoftware.InnoSetup
rem Without Inno Setup the two zips are still produced and the setup step is skipped.
rem ---------------------------------------------------------------------------
cd /d "%~dp0"

set VER=%1
if "%VER%"=="" set VER=0.1.30

echo [1/4] publishing self-contained single-file exe (this takes a minute) ...
dotnet publish src\LikeShot\LikeShot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o build\singlefile
if errorlevel 1 (
    echo self-contained publish FAILED
    exit /b 1
)

echo [2/4] publishing framework-dependent exe ...
dotnet publish src\LikeShot\LikeShot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o build\portable
if errorlevel 1 (
    echo portable publish FAILED
    exit /b 1
)

if not exist dist mkdir dist
rem bsdtar, not the GNU tar that ships with Git for Windows - only bsdtar writes zip.
set TAR=%SystemRoot%\System32\tar.exe
del /q "dist\LikeShot-%VER%-win-x64-selfcontained.zip" 2>nul
del /q "dist\LikeShot-%VER%-win-x64-portable.zip" 2>nul
"%TAR%" -a -c -f "dist\LikeShot-%VER%-win-x64-selfcontained.zip" -C build\singlefile LikeShot.exe
if errorlevel 1 (
    echo selfcontained zip FAILED
    exit /b 1
)
"%TAR%" -a -c -f "dist\LikeShot-%VER%-win-x64-portable.zip" -C build\portable LikeShot.exe
if errorlevel 1 (
    echo portable zip FAILED
    exit /b 1
)

rem ---- installer (Inno Setup) ----------------------------------------------
echo [3/4] looking for Inno Setup ...
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
    goto report
)

echo building installer with "%ISCC%" ...
"%ISCC%" /Q /DMyAppVersion=%VER% installer\LikeShot.iss
if errorlevel 1 (
    echo installer build FAILED
    exit /b 1
)

:report
echo [4/4] release files in dist\ :
for %%F in ("dist\LikeShot-%VER%-win-x64-setup.exe") do echo   %%~nxF  %%~zF bytes
for %%F in ("dist\LikeShot-%VER%-win-x64-selfcontained.zip") do echo   %%~nxF  %%~zF bytes
for %%F in ("dist\LikeShot-%VER%-win-x64-portable.zip") do echo   %%~nxF  %%~zF bytes
exit /b 0

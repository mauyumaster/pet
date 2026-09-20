@echo off
rem ============================================================
rem  Azhu desktop-pet launcher  --  the ONE entry point.
rem  KEEP THIS FILE ASCII-ONLY + CRLF-TERMINATED.
rem    * cmd.exe decodes batch files with the OEM codepage, 936 on
rem      a Chinese Windows, so UTF-8 Chinese turns into mojibake.
rem    * an LF-only file makes cmd.exe fail SILENTLY partway through
rem      label and paren blocks. This file was once corrupted so that
rem      its line-ending markers became literal text; that merged two
rem      lines and killed the build step without printing any error.
rem ============================================================
cd /d "%~dp0"
rem ---- NuGet cache ----
rem This line used to pin NUGET_PACKAGES=D:/nuget, a directory that does not
rem exist. NuGet then used that empty folder as the global cache and every
rem restore that needed a package went to the network. Removed, so the normal
rem per-user cache (~\.nuget\packages) is used. The WinRT projection pack
rem (microsoft.windows.sdk.net.ref) lives there, which matters now that the
rem TFM carries a Windows SDK version.

rem ---- Release is the only configuration we build or launch ----
rem This file used to start bin\Debug while releases were built with
rem "-c Release". The two drifted apart, so double-clicking here ran a
rem STALE binary and every "restart the pet to see the change" claim
rem was quietly false. Project rule: one canonical output directory,
rem or the copies diverge. Hence Release only, rebuilt on every
rem launch. An incremental no-op build costs about 3 seconds.
rem MUST match <TargetFramework> in pet.csproj -- the folder name IS the TFM.
set "PETEXE=bin\Release\net9.0-windows10.0.19041.0\pet.exe"

echo [build] incremental Release build...
dotnet build -c Release -v q --nologo
if errorlevel 1 goto :buildfailed

if not exist "%PETEXE%" goto :nobinary
start "" "%PETEXE%"
exit /b 0

:buildfailed
echo.
echo [x] Build FAILED -- NOT launching. The binary may not match the source.
echo     To see the real errors, run this in this folder:
echo         dotnet build -c Release
echo.
tasklist /fi "imagename eq pet.exe" 2>nul | find /i "pet.exe" >nul
if errorlevel 1 goto :norunning
echo     A pet is currently RUNNING, and it locks pet.exe and pet.dll.
echo     Close it first: right-click the tray icon, then choose Exit.
echo     After that, run this file again.
goto :hint

:norunning
echo     No pet is running, so the failure is in the build itself.

:hint
echo.
echo     To run the previously built binary anyway, which may be stale:
echo         start "" "%PETEXE%"
echo.
pause
exit /b 1

:nobinary
echo.
echo [x] "%PETEXE%" is missing and the build did not produce it.
echo     Build manually, then run this file again.
echo.
pause
exit /b 1

@echo off
rem ============================================================
rem  Azhu desktop-pet release packer  --  builds the redistributable zip.
rem  KEEP THIS FILE ASCII-ONLY + CRLF-TERMINATED.
rem    * cmd.exe decodes batch files with the OEM codepage (936 on a
rem      Chinese Windows), so UTF-8 Chinese turns into mojibake.
rem    * LF-only line endings make cmd.exe fail SILENTLY partway through
rem      label and paren blocks. This has bitten this project before.
rem
rem  WHY THIS SCRIPT EXISTS:
rem  The publish step alone is NOT the release. Two things used to be done
rem  by hand and got forgotten, which is exactly how a release goes out
rem  broken while every test is green:
rem    1. persona.md must sit NEXT TO pet.exe. Persona.cs looks for a bare
rem       persona.md in the exe directory before walking up the repo tree,
rem       and a released zip has no repo tree. Forget it and she silently
rem       falls back to the built-in skeleton -- still runs, still talks,
rem       just... not her.
rem    2. The persona text is CC BY-NC-SA licensed while the code is MIT.
rem       The redistributed persona.md therefore has to carry its own
rem       license header; pet/persona.md is the copy that has it.
rem  Doing both here means the release step is one command and cannot
rem  quietly skip a part of itself.
rem ============================================================
setlocal
cd /d "%~dp0"

rem MUST match <TargetFramework> in pet.csproj -- the folder name IS the TFM.
set "PUBDIR=bin\Release\net9.0-windows10.0.19041.0\win-x64\publish"
set "ZIP=AzhuPet-v0.1.0-win-x64.zip"

echo [1/4] publish (framework-dependent single file)...
rem NOTE: `-p:` not `/p:` -- Git Bash eats the slash form.
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -v q --nologo
if errorlevel 1 goto :failed

if not exist "%PUBDIR%\pet.exe" goto :nobinary

echo [2/4] stage persona.md next to the exe...
copy /y "persona.md" "%PUBDIR%\persona.md" >nul
if errorlevel 1 goto :failed

echo [3/4] guard: the release must contain BOTH files...
if not exist "%PUBDIR%\persona.md" goto :nopersona

echo [4/4] zip...
rem tar is present on Windows 10 1803+ and does zip without extra tooling.
if exist "%ZIP%" del /q "%ZIP%"
tar -a -c -f "%ZIP%" -C "%PUBDIR%" pet.exe persona.md
if errorlevel 1 goto :failed

echo.
echo   [+] %ZIP%
for %%F in ("%ZIP%") do echo       %%~zF bytes
echo.
echo   Contents:
tar -t -f "%ZIP%"
echo.
echo   Next: this is a LOCAL artifact only. Nothing was uploaded.
echo         To publish, attach this zip to a GitHub Release (tag v0.1.0).
exit /b 0

:failed
echo.
echo [x] Build or packaging FAILED. Nothing was zipped.
pause
exit /b 1

:nobinary
echo.
echo [x] "%PUBDIR%\pet.exe" is missing after publish.
pause
exit /b 1

:nopersona
echo.
echo [x] persona.md did not make it into the release folder.
echo     Shipping without it means she falls back to the skeleton voice.
pause
exit /b 1

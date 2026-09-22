@echo off
rem ============================================================
rem  Azhu desktop-pet deliver  --  publish, then install to the app folder.
rem  KEEP THIS FILE ASCII-ONLY + CRLF-TERMINATED.
rem    * cmd.exe decodes batch files with the OEM codepage, 936 on a
rem      Chinese Windows, so UTF-8 Chinese turns into mojibake.
rem    * LF-only line endings make cmd.exe fail SILENTLY partway through
rem      label and paren blocks. This has bitten this project more than once.
rem
rem  WHY THIS SCRIPT EXISTS -- the one-install-location rule:
rem  She used to run straight out of bin\Release\..., which is a BUILD OUTPUT
rem  directory. THREE things pointed at it, and all three broke differently:
rem    1. The desktop shortcut ran run-pet.cmd, which rebuilds on every
rem       launch. So it silently OVERWROTE the exe the self-updater had just
rem       installed -- while persona.md, not being a build artifact, stayed
rem       new. Result: old exe, new persona, versions mismatched, and the
rem       update looked like it "worked" while doing nothing.
rem    2. The autostart key stored that build path. A `dotnet clean`, a TFM
rem       bump or an SDK change would kill autostart with no error at all.
rem    3. There was no sense in which she "was installed" anywhere.
rem  So: keep the dev tree for developing, and give her ONE fixed install
rem  folder that the shortcut, autostart and updater all agree on.
rem
rem  Usage:  double-click this file. Run run-pet.cmd instead to just try the
rem          current source without touching the installed copy.
rem ============================================================
setlocal
cd /d "%~dp0"

rem ---- the ONE install location. Change this line if you want her elsewhere.
set "APPDIR=D:\AzhuPet"

rem MUST match <TargetFramework> in pet.csproj -- the folder name IS the TFM.
set "PUBDIR=bin\Release\net9.0-windows10.0.19041.0\win-x64\publish"
rem Relative path of the model INSIDE the install (must match PetConfig.ModelRelPath).
set "MODELREL=model\chibi_maid_pet.glb"

echo [1/6] publish (framework-dependent single file)...
rem NOTE: `-p:` not `/p:` -- Git Bash eats the slash form.
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -v q --nologo
if errorlevel 1 goto :failed
if not exist "%PUBDIR%\pet.exe" goto :nobinary

echo [2/6] stage persona.md next to the exe...
rem WHY: Persona.cs looks for a bare persona.md in the exe directory FIRST,
rem and an installed folder has no repo tree to walk up. Forget it and she
rem silently falls back to the built-in skeleton -- still runs, still talks,
rem just... not her.
copy /y "persona.md" "%PUBDIR%\persona.md" >nul
if errorlevel 1 goto :failed

echo [3/6] stage %MODELREL% next to the exe...
rem WHY: the 13MB GLB lives in the PARENT folder (..\model) during
rem development and pet.exe used to walk UP the tree to find it. An install
rem folder has no tree, so put it beside the exe.
if not exist "..\model\chibi_maid_pet.glb" goto :nomodel
if not exist "%PUBDIR%\model" mkdir "%PUBDIR%\model"
copy /y "..\model\chibi_maid_pet.glb" "%PUBDIR%\model\chibi_maid_pet.glb" >nul
if errorlevel 1 goto :failed

echo [4/6] guard: the publish folder must hold ALL THREE files...
rem WHY the guard: a half-staged publish folder is exactly how a release
rem goes out broken while every test is green. Check before touching the
rem installed copy, so a bad build can never damage a good install.
if not exist "%PUBDIR%\persona.md" goto :nopersona
if not exist "%PUBDIR%\model\chibi_maid_pet.glb" goto :nomodel

rem ---- version: read from the BUILT EXE, never from a hardcoded string ----
rem WHY: the version lives in exactly one place (pet.csproj <Version>). A
rem second copy could disagree with the binary, and then the updater either
rem never offers the update or offers it forever.
for /f "usebackq tokens=*" %%V in (`"%PUBDIR%\pet.exe" --version`) do set "VER=%%V"
if "%VER%"=="" goto :nover

echo [5/6] install to %APPDIR%  (version %VER%)...
rem WHY we refuse to install while she runs: pet.exe would be locked, the
rem copy would fail halfway, and you would be left with a mix of old and new
rem files -- the worst state, because it looks fine until something odd.
tasklist /fi "imagename eq pet.exe" 2>nul | find /i "pet.exe" >nul
if not errorlevel 1 goto :running

if not exist "%APPDIR%" mkdir "%APPDIR%"
if not exist "%APPDIR%\model" mkdir "%APPDIR%\model"

copy /y "%PUBDIR%\pet.exe" "%APPDIR%\pet.exe" >nul
if errorlevel 1 goto :failed
copy /y "%PUBDIR%\persona.md" "%APPDIR%\persona.md" >nul
if errorlevel 1 goto :failed
copy /y "%PUBDIR%\model\chibi_maid_pet.glb" "%APPDIR%\model\chibi_maid_pet.glb" >nul
if errorlevel 1 goto :failed

echo [6/6] verify what actually landed in %APPDIR%...
if not exist "%APPDIR%\pet.exe" goto :verifyfail
if not exist "%APPDIR%\persona.md" goto :verifyfail
if not exist "%APPDIR%\model\chibi_maid_pet.glb" goto :verifyfail
rem Ask the INSTALLED binary its version. Comparing it to the built one is
rem the only way to know the copy really replaced an older file rather than
rem silently keeping it.
set "INSTVER="
for /f "usebackq tokens=*" %%V in (`"%APPDIR%\pet.exe" --version`) do set "INSTVER=%%V"
if "%INSTVER%"=="%VER%" goto :installed
goto :verifyfail

:installed
echo.
echo   [+] installed  %APPDIR%
echo       version    %INSTVER%
echo.
for %%F in ("%APPDIR%\pet.exe") do echo       pet.exe       %%~zF bytes
for %%F in ("%APPDIR%\persona.md") do echo       persona.md    %%~zF bytes
for %%F in ("%APPDIR%\model\chibi_maid_pet.glb") do echo       model         %%~zF bytes
echo.
echo   Start her by double-clicking pet.exe in that folder (or the desktop
echo   shortcut, if it points there). Nothing was uploaded anywhere.
echo.
pause
exit /b 0

:running
echo.
echo [x] She is RUNNING -- not installing.
echo.
echo     pet.exe is locked while she runs, so the copy would fail halfway and
echo     leave you with a mix of old and new files.
echo.
echo     Close her first, then run this file again:
echo       * right-click her on the desktop, or the tray icon, and choose Exit
echo.
pause
exit /b 1

:verifyfail
echo.
echo [x] The installed copy did not verify.
echo     Looked at: %APPDIR%
echo     Expected version %VER%, got "%INSTVER%".
echo     She may now be a mix of old and new files. Re-run this file; if it
echo     keeps failing, delete %APPDIR% and run it once more for a clean install.
echo.
pause
exit /b 1

:nover
echo.
echo [x] Could not read the version from "%PUBDIR%\pet.exe".
echo     The publish step did not produce a runnable binary.
echo.
pause
exit /b 1

:failed
echo.
echo [x] Build or install FAILED. The installed copy was not touched
echo     unless a copy step above had already succeeded.
echo.
pause
exit /b 1

:nobinary
echo.
echo [x] "%PUBDIR%\pet.exe" is missing after publish.
echo     Run this to see the real errors:
echo         dotnet publish -c Release -r win-x64 --self-contained false
echo.
pause
exit /b 1

:nopersona
echo.
echo [x] persona.md did not make it into the publish folder.
echo     Installing without it means she falls back to the skeleton voice:
echo     she would still run and still talk, just not as herself.
echo.
pause
exit /b 1

:nomodel
echo.
echo [x] The 3D model did not make it into the publish folder.
echo     Looked for: ..\model\chibi_maid_pet.glb  (13MB, lives beside the pet folder)
echo     Without it the app dies at startup with "cannot find model/chibi_maid_pet.glb".
echo.
pause
exit /b 1

@echo off
rem ============================================================
rem  Azhu desktop-pet release packer -- builds BOTH redistributables:
rem    AzhuPet-v<ver>-win-x64.zip         portable zip (also the updater feed)
rem    AzhuPet-v<ver>-win-x64-setup.exe   installer (needs Inno Setup 6)
rem  KEEP THIS FILE ASCII-ONLY + CRLF-TERMINATED.
rem    * cmd.exe decodes batch files with the OEM codepage, 936 on a
rem      Chinese Windows, so UTF-8 Chinese turns into mojibake.
rem    * LF-only line endings make cmd.exe fail SILENTLY partway through
rem      label and paren blocks. This has bitten this project before.
rem
rem  WHY THIS SCRIPT EXISTS:
rem  The publish step alone is NOT the release. Three things used to be done
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
rem    3. The zip and the installer MUST be built from the SAME publish
rem       folder. Build them from different ones and the two downloads can
rem       carry different binaries under one version number.
rem  Doing all of it here means the release step is one command and cannot
rem  quietly skip a part of itself.
rem
rem  WHY the "is she running" guard (added 2026-09-23):
rem  dotnet publish writes into bin\Release\...\pet.exe, which is LOCKED
rem  while she runs, so the build dies halfway and leaves a mix of old and
rem  new files. deliver.cmd has had this guard for a while; this script did
rem  not -- and it cost a build.
rem ============================================================
setlocal
cd /d "%~dp0"

rem MUST match <TargetFramework> in pet.csproj -- the folder name IS the TFM.
set "PUBDIR=bin\Release\net9.0-windows10.0.19041.0\win-x64\publish"

set "ISSFILE=azhupet.iss"
set "FEED=version.json"
rem Relative path of the model INSIDE the release (must match PetConfig.ModelRelPath).
set "MODELREL=model\chibi_maid_pet.glb"

echo [1/8] guard: is she running?
rem WHY: see the header. pet.exe is locked while she runs.
tasklist /fi "imagename eq pet.exe" 2>nul | find /i "pet.exe" >nul
if not errorlevel 1 goto :running

echo [2/8] publish (framework-dependent single file)...
rem NOTE: `-p:` not `/p:` -- Git Bash eats the slash form.
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -v q --nologo
if errorlevel 1 goto :failed

if not exist "%PUBDIR%\pet.exe" goto :nobinary

rem Gate: the browser credential window needs the NATIVE loader shipped next to the exe.
rem WHY this has to be a gate and not an afterthought: WebView2Loader.dll is a native
rem library. If publish drops it, the project still COMPILES, every offline test still
rem passes, and it only breaks at the exact moment a user clicks "browser login".
rem So: (1) check the file is there, (2) actually RUN --webtest, which P/Invokes the
rem loader -- that is the only way to prove it resolves in this layout.
if not exist "%PUBDIR%\WebView2Loader.dll" goto :noloader
echo [2b/8] webtest: verifying the embedded-browser dependency chain...
"%PUBDIR%\pet.exe" --webtest
if errorlevel 1 goto :webfail

rem Read the version from the BUILT EXE, never from a hardcoded string.
rem WHY: the version lives in exactly one place (pet.csproj <Version>). If this
rem script kept its own copy, a release could ship an exe that says 0.2.0 while
rem the feed says 0.1.0 -- and the updater would then either never offer the
rem update or offer it forever. Asking the binary makes that impossible.
rem NOTE: pet.exe --version prints just the number (see Program.cs RunVersion).
for /f "usebackq tokens=*" %%V in (`"%PUBDIR%\pet.exe" --version`) do set "VER=%%V"
if "%VER%"=="" goto :nover

set "ZIP=AzhuPet-v%VER%-win-x64.zip"
set "SETUP=AzhuPet-v%VER%-win-x64-setup.exe"

echo [3/8] stage persona.md next to the exe...
copy /y "persona.md" "%PUBDIR%\persona.md" >nul
if errorlevel 1 goto :failed

echo [4/8] stage %MODELREL% next to the exe...
rem WHY: the 13MB GLB lives in the PARENT folder (..\model) during development --
rem pet.exe used to walk UP the tree to find it. In a released zip there is no
rem tree to walk, so the app died with "cannot find model/chibi_maid_pet.glb".
rem Put it beside the exe so ResolveModel() finds it without any tree.
if not exist "..\model\chibi_maid_pet.glb" goto :nomodel
if not exist "%PUBDIR%\model" mkdir "%PUBDIR%\model"
copy /y "..\model\chibi_maid_pet.glb" "%PUBDIR%\model\chibi_maid_pet.glb" >nul
if errorlevel 1 goto :failed

echo [5/8] guard: the release must contain ALL THREE files...
if not exist "%PUBDIR%\persona.md" goto :nopersona
if not exist "%PUBDIR%\model\chibi_maid_pet.glb" goto :nomodel

echo [6/8] zip...
rem tar is present on Windows 10 1803+ and does zip without extra tooling.
if exist "%ZIP%" del /q "%ZIP%"
tar -a -c -f "%ZIP%" -C "%PUBDIR%" pet.exe persona.md model/chibi_maid_pet.glb
if errorlevel 1 goto :failed

echo [7/8] installer (Inno Setup)...
rem WHY pushd: azhupet.iss uses paths relative to its own folder
rem (..\bin\Release\...\publish for input, ..\release for output), so the
rem compiler has to run from installer\ or every path resolves one level off.
set "ISCC="
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC goto :noiscc

pushd "installer"
rem /DAppVersion= is passed so the .iss does not keep its own copy of the
rem version -- and azhupet.iss cross-checks it against the real FileVersion of
rem the pet.exe inside the package, then fails the build if they disagree.
"%ISCC%" /DAppVersion=%VER% "%ISSFILE%"
set "ISCCRC=%errorlevel%"
popd
if not "%ISCCRC%"=="0" goto :isscfail

if not exist "release\%SETUP%" goto :issetupmissing

echo [8/8] write %FEED% (the updater's feed)...
rem NOTE: the zip URL points at a GitHub Release asset. Bump this by hand when
rem you cut a new tag -- it cannot be derived from the version alone.
rem ASCII only; the updater parses it with a tiny hand-rolled reader.
rem WARNING: keep the owner/repo exactly as `git remote -v` reports it.
rem The repo is mauyumaster/pet (NOT AzhuPet) -- we shipped 404s once by guessing.
rem
rem NOTE: the feed keeps pointing at the ZIP, not the installer. Self-update
rem replaces files in place, so it must not shell out to an installer -- and
rem the zip stays the artifact that works in both layouts (installed or portable).
> "%FEED%" echo {
>>"%FEED%" echo   "version": "%VER%",
>>"%FEED%" echo   "url": "https://github.com/mauyumaster/pet/releases/download/v%VER%/AzhuPet-v%VER%-win-x64.zip",
>>"%FEED%" echo   "notes": ""
>>"%FEED%" echo }
if not exist "%FEED%" goto :nofeed

echo.
echo   [+] %ZIP%
for %%F in ("%ZIP%") do echo       %%~zF bytes   (portable zip)
echo   [+] release\%SETUP%
for %%F in ("release\%SETUP%") do echo       %%~zF bytes   (installer)
echo   [+] %FEED%
echo.
echo   Zip contents:
tar -t -f "%ZIP%"
echo.
echo   Next: this is a LOCAL artifact only. Nothing was uploaded.
echo         To publish, attach BOTH files above to a GitHub Release
echo         (tag v%VER%), then commit %FEED%.
echo         Upload the ZIP even though the installer exists -- the
echo         self-updater reads %FEED% and downloads the zip.
exit /b 0

:running
echo.
echo [x] She is RUNNING -- not building.
echo.
echo     dotnet publish writes into %PUBDIR%\..\pet.exe,
echo     which is locked while she runs. The build would die halfway and leave
echo     you with a mix of old and new files.
echo.
echo     Close her first (right-click the tray icon and choose Exit), then
echo     run this file again.
pause
exit /b 1

:nover
echo.
echo [x] Could not read the version from "%PUBDIR%\pet.exe".
echo     Run a build first: dotnet build -c Release
pause
exit /b 1

:nofeed
echo.
echo [x] %FEED% was not written.
echo     Without it the updater has nothing to read and will never offer updates.
pause
exit /b 1

:noiscc
echo.
echo [x] Inno Setup 6 compiler (ISCC.exe) not found -- the installer was NOT built.
echo     The zip above is fine; only the setup.exe is missing.
echo.
echo     Install it with any of:
echo         winget install JRSoftware.InnoSetup
echo         https://jrsoftware.org/isdl.php
echo     Then run this file again.
pause
exit /b 1

:isscfail
echo.
echo [x] The installer did not compile (ISCC exit %ISCCRC%).
echo     The zip above is fine; only the setup.exe is missing.
echo     Common causes, both caught at compile time by installer\azhupet.iss:
echo       * EXE_VERSION_MISMATCH -- the pet.exe in the publish folder is not
echo         version %VER%. Run this file again from a clean publish.
echo       * MISSING_PET_EXE -- publish produced nothing.
pause
exit /b 1

:issetupmissing
echo.
echo [x] ISCC reported success but release\%SETUP% is not there.
echo     Check OutputDir in installer\azhupet.iss.
pause
exit /b 1

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

:noloader
echo.
echo [x] WebView2Loader.dll is missing from the publish folder.
echo     It is a NATIVE file that comes with the WebView2 package; publish normally
echo     drops it next to pet.exe. Without it the app still runs, but the moment
echo     someone clicks "browser login" on the WorkBuddy card the window dies with
echo     a DllNotFoundException. Fix: make sure the PackageReference to
echo     Microsoft.Web.WebView2 is still in pet.csproj, then publish again.
pause
exit /b 1

:webfail
echo.
echo [x] pet.exe --webtest FAILED on the published binary.
echo     Read the three PASS/FAIL lines above to see which one:
echo       * runtime missing  -- this machine has no WebView2 Evergreen runtime.
echo         Install it and re-run. End users on Win11 already have it.
echo       * loader not found -- the native file is shipped but cannot be resolved
echo         in this layout (a packaging bug, not a machine problem).
echo     Note: this only disables the NEW browser-login route. Manual pasting of
echo     credentials still works, so the balance feature itself is not broken.
pause
exit /b 1

:nopersona
echo.
echo [x] persona.md did not make it into the release folder.
echo     Shipping without it means she falls back to the skeleton voice.
pause
exit /b 1

:nomodel
echo.
echo [x] The 3D model did not make it into the release folder.
echo     Looked for: ..\model\chibi_maid_pet.glb  (13MB, lives beside the pet folder)
echo     Without it the app dies at startup with "cannot find model/chibi_maid_pet.glb".
pause
exit /b 1

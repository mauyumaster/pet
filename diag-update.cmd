@echo off
rem ============================================================
rem  Azhu desktop-pet self-update DIAGNOSTIC  --  double-click me.
rem  KEEP THIS FILE ASCII-ONLY + CRLF-TERMINATED.
rem    * cmd.exe decodes batch files with the OEM codepage (936 on a
rem      Chinese Windows), so UTF-8 Chinese turns into mojibake.
rem    * LF-only line endings make cmd.exe fail SILENTLY partway
rem      through label and paren blocks. See .gitattributes: this
rem      extension is pinned to eol=crlf on purpose.
rem
rem  WHY THIS EXISTS:
rem  "Check for updates" in the settings panel can only say "failed" --
rem  there is no room there to explain WHY. But "why" is the whole
rem  question: the update path can break in four completely different
rem  ways, and each needs a different fix:
rem    1. a stale proxy the machine no longer runs (seen for real: the
rem       env var pointed at 127.0.0.1:61827, which nothing listened on)
rem    2. a wrong repo name in the feed URL (seen for real: AzhuPet vs pet,
rem       where a wrong name is a 404 that LOOKS like "no network")
rem    3. no GitHub Release / no zip asset attached (feed is fine, download 404s)
rem    4. version.json not committed after repacking (feed serves the old one)
rem  This script prints all four checks in one go.
rem ============================================================
setlocal
cd /d "%~dp0"

rem MUST match <TargetFramework> in pet.csproj -- the folder name IS the TFM.
set "PETEXE=bin\Release\net9.0-windows10.0.19041.0\pet.exe"

if not exist "%PETEXE%" goto :nobinary

echo ============================================================
echo   Azhu pet  --  self-update diagnostic
echo ============================================================
echo.
"%PETEXE%" --updatediag
set "RC=%ERRORLEVEL%"
echo.
echo ============================================================
if "%RC%"=="0" (
    echo   Result: PASS  --  the update channel works end to end.
) else (
    echo   Result: FAIL  --  read the checklist printed above.
)
echo ============================================================
echo.
pause
exit /b %RC%

:nobinary
echo.
echo [x] "%PETEXE%" was not found.
echo     Build it first, in this folder:
echo         dotnet build -c Release
echo     (or just run run-pet.cmd once, which builds then launches)
echo.
pause
exit /b 1

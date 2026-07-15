@echo off
setlocal enabledelayedexpansion

rem ==============================
rem HISAB KITAB - Diagnostics
rem
rem IMPORTANT:
rem Users sometimes run this script from the *project* folder (e.g. installer\InnoSetup)
rem instead of the installed folder. To avoid misleading output, we locate the real
rem installed EXE automatically.
rem ==============================

set "APPDIR=%~dp0"
set "EXE=%APPDIR%HISAB KITAB.exe"

rem If EXE is not beside this .bat, try the normal install locations.
if not exist "%EXE%" (
  set "CAND1=%ProgramFiles(x86)%\HISAB KITAB\"
  set "CAND2=%ProgramFiles%\HISAB KITAB\"
  if exist "!CAND1!HISAB KITAB.exe" (
    set "APPDIR=!CAND1!"
    set "EXE=!APPDIR!HISAB KITAB.exe"
  ) else if exist "!CAND2!HISAB KITAB.exe" (
    set "APPDIR=!CAND2!"
    set "EXE=!APPDIR!HISAB KITAB.exe"
  )
)
set "LOGDIR=%LOCALAPPDATA%\Hisab Kitab\Logs"
set "OUT=%LOGDIR%\installer_diag.txt"
set "STARTUP=%LOGDIR%\startup_last.txt"

if not exist "%LOGDIR%" md "%LOGDIR%" >nul 2>&1

(
  echo ===== HISAB KITAB - Installer Diagnostics =====
  echo Time: %DATE% %TIME%
  echo User: %USERNAME%
  echo Computer: %COMPUTERNAME%
  echo OS Info:
  ver
  systeminfo | findstr /B /C:"OS Name" /C:"OS Version" /C:"System Type"
  echo.
  echo Candidate install locations:
  echo - Bat directory: %~dp0
  echo - ProgramFiles(x86): %ProgramFiles(x86)%\HISAB KITAB\
  echo - ProgramFiles: %ProgramFiles%\HISAB KITAB\
  echo.
  echo InstallDir: %APPDIR%
  echo.
  echo Files in install dir:
  dir /b "%APPDIR%"
  echo.
  echo Logs dir: %LOGDIR%
  if exist "%LOGDIR%" (
    dir /b "%LOGDIR%"
  ) else (
    echo (Logs dir not found)
  )
  echo.
  if exist "%STARTUP%" (
    echo Last startup marker:
    type "%STARTUP%"
  ) else (
    echo No startup marker found at %STARTUP%
  )
  echo.
) > "%OUT%"

if exist "%EXE%" (
  >> "%OUT%" echo Running "HISAB KITAB.exe --diag" ...
  "%EXE%" --diag >> "%OUT%" 2>&1
  >> "%OUT%" echo ExitCode: %ERRORLEVEL%
) else (
  >> "%OUT%" echo ERROR: EXE not found at: %EXE%
)

>> "%OUT%" echo.
>> "%OUT%" echo Recent crash logs:
for /f "delims=" %%F in ('dir /b /o:-d "%LOGDIR%\crash_*.log" 2^>nul') do (
  >> "%OUT%" echo - %%F
  goto :doneCrashList
)
:doneCrashList

echo.
echo Diagnostic log written to:
echo %OUT%
echo.
echo Opening log in Notepad...
start "" notepad "%OUT%"

exit /b 0

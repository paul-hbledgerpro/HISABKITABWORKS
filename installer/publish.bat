@echo off
setlocal

REM Publishes the WinForms app to installer\publish\win-x64 so Inno Setup can build Setup.exe.
REM Snapdragon/Windows-on-ARM runs this win-x64 build via x64 emulation.

if "%RUNTIME%"=="" set RUNTIME=win-x64
if /I not "%RUNTIME%"=="win-x64" (
  echo.
  echo NOTE: This project is Windows-only. For Snapdragon/Windows-on-ARM we still publish win-x64.
  echo Forcing RUNTIME=win-x64
  set RUNTIME=win-x64
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Runtime %RUNTIME%
if errorlevel 1 (
  echo.
  echo Publish FAILED. Fix the error above and run publish.bat again.
  pause
  exit /b 1
)

if not exist "%~dp0publish\%RUNTIME%\HISAB KITAB.exe" (
  echo.
  echo ERROR: Expected output exe not found: installer\publish\%RUNTIME%\HISAB KITAB.exe
  echo Make sure the UI project AssemblyName is "HISAB KITAB" and publish succeeded.
  pause
  exit /b 1
)

echo.
echo Publish OK. Now compile installer\InnoSetup\ManagerPaperworkSystem.iss in Inno Setup.
pause
endlocal

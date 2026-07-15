@echo off
setlocal

REM Windows-only publishing:
REM   - We publish ONLY win-x64 (Self-Contained)
REM   - Snapdragon/Windows-on-ARM runs this build via x64 emulation.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish_all.ps1"
if errorlevel 1 (
  echo.
  echo Publish_ALL FAILED. Fix the error above and run publish_all.bat again.
  pause
  exit /b 1
)

echo.
echo Publish_ALL OK. Now compile installer\InnoSetup\ManagerPaperworkSystem.iss in Inno Setup.
pause
endlocal

@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-workstation.ps1" %*
if errorlevel 1 (
  echo.
  echo Workstation setup failed. Review the message above.
  exit /b 1
)
endlocal


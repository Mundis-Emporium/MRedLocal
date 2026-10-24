@echo off
setlocal
powershell.exe -NoProfile -File "%~dp0scripts\Start-LocalTest.ps1"
set "result=%ERRORLEVEL%"
if not "%result%"=="0" (
    echo.
    echo Local test launcher stopped with error %result%.
    pause
)
endlocal & exit /b %result%

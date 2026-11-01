@echo off
setlocal
set "SCRIPT=%~dp0publish.ps1"

if not exist "%SCRIPT%" (
    echo publish.ps1 was not found next to this batch file.
    exit /b 1
)

where pwsh >nul 2>nul
if errorlevel 1 goto use_windows_powershell

pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RC=%ERRORLEVEL%"
endlocal & exit /b %RC%

:use_windows_powershell
where powershell >nul 2>nul
if errorlevel 1 goto no_shell

powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RC=%ERRORLEVEL%"
endlocal & exit /b %RC%

:no_shell
echo Neither pwsh nor Windows PowerShell was found.
exit /b 1

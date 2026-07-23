@echo off
setlocal
cd /d "%~dp0"
REM 右键/双击更稳：固定用 Windows PowerShell 5.1，并绕过执行策略
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" %*
set ERR=%ERRORLEVEL%
exit /b %ERR%
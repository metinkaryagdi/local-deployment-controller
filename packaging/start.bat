@echo off
title Local Deployment Controller
cd /d "%~dp0"
echo Local Deployment Controller baslatiliyor...
echo Panel: http://localhost:5000   (durdurmak icin Ctrl+C)
echo.
DeployController.exe
echo.
echo Sunucu durdu.
pause

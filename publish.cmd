@echo off
chcp 65001 >nul
title SmartBOQ Enterprise - Launcher & Environment Check
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ==================================================================
    echo Execution encountered an error. Press any key to exit...
    echo ==================================================================
    pause >nul
)

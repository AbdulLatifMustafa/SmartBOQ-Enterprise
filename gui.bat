@echo off
setlocal
echo ==================================================
echo   Starting SmartBOQ Enterprise Desktop (GUI)...
echo ==================================================
dotnet run --project "%~dp0src\SmartBOQ.App" %*

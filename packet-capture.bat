@echo off
setlocal EnableDelayedExpansion

:: ============================================================
::  packet-capture.bat
::  一键启动：服务端抓包模式 + PvfProxy + 游戏客户端
::
::  前置条件（需自行配置）：
::    1. PvfProxy.exe 放在脚本同级目录
::    2. 游戏客户端启动脚本在 CLIENT_DIR 中（StartGame.bat）
::    3. 服务端已编译（dotnet build）
:: ============================================================

set "ROOT=%~dp0"
set "CAPTURE_DIR=%ROOT%capture_logs"
if not exist "%CAPTURE_DIR%" mkdir "%CAPTURE_DIR%"

:: Clean stale processes and old capture logs
taskkill /f /im DNF.exe >nul 2>&1
taskkill /f /im PvfProxy.exe >nul 2>&1
taskkill /f /im DfoServer.exe >nul 2>&1
del /f /q "%CAPTURE_DIR%\packet_log.txt" 2>nul
timeout /t 1 /nobreak >nul

:: Start server in proxy mode (captures SEND/RECV packets)
start "" /min "%ROOT%Server\DfoServer\bin\Debug\DfoServer.exe" --server-ip "127.0.0.1" --packet-capture "%CAPTURE_DIR%" --proxy
timeout /t 2 /nobreak >nul

:: Start PvfProxy (forwards 7001/10011 → 7002/10012)
start "" /min /d "%CAPTURE_DIR%" "%ROOT%PvfProxy.exe"
timeout /t 2 /nobreak >nul

:: Start game client
:: ★ 修改 CLIENT_DIR 为你的游戏客户端路径
set "CLIENT_DIR=%ROOT%DXF"
cd /d "%CLIENT_DIR%"
start "" /wait "StartGame.bat"

:: Cleanup after client closes
taskkill /f /im DNF.exe >nul 2>&1
taskkill /f /im PvfProxy.exe >nul 2>&1
taskkill /f /im DfoServer.exe >nul 2>&1
echo Capture log saved: %CAPTURE_DIR%\packet_log.txt
exit /b 0

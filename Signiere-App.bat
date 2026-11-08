@echo off
chcp 65001 >nul
title SystemOptimierer - App signieren
echo ========================================================
echo  Signiere SystemOptimierer und gebe Dateien frei...
echo ========================================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Sign-Application.ps1"

echo.
echo ========================================================
echo  Vorgang beendet.
echo ========================================================
pause
@echo off
setlocal
title VictusFanControl - Diagnostico WMI y ACPI
echo Ejecuta este archivo como administrador para incluir las trazas ETW.
echo Espera "Observando" y luego usa la app en otra ventana.
echo Este recolector no abre la app. Q termina y genera el ZIP.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-Victus-WmiTimeout.ps1" -CaptureMinutes 15
echo.
pause

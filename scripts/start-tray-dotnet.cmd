@echo off
chcp 65001 >nul
rem Start PengchuanTray through the Microsoft-signed dotnet host.
rem This bypasses Smart App Control blocking of unsigned exe on Windows 11.
rem Place this script next to PengchuanTray.dll (framework-dependent build).
cd /d "%~dp0"

set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not exist "%DOTNET%" (
  for /f "delims=" %%i in ('where dotnet') do set "DOTNET=%%i"
)
if not exist "%DOTNET%" (
  echo dotnet not found. Please install .NET 10 Desktop Runtime first.
  pause
  exit /b 1
)

start "" "%DOTNET%" "%~dp0PengchuanTray.dll"
exit /b 0

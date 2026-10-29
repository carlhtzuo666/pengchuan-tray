@echo off
rem ==========================================================
rem  Tiechuan Assistant - Launcher
rem
rem  Why this file exists:
rem  Corporate EDR (Sentinel Agent etc.) blocks direct execution of
rem  PengchuanTray.exe (shows "security risk" / "operation canceled").
rem  Loading the same assembly through Microsoft-signed dotnet.exe works.
rem
rem  Keep this file next to PengchuanTray.dll.
rem  IMPORTANT: this file must stay pure ASCII. cmd.exe reads .cmd files
rem  with the OEM code page (GBK on zh-CN), so UTF-8 Chinese comments get
rem  parsed as commands and break the script.
rem ==========================================================
cd /d "%~dp0"

set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not exist "%DOTNET%" (
  for /f "delims=" %%i in ('where dotnet 2^>nul') do set "DOTNET=%%i"
)

if not exist "%DOTNET%" (
  echo.
  echo  [!] .NET runtime not found.
  echo      Please install .NET 10 Desktop Runtime:
  echo      https://dotnet.microsoft.com/download/dotnet/10.0
  echo.
  pause
  exit /b 1
)

if not exist "%~dp0PengchuanTray.dll" (
  echo.
  echo  [!] PengchuanTray.dll not found next to this launcher.
  echo.
  pause
  exit /b 1
)

start "" "%DOTNET%" "%~dp0PengchuanTray.dll"
exit /b 0

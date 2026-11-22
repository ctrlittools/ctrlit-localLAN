@echo off
setlocal
cd /d "%~dp0"

rem "build.bat ci" only compiles (used by the GitHub release workflow).
set "CI="
if /i "%~1"=="ci" set "CI=1"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo Could not find the .NET Framework 4 C# compiler.
  if not defined CI pause
  exit /b 1
)

if not defined CI taskkill /im ctrlit-lan.exe /f >nul 2>&1

rem The icon is used for the .exe file and also built in for the window.
rem oui.gz is the MAC address manufacturer list, built into the program.
set "ICON="
if exist ctrlit-lan.ico set "ICON=/win32icon:ctrlit-lan.ico /resource:ctrlit-lan.ico,ctrlit-lan.ico"

echo === build === > build.log
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:ctrlit-lan.exe %ICON% /resource:oui.gz,oui.gz /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll CtrlItLAN.cs >> build.log 2>&1
if errorlevel 1 (
  type build.log
  echo.
  echo Build failed - the details are saved in build.log.
  if not defined CI pause
  exit /b 1
)
echo === done === >> build.log

if defined CI (
  type build.log
  exit /b 0
)

echo Built ctrlit-lan.exe - starting it.
start "" "%~dp0ctrlit-lan.exe"
timeout /t 3 >nul

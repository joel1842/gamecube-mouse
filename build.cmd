@echo off
rem Builds dist\GameCubeMouse.exe and the installer dist\GameCubeMouse-Setup.exe
rem using the C# compiler that ships with Windows (.NET Framework 4). Nothing to install.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
for %%F in ("%CSC%") do set "FW=%%~dpF"
if not exist dist mkdir dist

"%CSC%" /nologo /target:winexe /optimize+ /out:dist\GameCubeMouse.exe /lib:"%FW%WPF" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll ^
  src\GameCubeMouse.cs
if errorlevel 1 goto failed

"%CSC%" /nologo /target:winexe /optimize+ /out:dist\GameCubeMouse-Setup.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /resource:dist\GameCubeMouse.exe,GameCubeMouse.exe installer\Setup.cs
if errorlevel 1 goto failed

echo Built dist\GameCubeMouse.exe and dist\GameCubeMouse-Setup.exe
exit /b 0

:failed
echo Build failed.
exit /b 1

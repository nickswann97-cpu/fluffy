@echo off
rem Builds Fluffy.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
cd /d "%~dp0"
if not exist Fluffy.ico powershell -NoProfile -ExecutionPolicy Bypass -File make_icon.ps1
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:Fluffy.exe /win32icon:Fluffy.ico /lib:"%FW%\WPF" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll /r:System.Core.dll ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  Fluffy.cs Recording.cs
if errorlevel 1 (echo Build failed & pause & exit /b 1)
echo Built Fluffy.exe

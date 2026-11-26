@echo off
rem Builds dist\KeyFlip.exe, or dist\KeyFlip.Tests.exe when called with "tests".
rem Uses the C# compiler that ships with Windows: no SDK and no packages.
setlocal
cd /d "%~dp0.."

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" goto nocompiler

set "CSC=%FW%\csc.exe"
set "OPTIONS=/nologo /warn:4 /codepage:65001 /optimize+ /debug- /platform:anycpu"
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll"

if not exist dist mkdir dist
if not exist obj mkdir obj

if /i "%~1"=="tests" goto tests

rem The icon is drawn by the same code the app uses for its tray icon.
"%CSC%" %OPTIONS% /target:exe /out:obj\MakeIcon.exe /r:System.dll /r:System.Drawing.dll tools\MakeIcon.cs src\KeyFlip\AppIcon.cs
if errorlevel 1 exit /b 1
obj\MakeIcon.exe obj\KeyFlip.ico
if errorlevel 1 exit /b 1
"%CSC%" %OPTIONS% /target:winexe /out:dist\KeyFlip.exe /main:KeyFlip.Program /win32manifest:res\app.manifest /win32icon:obj\KeyFlip.ico %REFS% /recurse:src\*.cs
if errorlevel 1 exit /b 1
echo Built dist\KeyFlip.exe
exit /b 0

:tests
"%CSC%" %OPTIONS% /target:exe /out:dist\KeyFlip.Tests.exe /main:KeyFlip.Tests.TestProgram %REFS% /recurse:src\*.cs /recurse:tests\*.cs
if errorlevel 1 exit /b 1
echo Built dist\KeyFlip.Tests.exe
exit /b 0

:nocompiler
echo The .NET Framework 4.8 C# compiler was not found under %WINDIR%\Microsoft.NET. 1>&2
exit /b 1

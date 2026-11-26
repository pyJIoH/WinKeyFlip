@echo off
rem Desktop checks of the Windows integration. They post no input, take no focus
rem and do not touch your clipboard. Add --with-layouts to also check conversion
rem with the installed English and Russian layouts.
setlocal
call "%~dp0build.cmd" tests
if errorlevel 1 exit /b 1
set "CHECKS=--platform"
if /i "%~1"=="--with-layouts" set "CHECKS=--platform --layouts"
"%~dp0..\dist\KeyFlip.Tests.exe" %CHECKS%
exit /b %ERRORLEVEL%

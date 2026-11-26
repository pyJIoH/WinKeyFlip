@echo off
rem Builds and runs the core scenarios. Exits non-zero when one fails.
rem Arguments are passed on, for example: scripts\test.cmd --e2e
setlocal
call "%~dp0build.cmd" tests
if errorlevel 1 exit /b 1
"%~dp0..\dist\KeyFlip.Tests.exe" %*
exit /b %ERRORLEVEL%

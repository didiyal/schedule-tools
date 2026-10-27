@echo off
rem ============================================================
rem  TodoTimer 1.4.0 installer build (WPF self-contained)
rem  Flow: dotnet publish (win-x64 self-contained) -> ISCC
rem  Output: tools/TodoTimer_Setup_1.4.0.exe
rem  NOTE: C:\Program Files (x86) has parens - never put %ISCC% checks inside if(..) blocks
rem ============================================================
setlocal
set "DOTNET_ROOT=D:\dotnet-sdk"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
cd /d "%~dp0.."
echo [1/3] Publishing WPF self-contained (win-x64)...
dotnet publish wpf\TodoTimer\TodoTimer.csproj -c Release -r win-x64 --self-contained true -o installer\publish
if errorlevel 1 goto :fail
echo [2/3] Compiling installer (Inno Setup)...
if not exist "%ISCC%" for /f "delims=" %%i in ('where iscc 2^>nul') do set "ISCC=%%i"
if not exist "%ISCC%" echo [ERROR] Inno Setup 6 not found: %ISCC%
if not exist "%ISCC%" goto :fail
pushd installer
"%ISCC%" installer.iss
if errorlevel 1 goto :fail_pop
popd
echo [3/3] Done. Installer: %~dp0..\TodoTimer_Setup_1.4.0.exe
endlocal
exit /b 0
:fail_pop
popd
:fail
echo BUILD FAILED.
endlocal
exit /b 1

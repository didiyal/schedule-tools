@echo off
rem ============================================================
rem  待办计时器 一键打包: 图标 -> PyInstaller EXE -> Inno Setup 安装器
rem  路径基于本 bat 所在目录(%~dp0), 不依赖运行时 cwd。
rem ============================================================
setlocal

set "ROOT=%~dp0.."
set "WS=%ROOT%\workspace"
set "ICON=%~dp0app.ico"
set "ISS=%~dp0installer.iss"
set "EXE=%ROOT%\待办计时器.exe"

echo ============================================================
echo  Todo Timer  build script
echo ============================================================

rem --- 1. (re)generate icon ---
echo [1/3] generating app.ico ...
pushd "%~dp0"
python make_icon.py || goto :fail_icon
popd

rem --- 2. PyInstaller: build EXE with embedded icon ---
echo [2/3] PyInstaller building EXE ...
pushd "%WS%"
pyinstaller --onefile --noconsole --name "待办计时器" ^
  --collect-all pystray --collect-all customtkinter ^
  --hidden-import six.moves ^
  --icon "%ICON%" ^
  --distpath "%ROOT%" --workpath "%ROOT%\build_tmp" main.py || goto :fail_py
del "%ROOT%\待办计时器.spec" 2>nul
rmdir /s /q "%ROOT%\build_tmp" 2>nul
popd

if not exist "%EXE%" goto :fail_py

rem --- 3. locate Inno Setup compiler ---
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  for /f "delims=" %%i in ('where iscc 2^>nul') do set "ISCC=%%i"
)
if not exist "%ISCC%" goto :no_iscc

echo [3/3] Inno Setup compiling installer ...
pushd "%~dp0"
"%ISCC%" "installer.iss" || goto :fail_iscc
popd

echo.
echo ============================================================
echo  DONE.  Installer:  %ROOT%\待办计时器_Setup_1.1.0.exe
echo ============================================================
endlocal
exit /b 0

:no_iscc
echo.
echo [ERROR] Inno Setup 6 not found.
echo   Install from https://jrsoftware.org/isdl.php  then re-run this script.
endlocal
exit /b 1

:fail_icon
popd 2>nul
echo [ERROR] icon generation failed.
endlocal
exit /b 1

:fail_py
popd 2>nul
echo [ERROR] PyInstaller build failed.
endlocal
exit /b 1

:fail_iscc
popd 2>nul
echo [ERROR] Inno Setup compile failed.
endlocal
exit /b 1

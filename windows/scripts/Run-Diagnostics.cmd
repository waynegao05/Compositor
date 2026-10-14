@echo off
setlocal
set "COMPOSITOR_CHECK_DIR=%LOCALAPPDATA%\Compositor\Checks\diagnostics-%RANDOM%-%RANDOM%"
mkdir "%COMPOSITOR_CHECK_DIR%"
if errorlevel 1 goto failed
start "" /b /wait "%~dp0Compositor.Desktop.exe" --diagnostics "%COMPOSITOR_CHECK_DIR%\diagnostics.zip" > "%COMPOSITOR_CHECK_DIR%\export.log" 2>&1
if errorlevel 1 goto failed
if not exist "%COMPOSITOR_CHECK_DIR%\diagnostics.zip" goto failed
echo Diagnostics saved to:
echo "%COMPOSITOR_CHECK_DIR%"
echo Nothing has been uploaded. Review the archive before sharing it.
start "" explorer.exe "%COMPOSITOR_CHECK_DIR%"
pause
exit /b 0
:failed
echo Diagnostics export failed. Check the application's files and export.log if available.
echo "%COMPOSITOR_CHECK_DIR%"
pause
exit /b 1

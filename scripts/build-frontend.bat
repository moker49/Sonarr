@echo off
setlocal
cd /d "%~dp0.."
if errorlevel 1 goto failed
set "SKIP_INSTALL=0"
set "SKIP_PAUSE=0"
for %%A in (%*) do (
    if /i "%%~A"=="--no-install" set "SKIP_INSTALL=1"
    if /i "%%~A"=="--no-pause" set "SKIP_PAUSE=1"
)
if not exist "%NVM_HOME%\v24.14.0\node.exe" (
    echo Node 24 not found at %NVM_HOME%\v24.14.0.
    goto failed
)
set "PATH=%NVM_HOME%\v24.14.0;%PATH%"
if "%SKIP_INSTALL%"=="1" goto build
echo Installing frontend dependencies...
call corepack yarn install --frozen-lockfile
if errorlevel 1 goto failed
:build
if not exist "node_modules\.bin\webpack.cmd" (
    echo Frontend dependencies are missing. Run without --no-install first.
    goto failed
)
echo Building frontend...
call node_modules\.bin\webpack.cmd --config frontend\build\webpack.config.js
if errorlevel 1 goto failed
echo Frontend build succeeded.
if "%SKIP_PAUSE%"=="0" pause
exit /b 0

:failed
echo Frontend build failed.
if "%SKIP_PAUSE%"=="0" pause
exit /b 1

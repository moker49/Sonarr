@echo off
setlocal
cd /d "%~dp0.."
if errorlevel 1 goto failed

rem Use the development tools installed for this checkout without changing system defaults.
set "PATH=%NVM_HOME%\v24.14.0;%PATH%"
if exist "%LOCALAPPDATA%\SonarrDev\dotnet\dotnet.exe" (
    set "PATH=%LOCALAPPDATA%\SonarrDev\dotnet;%PATH%"
    set "DOTNET_ROOT=%LOCALAPPDATA%\SonarrDev\dotnet"
)
if not exist "%NVM_HOME%\v24.14.0\node.exe" goto missing_tools
where dotnet >nul 2>&1
if errorlevel 1 goto missing_tools

if not exist "_output\dev\Sonarr.Console.exe" (
    echo Backend build not found. Run scripts\build-backend.bat first.
    goto failed
)
if not exist "_output\dev\Sonarr.Windows.dll" (
    echo Windows backend components not found. Run scripts\build-backend.bat first.
    goto failed
)
if not exist "_output\UI\index.html" (
    echo Frontend build not found. Run scripts\build-frontend.bat first.
    goto failed
)
if not exist "node_modules\.bin\webpack.cmd" (
    echo Frontend dependencies not found. Run scripts\build-frontend.bat first.
    goto failed
)
powershell -NoProfile -Command "$c = New-Object System.Net.Sockets.TcpClient; try { $c.Connect('localhost', 8989); exit 1 } catch { exit 0 } finally { $c.Dispose() }"
if errorlevel 1 (
    echo Port 8989 is already in use. Stop the other application before launching Sonarr.
    goto failed
)

echo Starting frontend watcher and Sonarr...
start "Sonarr frontend watcher" cmd /k "node_modules\.bin\webpack.cmd --watch --config frontend\build\webpack.config.js"
start "Sonarr development" /D "%CD%\_output\dev" cmd /k ""%CD%\_output\dev\Sonarr.Console.exe" "/data=%CD%\_devdata" /nobrowser"
echo Waiting for Sonarr on http://localhost:8989 ...
powershell -NoProfile -Command "$ready = $false; for ($i = 0; $i -lt 60; $i++) { try { $r = Invoke-WebRequest 'http://localhost:8989' -UseBasicParsing -TimeoutSec 2; if ($r.StatusCode -eq 200) { $ready = $true; break } } catch {}; Start-Sleep -Seconds 1 }; if ($ready) { Start-Process 'http://localhost:8989' } else { Write-Host 'Sonarr did not respond. Check the Sonarr development window and _devdata\logs\sonarr.txt.'; exit 1 }"
if errorlevel 1 goto failed
echo Sonarr is ready. Close the Sonarr and frontend watcher windows to stop it.
exit /b 0

:missing_tools
echo Required development tools were not found. Check Node 24 and the .NET 6 SDK/runtime installations.
goto failed

:failed
echo Startup failed. Review the messages above.
pause
exit /b 1

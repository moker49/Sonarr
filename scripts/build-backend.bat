@echo off
setlocal
cd /d "%~dp0.."
if errorlevel 1 goto failed
rem Prefer an optional checkout SDK, otherwise use the installed .NET SDK.
if exist "%LOCALAPPDATA%\SonarrDev\dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%LOCALAPPDATA%\SonarrDev\dotnet"
    set "PATH=%LOCALAPPDATA%\SonarrDev\dotnet;%PATH%"
)
where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET SDK not found. Install the .NET 6 SDK required by global.json.
    goto failed
)

echo Building Sonarr backend...
rem Keep package audit findings visible without blocking this local development build.
rem Double percent escapes the MSBuild list separator for batch-file expansion.
rem Keep the dynamically loaded platform project's older dependencies away from
rem the console host. Only its own assembly belongs in the host output.
dotnet build src\NzbDrone.Windows\Sonarr.Windows.csproj -c Debug -p:Platform=Windows -p:RuntimeIdentifiers=win-x64 -p:RunAnalyzers=false "-p:WarningsNotAsErrors=NU1902%%3BNU1903" "-p:OutDir=%CD%\_temp\dev-windows/" "-p:SolutionDir=%CD%\src/"
if errorlevel 1 goto failed
dotnet build src\NzbDrone.Console\Sonarr.Console.csproj -c Debug -p:Platform=Windows -p:RuntimeIdentifiers=win-x64 -p:RunAnalyzers=false "-p:WarningsNotAsErrors=NU1902%%3BNU1903" "-p:OutDir=%CD%\_output\dev/" "-p:SolutionDir=%CD%\src/"
if errorlevel 1 goto failed
copy /y "_temp\dev-windows\Sonarr.Windows.dll" "_output\dev\Sonarr.Windows.dll" >nul
if errorlevel 1 goto failed
if exist "_temp\dev-windows\Sonarr.Windows.pdb" copy /y "_temp\dev-windows\Sonarr.Windows.pdb" "_output\dev\Sonarr.Windows.pdb" >nul
echo Backend build succeeded.
if /i not "%~1"=="--no-pause" pause
exit /b 0

:failed
echo Backend build failed.
if /i not "%~1"=="--no-pause" pause
exit /b 1

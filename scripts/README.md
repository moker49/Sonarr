# Sonarr convenience scripts

Run these from any directory, or double-click the batch files. They resolve the
checkout from their own location.

| Script | Purpose |
| --- | --- |
| `build-backend.bat` | Build the Debug console host and Windows platform assembly into `_output/dev/` |
| `build-frontend.bat` | Install locked dependencies and build the UI into `_output/UI/` |
| `start-sonarr-dev.bat` | Start Sonarr and the frontend watcher, then open port 8989 in the browser |
| `build-container.bat` | Build and export the Linux amd64 deployment bundle |
| `build-container-to-server.bat` | Build and transfer the bundle to `\\server\Server\docker\sonarr49` |
| `run-container.bat` | Run on the destination alongside the exported archive, Compose file, and `.env` |
| `run-container-build.ps1` | Shared build launcher with timestamped logs |

## Local development

The backend uses the .NET SDK selected by `global.json`. An optional SDK installation
at `%LOCALAPPDATA%\SonarrDev\dotnet` takes precedence over `dotnet` on PATH. The
frontend and watcher use `%NVM_HOME%\v24.14.0`, matching the Radarr helpers. Dependency
installation uses Corepack and Yarn. Install the .NET 6 runtime to run the built host.

```powershell
.\scripts\build-backend.bat
.\scripts\build-frontend.bat
.\scripts\start-sonarr-dev.bat
```

The backend helper skips analyzers for local builds and leaves package advisory
warnings visible. Build helpers pause at the end; pass `--no-pause` for automation.
The frontend helper accepts `--no-install` to reuse installed dependencies:

```powershell
.\scripts\build-frontend.bat --no-install --no-pause
```

Development uses `_devdata/`, which is ignored by Git and separate from installed
Sonarr and Radarr data. The launcher requires port 8989 to be free. Close the Sonarr
and watcher windows to stop them. It does not configure clients or import a library.

## Containers

```powershell
.\scripts\build-container.bat
.\scripts\build-container-to-server.bat
```

See [CONTAINER.md](../CONTAINER.md) for build/export options and
[CONTAINER_DEPLOY.md](../CONTAINER_DEPLOY.md) for destination setup. The server
destination defaults to `\\server\Server\docker\sonarr49`. Pass `-Destination` to
override it; no Radarr deployment directory is reused.

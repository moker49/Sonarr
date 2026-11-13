# Build and export the Sonarr image

This checkout builds a Linux amd64 image and a deployment bundle. The build and
transfer scripts do not launch Sonarr or recreate a running container.

From this checkout, with Docker Desktop's Linux engine running:

```powershell
.\scripts\build-container.bat
```

To build and transfer the bundle to `\\server\Server\docker\sonarr49`:

```powershell
.\scripts\build-container-to-server.bat
```

The server helper checks destination access first, stages and verifies the files,
publishes them, then removes the transferred local copies. It preserves an existing
server `.env` and retains local outputs on failure. Both batch launchers keep their
windows open and save timestamped logs in `_artifacts/build-logs/`.

Pass `-Destination` to override the server directory. Other optional arguments include
`-ImageTag` and `-DockerContext`. Local export also accepts
`-OutputDirectory`. For example:

```powershell
.\build-container.ps1 -DockerContext my-builder -ImageTag sonarr-composite:test
```

The Dockerfile builds the production frontend with Node 24 and the lockfile, then
publishes Sonarr and its POSIX assembly for .NET 6. The image copies the .NET 6
runtime onto Debian Bookworm runtime dependencies and includes ffprobe, SQLite,
and non-root UID 1654. In-app updates are disabled.
The Docker context allowlist excludes dev data, API keys, dependencies, Git metadata,
and local build outputs.

The exported `_artifacts/container/` directory contains:

| File | Purpose |
| --- | --- |
| `sonarr-composite-linux-amd64.tar` | Image archive |
| `sonarr-composite-linux-amd64.tar.sha256` | Archive checksum |
| `compose.yaml` | Destination configuration with no build step |
| `.env` | Image tag, folders, host port, and timezone |
| `README.md` | Destination instructions |
| `run-container.bat` | Verify, load, and start/recreate the container |

Review `.env` on the destination before launching it. Defaults are port `8989`,
`downloads=C:\Complete`, and `media_root=F:\Media\TV`. Existing output and server
`.env` files are preserved, so update `SONARR_IMAGE` yourself when changing image tags.

See [CONTAINER_DEPLOY.md](CONTAINER_DEPLOY.md) for destination setup and smoke checks.
The scripts do not constitute validation of Sonarr's composite workflows.

## Validation

The Windows backend and frontend helpers passed on November 13, 2026. A real Linux
amd64 image was built and exported with the tag `sonarr-composite:script-check` to
`_artifacts/container-validation/`. Offline runtime checks verified .NET 6.0.36,
ffprobe, UID 1654, and the host, POSIX assembly, UI, and localization files. Sonarr
was not launched and no server deployment was changed.

The export/transfer orchestration was checked under Windows PowerShell with a
Docker test double, including context forwarding, checksums, preserving local and
server `.env` files, and stopping after build or engine failures. Build logs are
in `_artifacts/build-logs/`.

# syntax=docker/dockerfile:1
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:6.0-bullseye-slim@sha256:fc71510497ce2ec3575359068b9c7b1b9f449cfdb0371b5c71a939963a2fedfd

FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS frontend
WORKDIR /src
COPY package.json yarn.lock .yarnrc ./
RUN yarn install --frozen-lockfile --network-timeout 120000
COPY tsconfig.json ./
COPY frontend/ frontend/
ENV NODE_OPTIONS=--max-old-space-size=4096
RUN yarn build --env production

FROM --platform=$BUILDPLATFORM ${DOTNET_SDK_IMAGE} AS backend
ARG TARGETARCH
WORKDIR /src
COPY global.json .editorconfig ./
COPY src/ src/
COPY Logo/64.png Logo/64.png
# Publish the dynamically loaded POSIX platform assembly as well as the host.
# Keep the existing advisory warnings visible without making restore fail.
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac; \
    dotnet publish src/NzbDrone.Mono/Sonarr.Mono.csproj \
      -c Release -f net6.0 -r "$rid" --self-contained false -o /publish \
      -p:Platform=Posix -p:RuntimeIdentifiers="$rid" -p:SolutionDir=/src/src/ \
      '-p:WarningsNotAsErrors=NU1902%3BNU1903' -p:RunAnalyzers=false -p:SentryUploadSymbols=false \
    && dotnet publish src/NzbDrone.Console/Sonarr.Console.csproj \
      -c Release -f net6.0 -r "$rid" --self-contained false -o /publish \
      -p:Platform=Posix -p:RuntimeIdentifiers="$rid" -p:SolutionDir=/src/src/ \
      '-p:WarningsNotAsErrors=NU1902%3BNU1903' -p:RunAnalyzers=false -p:SentryUploadSymbols=false \
    && test -f /publish/Sonarr.dll \
    && test -f /publish/Sonarr.Mono.dll

FROM mcr.microsoft.com/dotnet/aspnet:6.0-bullseye-slim@sha256:aea501c911bdc8babe50f55f5aca89bcdd59fd89e72063d08c2ce29af1d76024 AS dotnet-runtime

# Keep Sonarr's .NET 6 runtime while using the current Debian runtime dependencies.
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-bookworm-slim AS runtime
COPY --from=dotnet-runtime /usr/share/dotnet/ /usr/share/dotnet/
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ffmpeg libsqlite3-0 tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && mkdir -p /config /app/bin \
    && chown app:app /config
WORKDIR /app/bin
COPY --from=backend /publish/ ./
COPY --from=frontend /src/_output/UI/ ./UI/
COPY LICENSE.md /app/LICENSE.md
# Sonarr searches its application directory for ffprobe.
RUN ln -sf /usr/bin/ffprobe /app/bin/ffprobe \
    && printf '%s\n' \
      'PackageAuthor=Sonarr composite-profile fork' \
      'UpdateMethod=Docker' \
      'UpdateMethodMessage=Rebuild and recreate this container to update the fork.' \
      > /app/package_info
ENV DOTNET_ROOT=/usr/share/dotnet \
    Sonarr__Server__BindAddress=* \
    Sonarr__Server__Port=8989 \
    Sonarr__Server__EnableSsl=false \
    Sonarr__Server__UrlBase="" \
    Sonarr__Update__Mechanism=Docker \
    Sonarr__Update__Automatically=false \
    HOME=/config
USER app
VOLUME ["/config"]
EXPOSE 8989
HEALTHCHECK --interval=30s --timeout=5s --start-period=90s --retries=3 \
    CMD curl --fail --silent --show-error "http://127.0.0.1:8989${Sonarr__Server__UrlBase}/ping" || exit 1
ENTRYPOINT ["dotnet", "/app/bin/Sonarr.dll", "/nobrowser", "/data=/config"]

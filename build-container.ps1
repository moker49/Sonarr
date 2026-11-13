[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9./:_-]*$')]
    [string]$ImageTag = 'sonarr-composite:local',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '_artifacts/container'),
    [string]$DockerContext
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker CLI is required to build/export the image. Install Docker Desktop for building, or configure Docker CLI access to a remote Linux engine. This script does not run Sonarr.'
}

$dockerOptions = @()
if ($DockerContext) {
    $dockerOptions = @('--context', $DockerContext)
}

function Invoke-Docker {
    param([string[]]$DockerArguments)
    $nativeErrorPreference = $ErrorActionPreference
    try {
        # Docker progress is on stderr; send it through the host so transcripts
        # capture it under Windows PowerShell as well as PowerShell 7.
        $ErrorActionPreference = 'Continue'
        & docker @dockerOptions @DockerArguments 2>&1 | ForEach-Object {
            $line = if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { $_.ToString() }
            Write-Host $line
        }
        $dockerExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $nativeErrorPreference
    }
    if ($dockerExitCode -ne 0) {
        throw "Docker failed ($dockerExitCode): $($DockerArguments -join ' ')"
    }
}

$probeErrorPreference = $ErrorActionPreference
try {
    # Capture diagnostics even when Docker returns an empty OS and a zero exit code.
    # Windows PowerShell represents native stderr as ErrorRecord objects.
    $ErrorActionPreference = 'Continue'
    $engineResponse = @(& docker @dockerOptions info --format '{{.OSType}}' 2>&1)
    $engineExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $probeErrorPreference
}
$engineOs = ($engineResponse | ForEach-Object { $_.ToString() }) -join "`n"
$engineOs = $engineOs.Trim()
if ($engineExitCode -eq 0 -and $engineOs -eq 'windows') {
    throw 'The Docker builder must use Linux containers.'
}
if ($engineExitCode -ne 0 -or $engineOs -ne 'linux') {
    throw "Docker's Linux engine is unavailable (exit code $engineExitCode). Start or restart Docker Desktop and wait for the engine to be ready. Docker response: $engineOs"
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archive = Join-Path $output 'sonarr-composite-linux-amd64.tar'

Write-Host 'Building the Linux amd64 image. Sonarr will not be launched on this machine.'
Invoke-Docker -DockerArguments @('build', '--platform', 'linux/amd64', '--tag', $ImageTag, '--progress', 'plain', $PSScriptRoot)
Invoke-Docker -DockerArguments @('image', 'save', '--output', $archive, $ImageTag)

$sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $output 'sonarr-composite-linux-amd64.tar.sha256') -Value "$sha256  sonarr-composite-linux-amd64.tar" -Encoding Ascii
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'compose.deploy.yaml') -Destination (Join-Path $output 'compose.yaml') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CONTAINER_DEPLOY.md') -Destination (Join-Path $output 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scripts/run-container.bat') -Destination (Join-Path $output 'run-container.bat') -Force
$envPath = Join-Path $output '.env'
if (-not (Test-Path -LiteralPath $envPath)) {
    $defaults = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'container.defaults.env') -Raw
    Set-Content -LiteralPath $envPath -Value ("SONARR_IMAGE=$ImageTag`n" + $defaults) -Encoding Ascii
}

Write-Host "Export complete: $output"
Write-Host 'Copy that directory to the destination Docker host and follow README.md.'

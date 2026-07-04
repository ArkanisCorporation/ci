$ErrorActionPreference = 'Stop'

if (-not $env:DOCKER_HOST -and $env:OS -eq 'Windows_NT') {
    $env:DOCKER_HOST = 'npipe:////./pipe/dockerDesktopLinuxEngine'
}

& act @args
exit $LASTEXITCODE

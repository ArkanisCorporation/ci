Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$generator = Join-Path $scriptDirectory 'generate-docs.cs'
$dotnetArgs = @('run', '--file', $generator, '--') + $args

& dotnet @dotnetArgs
exit $LASTEXITCODE

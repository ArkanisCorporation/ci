Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $repoRoot '.github/workflows/wf-node-lint.yml'
$original = Get-Content -Raw -LiteralPath $workflowPath

try {
    $mutated = $original.Replace("        default: pnpm", "        default: npm")
    if ($mutated -eq $original) {
        throw "Could not mutate wf-node-lint package-manager default."
    }

    Set-Content -LiteralPath $workflowPath -Value $mutated -NoNewline

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & dotnet run --file (Join-Path $repoRoot 'scripts/validate-workflows.cs') *> $null
        $validatorExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($validatorExitCode -eq 0) {
        throw "Validator accepted a workflow input default that disagrees with its schema."
    }
}
finally {
    Set-Content -LiteralPath $workflowPath -Value $original -NoNewline
}

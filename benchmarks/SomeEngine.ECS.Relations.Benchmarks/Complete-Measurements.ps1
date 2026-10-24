param([Parameter(Mandatory)][string]$Series)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$reportDirectory = Join-Path $repositoryRoot "artifacts/ecs-relations/measurements/$Series"
$stages = [Collections.Generic.List[object]]::new()
foreach ($script in @('Collect-Measurements.ps1', 'Export-Evidence.ps1', 'Write-MeasurementReport.ps1')) {
    [string[]]$arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot $script), '-Series', $Series)
    $started = [DateTime]::UtcNow
    & pwsh @arguments
    $code = $LASTEXITCODE
    $stage = [pscustomobject]@{
        Script = $script; Executable = 'pwsh'; Arguments = $arguments
        StartedAtUtc = $started; FinishedAtUtc = [DateTime]::UtcNow; ExitCode = $code
    }
    $stages.Add($stage)
    $stages | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportDirectory 'postprocess-stages.json') -Encoding utf8
    Write-Host ($stage | ConvertTo-Json -Depth 6 -Compress)
    if ($code -ne 0) { exit $code }
}
$evidence = Join-Path $repositoryRoot "docs/evidence/ECS-Relations-2026-10-19/$Series"
Copy-Item -LiteralPath (Join-Path $reportDirectory 'postprocess-stages.json') -Destination (Join-Path $evidence 'postprocess-stages.json') -Force

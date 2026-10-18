param(
    [Parameter(Mandatory)][string]$Series,
    [string]$Destination = 'docs/evidence/ECS-Relations-2026-10-19'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$reports = Join-Path $repositoryRoot "artifacts/ecs-relations/measurements/$Series"
$evidence = Join-Path (Join-Path $repositoryRoot $Destination) $Series
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$submissions = Get-Content -Raw -LiteralPath (Join-Path $reports 'submissions.json') | ConvertFrom-Json
$index = foreach ($submission in $submissions) {
    $report = Get-Content -Raw -LiteralPath $submission.Report | ConvertFrom-Json
    $queueResult = Get-Content -Raw -LiteralPath (Join-Path $submission.QueueDirectory 'result.json') | ConvertFrom-Json
    $reportFile = "$($submission.CaseId).json.gz"
    $source = [IO.File]::OpenRead($submission.Report)
    $target = [IO.File]::Create((Join-Path $evidence $reportFile))
    $gzip = [IO.Compression.GZipStream]::new($target, [IO.Compression.CompressionLevel]::SmallestSize)
    try { $source.CopyTo($gzip) }
    finally { $gzip.Dispose(); $target.Dispose(); $source.Dispose() }
    [pscustomobject]@{
        CaseId = $submission.CaseId
        Report = $reportFile
        SourceCommit = $submission.SourceCommit
        Backend = $submission.Backend
        Equal = $report.Equivalence.Equal
        Passed = $report.Passed
        Comparisons = $report.Comparisons
        Device = $report.Device
        RelationMemory = $report.RelationMemory
        HandwrittenMemory = $report.HandwrittenMemory
        Queue = $queueResult
        Stdout = [IO.File]::ReadAllText((Join-Path $submission.QueueDirectory 'stdout.log'))
        Stderr = [IO.File]::ReadAllText((Join-Path $submission.QueueDirectory 'stderr.log'))
    }
}
$index | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $evidence 'index.json') -Encoding utf8
Write-Host "$Series`: $($index.Count) reports; $evidence"

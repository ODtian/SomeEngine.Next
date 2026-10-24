param([string]$Series = 'initial')
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$reportDirectory = Join-Path $repositoryRoot "artifacts/ecs-relations/measurements/$Series"
$submissions = Get-Content -Raw -LiteralPath (Join-Path $reportDirectory 'submissions.json') | ConvertFrom-Json
$summary = foreach ($submission in $submissions) {
    $reportId = [string]@($submission.CaseId)[0]
    $resultFile = Join-Path $submission.QueueDirectory 'result.json'
    if (!(Test-Path -LiteralPath $resultFile)) {
        [pscustomobject]@{ CaseId = $reportId; Status = 'queued'; QueueId = $submission.QueueId }
        continue
    }
    $result = Get-Content -Raw -LiteralPath $resultFile | ConvertFrom-Json
    if ($result.ExitCode -ne 0) {
        [pscustomobject]@{ CaseId = $reportId; Status = 'execution-failed'; QueueId = $submission.QueueId; ExitCode = $result.ExitCode }
        continue
    }
    $report = Get-Content -Raw -LiteralPath $submission.Report | ConvertFrom-Json
    foreach ($comparison in $report.Comparisons) {
        [pscustomobject]@{ CaseId = $reportId; Boundary = $comparison.Boundary;
            Required = $comparison.Required; Status = if ($report.ReportKind -eq 'HandwrittenControl') { 'diagnostic' }
                elseif ($comparison.Passed) { 'pass' } else { 'measured-overhead' };
            MedianRatio = $comparison.MedianRatio; P95Ratio = $comparison.P95Ratio;
            SourceCommit = $submission.SourceCommit; Backend = $submission.Backend;
            QueueId = $submission.QueueId; QueueResult = $resultFile; Report = $submission.Report }
    }
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportDirectory 'summary.json') -Encoding utf8
$summary | Format-Table CaseId, Boundary, Required, MedianRatio, P95Ratio, Status, QueueId -AutoSize

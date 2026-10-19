param(
    [Parameter(Mandatory)][string]$Series,
    [Parameter(Mandatory)][string]$PreparationQueueId
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$reports = Join-Path $repositoryRoot "artifacts/ecs-relations/measurements/$Series"
$destination = Join-Path $repositoryRoot "docs/evidence/ECS-Relations-2026-10-19/$Series/queue-boundaries.json"
$preparation = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "artifacts/benchmark-queue/$PreparationQueueId/result.json") | ConvertFrom-Json
$submission = Get-Content -Raw -LiteralPath (Join-Path $reports 'queue-submission.json') | ConvertFrom-Json
$cases = @(Get-Content -Raw -LiteralPath (Join-Path $reports 'submissions.json') | ConvertFrom-Json | ForEach-Object {
    $record = Get-Content -Raw -LiteralPath (Join-Path $_.QueueDirectory 'result.json') | ConvertFrom-Json
    [pscustomobject]@{ CaseId = [string]@($_.CaseId)[0]; SourceCommit = $_.SourceCommit; Queue = $record }
})
$analyses = @($submission.Reports | ForEach-Object {
    Get-Content -Raw -LiteralPath (Join-Path $_.QueueDirectory 'result.json') | ConvertFrom-Json
})
$lastSubmission = @($cases.Queue | ForEach-Object { [DateTimeOffset]$_.SubmittedAtUtc } | Sort-Object)[-1]
$firstStart = @($cases.Queue | ForEach-Object { [DateTimeOffset]$_.StartedAtUtc } | Sort-Object)[0]
$lastFinish = @($cases.Queue | ForEach-Object { [DateTimeOffset]$_.FinishedAtUtc } | Sort-Object)[-1]
$firstAnalysis = @($analyses | ForEach-Object { [DateTimeOffset]$_.StartedAtUtc } | Sort-Object)[0]
$orderedCases = @($cases | Sort-Object { [DateTimeOffset]$_.Queue.StartedAtUtc })
$executionSeconds = 0.0
$betweenCases = for ($index = 0; $index -lt $orderedCases.Count; $index++) {
    $current = $orderedCases[$index]
    $executionSeconds += ([DateTimeOffset]$current.Queue.FinishedAtUtc - [DateTimeOffset]$current.Queue.StartedAtUtc).TotalSeconds
    if ($index -gt 0) {
        $previous = $orderedCases[$index - 1]
        [pscustomobject]@{
            BeforeCaseId = $previous.CaseId; AfterCaseId = $current.CaseId
            StartedAtUtc = [DateTimeOffset]$previous.Queue.FinishedAtUtc
            FinishedAtUtc = [DateTimeOffset]$current.Queue.StartedAtUtc
            Seconds = ([DateTimeOffset]$current.Queue.StartedAtUtc - [DateTimeOffset]$previous.Queue.FinishedAtUtc).TotalSeconds
        }
    }
}
$observationPath = Join-Path (Split-Path -Parent $destination) 'protected-observation.json'
$observation = if (Test-Path -LiteralPath $observationPath) {
    $reading = Get-Content -Raw -LiteralPath $observationPath | ConvertFrom-Json
    $readStart = [DateTimeOffset]$reading.ObservationStartedAtUtc
    $readFinish = [DateTimeOffset]$reading.ObservationFinishedAtUtc
    [pscustomobject]@{
        StartedAtUtc = $readStart; FinishedAtUtc = $readFinish
        Seconds = ($readFinish - $readStart).TotalSeconds
        OverlappingCases = @($cases | Where-Object {
            [DateTimeOffset]$_.Queue.StartedAtUtc -le $readFinish -and [DateTimeOffset]$_.Queue.FinishedAtUtc -ge $readStart
        } | ForEach-Object {
            [pscustomobject]@{
                CaseId = $_.CaseId; QueueId = $_.Queue.Id
                StartedAtUtc = $_.Queue.StartedAtUtc; FinishedAtUtc = $_.Queue.FinishedAtUtc
            }
        })
    }
}
$summary = [pscustomobject]@{
    Series = $Series; SourceCommit = $submission.SourceCommit; Backend = $submission.Backend
    CaseCount = $cases.Count; SessionIds = @($cases.Queue.SessionId | Select-Object -Unique)
    PreparationExitCode = $preparation.ExitCode
    CaseExitCodes = @($cases.Queue.ExitCode | Select-Object -Unique)
    AnalysisExitCodes = @($analyses.ExitCode | Select-Object -Unique)
    LastCaseSubmittedAtUtc = $lastSubmission; FirstCaseStartedAtUtc = $firstStart
    PreparationFinishedAtUtc = [DateTimeOffset]$preparation.FinishedAtUtc
    LastCaseFinishedAtUtc = $lastFinish; FirstAnalysisStartedAtUtc = $firstAnalysis
    AllCasesSubmittedBeforeExecution = $lastSubmission -le $firstStart
    PreparationFinishedBeforeExecution = [DateTimeOffset]$preparation.FinishedAtUtc -le $firstStart
    AnalysisStartedAfterExecution = $firstAnalysis -ge $lastFinish
    MeasurementSeconds = ($lastFinish - $firstStart).TotalSeconds
    CaseExecutionSeconds = $executionSeconds
    BetweenCasesSeconds = ($betweenCases.Seconds | Measure-Object -Sum).Sum
    LongestBetweenCasesSeconds = ($betweenCases.Seconds | Measure-Object -Maximum).Maximum
    PreparationWaitingSeconds = ([DateTimeOffset]$preparation.StartedAtUtc - [DateTimeOffset]$preparation.SubmittedAtUtc).TotalSeconds
}
[pscustomobject]@{ Summary = $summary; Preparation = $preparation; Cases = $cases; Analyses = $analyses
    BetweenCases = @($betweenCases); ProtectedObservation = $observation } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $destination -Encoding utf8
$summary | ConvertTo-Json -Depth 6

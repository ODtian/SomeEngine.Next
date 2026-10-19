param(
    [string]$Series = 'initial',
    [string[]]$CaseId = @(),
    [ValidateSet('NativeAOT', 'JIT')][string]$Backend = 'NativeAOT',
    [ValidateSet('measure', 'phases')][string]$Mode = 'measure',
    [ValidateSet('Normal', 'Fixed')][string[]]$Machine = @('Fixed'),
    [string]$QueueScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../tools/Invoke-Benchmark.ps1')),
    [string]$Session = $env:CODEX_THREAD_ID,
    [switch]$CollectReports,
    [string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
$queueTool = [IO.Path]::GetFullPath($QueueScript)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$SourceCommit) { $SourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim() }
$reportDirectory = Join-Path $repositoryRoot "artifacts/ecs-relations/measurements/$Series"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$executable = Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/native-publish/SomeEngine.ECS.Relations.Benchmarks.exe'
$managed = Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/SomeEngine.ECS.Relations.Benchmarks.dll'
[string[]]$prefix = if ($Backend -eq 'NativeAOT') { @($executable) } else { @('dotnet', $managed) }
$previousDirectory = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    $submissionStarted = [DateTime]::UtcNow
    [string[]]$listingArguments = @($prefix | Select-Object -Skip 1) + @('--mode', 'list', '--source', $sourceCommit)
    Write-Host "LIST $($prefix[0]) $($listingArguments -join ' ')"
    $listing = & $prefix[0] @listingArguments
    if ($LASTEXITCODE -ne 0) { throw 'Case listing failed.' }
    $cases = ($listing -join [Environment]::NewLine) | ConvertFrom-Json
    $selected = $cases | Where-Object { ($CaseId.Count -eq 0 -or $_.Id -in $CaseId) -and ($Mode -ne 'phases' -or $_.ChangesEveryFrame) }
    $caseIndex = 0
    $submissions = foreach ($case in $selected) {
        $machineOrder = @($Machine)
        if ($caseIndex % 2 -eq 1) { [array]::Reverse($machineOrder) }
        $caseIndex++
        foreach ($configuration in $machineOrder) {
            $caseId = if ($Machine.Count -gt 1) { "$($case.Id)-$($configuration.ToLowerInvariant())" } else { $case.Id }
            $report = Join-Path $reportDirectory "$caseId.json"
            [string[]]$arguments = @('--session', $Session) + $prefix + @('--mode', $Mode, '--case', $case.Class, '--size', $case.Size,
                '--variant', $case.Variant, '--source', $sourceCommit, '--backend', $Backend, '--machine', $configuration, '--output', $report)
            $submitted = & $queueTool @arguments | ConvertFrom-Json
            Write-Host "QUEUED $($submitted.Id) $caseId"
            [pscustomobject]@{ CaseId = $caseId; InputCaseId = $case.Id; Machine = $configuration; SourceCommit = $sourceCommit; Backend = $Backend;
                Report = $report; QueueId = $submitted.Id; QueueDirectory = $submitted.OutputDirectory }
        }
    }
    $submissions | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportDirectory 'submissions.json') -Encoding utf8
    $reports = if ($CollectReports) {
        foreach ($script in @('Collect-Measurements.ps1', 'Export-Evidence.ps1', 'Write-MeasurementReport.ps1')) {
            [string[]]$arguments = @('--session', $Session, '--cpu', 'pwsh', '-NoProfile', '-File',
                (Join-Path $PSScriptRoot $script), '-Series', $Series)
            $submitted = & $queueTool @arguments | ConvertFrom-Json
            Write-Host "QUEUED $($submitted.Id) $script"
            [pscustomobject]@{ Script = $script; QueueId = $submitted.Id; QueueDirectory = $submitted.OutputDirectory }
        }
    } else { @() }
    [pscustomobject]@{
        SourceCommit = $sourceCommit; SubmissionCommit = (& git rev-parse HEAD).Trim(); Backend = $Backend
        SessionId = $Session; ListingCommand = @($prefix[0]) + $listingArguments
        StartedAtUtc = $submissionStarted; FinishedAtUtc = [DateTime]::UtcNow
        CaseCount = @($submissions).Count; Machines = @($Machine); Reports = @($reports)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportDirectory 'queue-submission.json') -Encoding utf8
}
finally { Set-Location -LiteralPath $previousDirectory }

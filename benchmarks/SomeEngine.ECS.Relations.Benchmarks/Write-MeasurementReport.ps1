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
$metrics = [Collections.Generic.List[object]]::new()
$memory = [Collections.Generic.List[object]]::new()
$cold = [Collections.Generic.List[object]]::new()
$processors = [Collections.Generic.List[object]]::new()
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("# $Series measurements")
$lines.Add('')
$lines.Add('E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.')
$lines.Add('')
$lines.Add('| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |')
$lines.Add('| --- | --- | --- | --- | --- | --- | --- | --- |')
foreach ($submission in $submissions) {
    $reportId = [string]@($submission.CaseId)[0]
    $report = Get-Content -Raw -LiteralPath $submission.Report | ConvertFrom-Json
    foreach ($comparison in $report.Comparisons) {
        $metric = [pscustomobject]@{
            CaseId = $reportId; Class = $report.Input.Class; Boundary = $comparison.Boundary; Required = $comparison.Required
            Equal = $report.Equivalence.Equal; Passed = $comparison.Passed; SourceCommit = $submission.SourceCommit
            Backend = $submission.Backend; QueueId = $submission.QueueId; Frequency = $report.Device.StopwatchFrequency
            Machine = $report.Device.Machine.Configuration; MainThreadMask = $report.Device.Machine.MainThreadMask
            ProcessMask = $report.Device.Machine.ProcessMask; ProcessPriority = $report.Device.Machine.ProcessPriority
            RelationMedianTicks = $comparison.Relation.MedianTicks; HandwrittenMedianTicks = $comparison.Handwritten.MedianTicks
            RelationP95Ticks = $comparison.Relation.P95Ticks; HandwrittenP95Ticks = $comparison.Handwritten.P95Ticks
            MedianRatio = $comparison.MedianRatio; P95Ratio = $comparison.P95Ratio
            RelationIqrTicks = $comparison.Relation.IqrTicks; HandwrittenIqrTicks = $comparison.Handwritten.IqrTicks
            RelationStandardDeviationTicks = $comparison.Relation.StandardDeviationTicks
            HandwrittenStandardDeviationTicks = $comparison.Handwritten.StandardDeviationTicks
            RelationPairsPerSecond = $comparison.Relation.PairsPerSecond
            HandwrittenPairsPerSecond = $comparison.Handwritten.PairsPerSecond
            RelationTotalFrameAllocatedBytes = $comparison.Relation.TotalFrameAllocatedBytes
            HandwrittenTotalFrameAllocatedBytes = $comparison.Handwritten.TotalFrameAllocatedBytes
        }
        $metrics.Add($metric)
        $median = ([double]$metric.MedianRatio).ToString('G17', [Globalization.CultureInfo]::InvariantCulture)
        $p95 = ([double]$metric.P95Ratio).ToString('G17', [Globalization.CultureInfo]::InvariantCulture)
        $lines.Add("| $($metric.CaseId) | $($metric.Boundary) | $($metric.Required) | $($metric.RelationMedianTicks) | $($metric.HandwrittenMedianTicks) | $median | $p95 | $($metric.Passed) |")
    }
    foreach ($side in 'Relation', 'Handwritten') {
        $data = $report."$($side)Memory"
        foreach ($field in $data.PayloadBytes.PSObject.Properties) {
            $memory.Add([pscustomobject]@{ CaseId = $reportId; Side = $side; Unit = 'bytes'; Field = $field.Name; Value = $field.Value })
        }
        foreach ($field in $data.Counts.PSObject.Properties) {
            $memory.Add([pscustomobject]@{ CaseId = $reportId; Side = $side; Unit = 'count'; Field = $field.Name; Value = $field.Value })
        }
    }
    foreach ($sample in $report.ColdSamples) {
        $cold.Add([pscustomobject]@{ CaseId = $reportId; SourceCommit = $submission.SourceCommit; Backend = $submission.Backend
            Pair = $sample.Pair; SideOrder = $sample.SideOrder; Side = $sample.Side; SetupTicks = $sample.SetupTicks
            IndexBuildTicks = $sample.IndexBuildTicks; FirstQueryTicks = $sample.FirstQueryTicks; FirstConsumeTicks = $sample.FirstConsumeTicks
            TotalTicks = $sample.TotalTicks; OutputPairCount = $sample.OutputPairCount; AllocatedBytes = $sample.AllocatedBytes })
    }
    if ($null -ne $report.Device.Machine) {
        foreach ($side in 'relation', 'handwritten') {
            $samples = @($report.RawSamples | Where-Object Side -eq $side)
            $machine = $report.Device.Machine
            $processors.Add([pscustomobject]@{
                CaseId = $reportId; Side = $side; Configuration = $machine.Configuration
                ProcessMask = $machine.ProcessMask; MainThreadMask = $machine.MainThreadMask
                PreviousMainThreadMask = $machine.PreviousMainThreadMask; ProcessPriority = $machine.ProcessPriority
                MainThreadPriority = $machine.MainThreadPriority; ProcessorAfterSetting = $machine.ProcessorAfterSetting
                Samples = $samples.Count
                ObservedProcessors = (@($samples.ProcessorBefore) + @($samples.ProcessorAfter) | Sort-Object -Unique) -join ','
                ChangedEndpoints = @($samples | Where-Object { $_.ProcessorBefore -ne $_.ProcessorAfter }).Count
            })
        }
    }
}
$required = @($metrics | Where-Object Required)
$passed = @($required | Where-Object Passed)
$lines.Add('')
$lines.Add("Required metrics: $($passed.Count)/$($required.Count) pass. Case reports: $($submissions.Count).")
$lines.Add('')
$lines.Add('Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.')
$lines | Set-Content -LiteralPath (Join-Path $evidence 'summary.md') -Encoding utf8
$metrics | Export-Csv -LiteralPath (Join-Path $evidence 'metrics.csv') -NoTypeInformation -Encoding utf8
$memory | Export-Csv -LiteralPath (Join-Path $evidence 'memory.csv') -NoTypeInformation -Encoding utf8
$cold | Export-Csv -LiteralPath (Join-Path $evidence 'cold.csv') -NoTypeInformation -Encoding utf8
if ($processors.Count -gt 0) {
    $processors | Export-Csv -LiteralPath (Join-Path $evidence 'processors.csv') -NoTypeInformation -Encoding utf8
}
Write-Output "$Series`: cases=$($submissions.Count); required=$($required.Count); passed=$($passed.Count); $evidence"
foreach ($group in ($required | Group-Object Class)) {
    Write-Output ("CLASS {0}: {1}/{2}" -f $group.Name, @($group.Group | Where-Object Passed).Count, $group.Count)
}
foreach ($metric in ($required | Where-Object { !$_.Passed })) {
    Write-Output ("OVERHEAD {0} {1} median={2:G17} p95={3:G17}" -f
        $metric.CaseId, $metric.Boundary, $metric.MedianRatio, $metric.P95Ratio)
}

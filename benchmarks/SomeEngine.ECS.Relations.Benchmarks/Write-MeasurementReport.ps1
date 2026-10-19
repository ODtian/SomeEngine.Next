param(
    [Parameter(Mandatory)][string]$Series,
    [string]$Destination = 'docs/evidence/ECS-Relations-2026-10-19',
    [double]$ApprovedRatioLimit = 1.01
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$reports = Join-Path $repositoryRoot "artifacts/ecs-relations/measurements/$Series"
$evidence = Join-Path (Join-Path $repositoryRoot $Destination) $Series
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$submissions = Get-Content -Raw -LiteralPath (Join-Path $reports 'submissions.json') | ConvertFrom-Json
$metrics = [Collections.Generic.List[object]]::new()
$controlMetrics = [Collections.Generic.List[object]]::new()
$memory = [Collections.Generic.List[object]]::new()
$cold = [Collections.Generic.List[object]]::new()
$processors = [Collections.Generic.List[object]]::new()
$workspaces = [Collections.Generic.List[object]]::new()
$initialization = [Collections.Generic.List[object]]::new()
$memberTerms = [Collections.Generic.List[object]]::new()
$formalCases = 0
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("# $Series measurements")
$lines.Add('')
$lines.Add('E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.')
$lines.Add('')
$lines.Add("| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=$ApprovedRatioLimit |")
$lines.Add('| --- | --- | --- | --- | --- | --- | --- | --- | --- |')
foreach ($submission in $submissions) {
    $reportId = [string]@($submission.CaseId)[0]
    $report = Get-Content -Raw -LiteralPath $submission.Report | ConvertFrom-Json
    $control = $report.ReportKind -eq 'HandwrittenControl'
    if (!$control) { $formalCases++ }
    foreach ($comparison in $report.Comparisons) {
        if ($control) {
            $h1 = $comparison.FirstHandwritten
            $h2 = $comparison.SecondHandwritten
            $controlMetrics.Add([pscustomobject]@{
                CaseId = $reportId; Boundary = $comparison.Boundary; Required = $comparison.Required
                Equal = $report.Equivalence.Equal; FirstHandwrittenType = $report.FirstHandwrittenType
                SecondHandwrittenType = $report.SecondHandwrittenType; SourceCommit = $submission.SourceCommit
                Backend = $submission.Backend; QueueId = $submission.QueueId; Frequency = $report.Device.StopwatchFrequency
                FirstHandwrittenMedianTicks = $h1.MedianTicks; SecondHandwrittenMedianTicks = $h2.MedianTicks
                FirstHandwrittenP95Ticks = $h1.P95Ticks; SecondHandwrittenP95Ticks = $h2.P95Ticks
                MedianRatio = $comparison.MedianRatio; P95Ratio = $comparison.P95Ratio
                FirstHandwrittenIqrTicks = $h1.IqrTicks; SecondHandwrittenIqrTicks = $h2.IqrTicks
                FirstHandwrittenStandardDeviationTicks = $h1.StandardDeviationTicks
                SecondHandwrittenStandardDeviationTicks = $h2.StandardDeviationTicks
                FirstHandwrittenPairsPerSecond = $h1.PairsPerSecond; SecondHandwrittenPairsPerSecond = $h2.PairsPerSecond
                FirstHandwrittenTotalFrameAllocatedBytes = $h1.TotalFrameAllocatedBytes
                SecondHandwrittenTotalFrameAllocatedBytes = $h2.TotalFrameAllocatedBytes
            })
            continue
        }
        $metric = [pscustomobject]@{
            CaseId = $reportId; Class = $report.Input.Class; Boundary = $comparison.Boundary; Required = $comparison.Required
            Equal = $report.Equivalence.Equal; Passed = $comparison.Passed; SourceCommit = $submission.SourceCommit
            ApprovedRatioLimit = $ApprovedRatioLimit
            ApprovedPassed = $comparison.MedianRatio -le $ApprovedRatioLimit -and $comparison.P95Ratio -le $ApprovedRatioLimit
            Backend = $submission.Backend; QueueId = $submission.QueueId; Frequency = $report.Device.StopwatchFrequency
            Machine = $report.Device.Machine.Configuration; MainThreadMask = $report.Device.Machine.MainThreadMask
            ProcessMask = $report.Device.Machine.ProcessMask; ProcessPriority = $report.Device.Machine.ProcessPriority
            DataSharing = $report.DataSharing
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
        $lines.Add("| $($metric.CaseId) | $($metric.Boundary) | $($metric.Required) | $($metric.RelationMedianTicks) | $($metric.HandwrittenMedianTicks) | $median | $p95 | $($metric.Passed) | $($metric.ApprovedPassed) |")
    }
    $sides = if ($control) { @('FirstHandwritten', 'SecondHandwritten') } else { @('Relation', 'Handwritten') }
    foreach ($side in $sides) {
        $sideLabel = if ($side -eq 'FirstHandwritten') { 'H1' } elseif ($side -eq 'SecondHandwritten') { 'H2' } else { $side }
        $initialization.Add([pscustomobject]@{
            CaseId = $reportId; Side = $sideLabel; IndexBuildTicks = $report."$($side)IndexBuildTicks"
            Frequency = $report.Device.StopwatchFrequency
        })
        $data = $report."$($side)Memory"
        foreach ($field in $data.PayloadBytes.PSObject.Properties) {
            $memory.Add([pscustomobject]@{ CaseId = $reportId; Side = $sideLabel; Unit = 'bytes'; Field = $field.Name; Value = $field.Value })
        }
        foreach ($field in $data.Counts.PSObject.Properties) {
            $memory.Add([pscustomobject]@{ CaseId = $reportId; Side = $sideLabel; Unit = 'count'; Field = $field.Name; Value = $field.Value })
        }
    }
    foreach ($workspace in $report.Workspaces) {
        $workspaces.Add([pscustomobject]@{
            CaseId = $reportId; DataSharing = $report.DataSharing; UsedBy = $workspace.UsedBy
            SetupTicks = $workspace.SetupTicks; Frequency = $report.Device.StopwatchFrequency
        })
        foreach ($term in $workspace.MemberTerms) {
            $memberTerms.Add([pscustomobject]@{
                CaseId = $reportId; UsedBy = $workspace.UsedBy; ComponentId = $term.ComponentId
                Kind = $term.Kind; Access = $term.Access; Filters = $term.Filters
            })
        }
        foreach ($field in $workspace.Memory.PayloadBytes.PSObject.Properties) {
            $memory.Add([pscustomobject]@{ CaseId = $reportId; Side = "$($workspace.UsedBy)-workspace"; Unit = 'bytes'; Field = $field.Name; Value = $field.Value })
        }
        foreach ($field in $workspace.Memory.Counts.PSObject.Properties) {
            $memory.Add([pscustomobject]@{ CaseId = $reportId; Side = "$($workspace.UsedBy)-workspace"; Unit = 'count'; Field = $field.Name; Value = $field.Value })
        }
    }
    foreach ($sample in $report.ColdSamples) {
        $cold.Add([pscustomobject]@{ CaseId = $reportId; SourceCommit = $submission.SourceCommit; Backend = $submission.Backend
            Pair = $sample.Pair; SideOrder = $sample.SideOrder; Side = $sample.Side; SetupTicks = $sample.SetupTicks
            IndexBuildTicks = $sample.IndexBuildTicks; FirstQueryTicks = $sample.FirstQueryTicks; FirstConsumeTicks = $sample.FirstConsumeTicks
            TotalTicks = $sample.TotalTicks; OutputPairCount = $sample.OutputPairCount; AllocatedBytes = $sample.AllocatedBytes })
    }
    if ($null -ne $report.Device.Machine) {
        $sampleSides = if ($control) { @('H1', 'H2') } else { @('relation', 'handwritten') }
        foreach ($side in $sampleSides) {
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
$approved = @($required | Where-Object ApprovedPassed)
$lines.Add('')
$lines.Add("Required metrics: $($passed.Count)/$($required.Count) pass <=1.00; $($approved.Count)/$($required.Count) pass <=$ApprovedRatioLimit. Case reports: $formalCases.")
$lines.Add('')
$lines.Add('Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.')
$lines | Set-Content -LiteralPath (Join-Path $evidence 'summary.md') -Encoding utf8
$metrics | Export-Csv -LiteralPath (Join-Path $evidence 'metrics.csv') -NoTypeInformation -Encoding utf8
if ($required.Count -gt 0) {
    [pscustomobject]@{
        Summary = [pscustomobject]@{
            Series = $Series; SourceCommit = $submissions[0].SourceCommit
            ApprovedRatioLimit = $ApprovedRatioLimit; StrictRatioLimit = 1.0
            Cases = $formalCases; Equal = @($metrics | Where-Object Equal | Select-Object -ExpandProperty CaseId -Unique).Count
            Required = $required.Count; StrictPassed = $passed.Count
            ApprovedPassed = $approved.Count; ApprovedUnmet = $required.Count - $approved.Count
        }
        Classes = @($required | Group-Object Class | ForEach-Object {
            [pscustomobject]@{
                Class = $_.Name; Required = $_.Count
                StrictPassed = @($_.Group | Where-Object Passed).Count
                ApprovedPassed = @($_.Group | Where-Object ApprovedPassed).Count
            }
        })
        Metrics = $required
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'approved-summary.json') -Encoding utf8
}
$memory | Export-Csv -LiteralPath (Join-Path $evidence 'memory.csv') -NoTypeInformation -Encoding utf8
$cold | Export-Csv -LiteralPath (Join-Path $evidence 'cold.csv') -NoTypeInformation -Encoding utf8
if ($processors.Count -gt 0) {
    $processors | Export-Csv -LiteralPath (Join-Path $evidence 'processors.csv') -NoTypeInformation -Encoding utf8
}
if ($workspaces.Count -gt 0) {
    $workspaces | Export-Csv -LiteralPath (Join-Path $evidence 'workspaces.csv') -NoTypeInformation -Encoding utf8
    $initialization | Export-Csv -LiteralPath (Join-Path $evidence 'initialization.csv') -NoTypeInformation -Encoding utf8
}
if ($memberTerms.Count -gt 0) {
    $memberTerms | Export-Csv -LiteralPath (Join-Path $evidence 'member-terms.csv') -NoTypeInformation -Encoding utf8
}
if ($controlMetrics.Count -gt 0) {
    $controlMetrics | Export-Csv -LiteralPath (Join-Path $evidence 'control-metrics.csv') -NoTypeInformation -Encoding utf8
    $controlLines = [Collections.Generic.List[string]]::new()
    $controlLines.Add("# $Series handwritten controls")
    $controlLines.Add('')
    $controlLines.Add('H1 and H2 use the same handwritten implementation with independent side state and the same actual workspace. Required=false; these ratios are diagnostic data.')
    $controlLines.Add('')
    $controlLines.Add('| Case | Boundary | H1 type | H2 type | H1 median ticks | H2 median ticks | H1/H2 median | H1/H2 p95 |')
    $controlLines.Add('| --- | --- | --- | --- | --- | --- | --- | --- |')
    foreach ($metric in $controlMetrics) {
        $median = ([double]$metric.MedianRatio).ToString('G17', [Globalization.CultureInfo]::InvariantCulture)
        $p95 = ([double]$metric.P95Ratio).ToString('G17', [Globalization.CultureInfo]::InvariantCulture)
        $controlLines.Add("| $($metric.CaseId) | $($metric.Boundary) | $($metric.FirstHandwrittenType) | $($metric.SecondHandwrittenType) | $($metric.FirstHandwrittenMedianTicks) | $($metric.SecondHandwrittenMedianTicks) | $median | $p95 |")
    }
    $controlLines | Set-Content -LiteralPath (Join-Path $evidence 'control-summary.md') -Encoding utf8
}
Write-Output "$Series`: cases=$($submissions.Count); required=$($required.Count); passed=$($passed.Count); approved=$($approved.Count); approvedRatioLimit=$ApprovedRatioLimit; $evidence"
foreach ($group in ($required | Group-Object Class)) {
    Write-Output ("CLASS {0}: {1}/{2}" -f $group.Name, @($group.Group | Where-Object Passed).Count, $group.Count)
}
foreach ($metric in ($required | Where-Object { !$_.Passed })) {
    Write-Output ("OVERHEAD {0} {1} median={2:G17} p95={3:G17}" -f
        $metric.CaseId, $metric.Boundary, $metric.MedianRatio, $metric.P95Ratio)
}

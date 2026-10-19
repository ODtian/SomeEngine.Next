param([Parameter(Mandatory)][string]$Series)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$reports = Join-Path $repositoryRoot "artifacts/ecs-relations/measurements/$Series"
$destination = Join-Path $repositoryRoot "docs/evidence/ECS-Relations-2026-10-19/$Series/sample-tails.json"
$submissions = Get-Content -Raw -LiteralPath (Join-Path $reports 'submissions.json') | ConvertFrom-Json

function Percentile([object[]]$Values, [double]$Fraction) {
    $sorted = @($Values | Sort-Object)
    $position = ($sorted.Count - 1) * $Fraction
    $lower = [int][Math]::Floor($position)
    $upper = [int][Math]::Ceiling($position)
    [double]$sorted[$lower] + ($position - $lower) * ([double]$sorted[$upper] - [double]$sorted[$lower])
}

$analyses = foreach ($submission in $submissions) {
    $report = Get-Content -Raw -LiteralPath $submission.Report | ConvertFrom-Json
    foreach ($comparison in @($report.Comparisons | Where-Object { $_.Required -and !$_.Passed })) {
        $completeFrame = $report.Input.Class -eq 'complete' -and $comparison.Boundary -eq 'FrameTotal'
        $sourceBoundary = if ($completeFrame) { $report.Input.PrimaryBoundary } else { $comparison.Boundary }
        $ticksProperty = if ($completeFrame) { 'FrameTicks' } else { 'ElapsedTicks' }
        $sides = foreach ($side in @('relation', 'handwritten')) {
            $distribution = $report.Comparisons | Where-Object Boundary -eq $comparison.Boundary |
                ForEach-Object { $_.$side }
            $samples = @($report.RawSamples | Where-Object { $_.Boundary -eq $sourceBoundary -and $_.Side -eq $side })
            $tail = @($samples | Where-Object { $_.$ticksProperty -ge $distribution.P95Ticks })
            $frames = foreach ($group in @($samples | Group-Object Frame)) {
                $values = @($group.Group.$ticksProperty)
                [pscustomobject]@{
                    Frame = [int]$group.Name; Count = $values.Count
                    MedianMicroseconds = (Percentile $values 0.5) * 1e6 / $report.Device.StopwatchFrequency
                    P95Microseconds = (Percentile $values 0.95) * 1e6 / $report.Device.StopwatchFrequency
                }
            }
            [pscustomobject]@{
                Side = $side; Samples = $samples.Count
                MedianMicroseconds = $distribution.MedianTicks * 1e6 / $report.Device.StopwatchFrequency
                P95Microseconds = $distribution.P95Ticks * 1e6 / $report.Device.StopwatchFrequency
                IqrMicroseconds = $distribution.IqrTicks * 1e6 / $report.Device.StopwatchFrequency
                StandardDeviationMicroseconds = $distribution.StandardDeviationTicks * 1e6 / $report.Device.StopwatchFrequency
                Gen0 = ($samples.Gen0 | Measure-Object -Sum).Sum
                Gen1 = ($samples.Gen1 | Measure-Object -Sum).Sum
                Gen2 = ($samples.Gen2 | Measure-Object -Sum).Sum
                ObservedProcessors = @(@($samples.ProcessorBefore) + @($samples.ProcessorAfter) | Sort-Object -Unique)
                ChangedEndpoints = @($samples | Where-Object { $_.ProcessorBefore -ne $_.ProcessorAfter }).Count
                TailCount = $tail.Count; Frame0TailCount = @($tail | Where-Object Frame -eq 0).Count
                TailByRound = @($tail | Group-Object Round | ForEach-Object { [pscustomobject]@{ Round = [int]$_.Name; Count = $_.Count } })
                TailBySideOrder = @($tail | Group-Object SideOrder | ForEach-Object { [pscustomobject]@{ SideOrder = $_.Name; Count = $_.Count } })
                TailByFrame = @($tail | Group-Object Frame | ForEach-Object { [pscustomobject]@{ Frame = [int]$_.Name; Count = $_.Count } })
                FrameDistributions = @($frames)
                SideOrderDistributions = @($samples | Group-Object SideOrder | ForEach-Object {
                    $values = @($_.Group.$ticksProperty)
                    [pscustomobject]@{
                        SideOrder = $_.Name; Samples = $values.Count
                        MedianMicroseconds = (Percentile $values 0.5) * 1e6 / $report.Device.StopwatchFrequency
                        P95Microseconds = (Percentile $values 0.95) * 1e6 / $report.Device.StopwatchFrequency
                    }
                })
                TailSamples = @($tail | Select-Object Round, SideOrder, Frame,
                    @{ Name = 'ElapsedTicks'; Expression = { $_.$ticksProperty } },
                    ProcessorBefore, ProcessorAfter, Gen0, Gen1, Gen2)
            }
        }
        [pscustomobject]@{
            CaseId = [string]@($submission.CaseId)[0]; SourceCommit = $submission.SourceCommit
            Boundary = $comparison.Boundary; MedianRatio = $comparison.MedianRatio; P95Ratio = $comparison.P95Ratio
            Frequency = $report.Device.StopwatchFrequency; DataSharing = $report.DataSharing; Sides = @($sides)
        }
    }
}
@($analyses) | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $destination -Encoding utf8
foreach ($analysis in $analyses) {
    foreach ($side in $analysis.Sides) {
        Write-Output ("{0} {1}: medianUs={2:G17} p95Us={3:G17} tails={4} frame0Tails={5} GC={6}/{7}/{8} endpointChanges={9}" -f
            $analysis.CaseId, $side.Side, $side.MedianMicroseconds, $side.P95Microseconds,
            $side.TailCount, $side.Frame0TailCount, $side.Gen0, $side.Gen1, $side.Gen2, $side.ChangedEndpoints)
    }
}
Write-Output "TAILS $destination"

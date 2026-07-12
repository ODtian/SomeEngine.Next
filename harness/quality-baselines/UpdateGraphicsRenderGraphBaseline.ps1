param(
    [string]$CheckpointCommit = "c0ac382e"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outputPath = Join-Path $PSScriptRoot "graphics-hard-baseline.v1.json"
$projects = @(
    @{
        Assembly = "SomeEngine.Graphics.Direct3D12"
        Path = "src\SomeEngine.Graphics.Direct3D12\SomeEngine.Graphics.Direct3D12.csproj"
    },
    @{
        Assembly = "SomeEngine.RenderGraph"
        Path = "src\SomeEngine.RenderGraph\SomeEngine.RenderGraph.csproj"
    }
)
$entries = [System.Collections.Generic.List[object]]::new()
$entryKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$checkpointSources = @{}
$invalidDiagnostics = [System.Collections.Generic.List[string]]::new()
$diagnosticPattern = '^(?<path>.+\.cs)\((?<line>\d+),(?<column>\d+)\): error (?<id>SE02[0-4]): (?<message>.+?) \['

foreach ($project in $projects) {
    $projectPath = Join-Path $repoRoot $project.Path
    $arguments = @(
        "build",
        $projectPath,
        "--no-restore",
        "--no-incremental",
        "-v",
        "minimal",
        "/p:BuildProjectReferences=false",
        "/p:NuGetAudit=false",
        "/p:SomeEngineQualityAnalyzerEnabled=true",
        "/p:SomeEngineQualityBaselineEnabled=false",
        "/p:NoWarn=SE010%3BSE031%3BSE052%3BRS2008%3BCS0618"
    )
    $output = & dotnet @arguments 2>&1

    foreach ($line in $output) {
        $text = $line.ToString()
        if ($text -notmatch $diagnosticPattern) {
            continue
        }

        $id = $Matches.id
        $diagnosticPath = $Matches.path
        $diagnosticLine = [int]$Matches.line
        $message = $Matches.message
        $symbolMatch = [regex]::Match($message, "'(?<symbol>[^']+)'")
        $metricPattern = switch ($id) {
            "SE020" { 'complexity (?<value>\d+)' }
            "SE021" { ' is (?<value>\d+) lines' }
            "SE022" { ' has (?<value>\d+) methods' }
            "SE023" { ' has (?<value>\d+) fields' }
            "SE024" { ' references (?<value>\d+) distinct types' }
        }
        $metricMatch = [regex]::Match($message, $metricPattern)
        if (-not $symbolMatch.Success -or -not $metricMatch.Success) {
            throw "Cannot parse accepted diagnostic: $text"
        }

        $fullPath = [System.IO.Path]::GetFullPath($diagnosticPath)
        $relative = [System.IO.Path]::GetRelativePath($repoRoot, $fullPath).Replace('\', '/')
        & git -C $repoRoot cat-file -e "${CheckpointCommit}:$relative"
        if ($LASTEXITCODE -ne 0) {
            throw "Diagnostic is not from the accepted checkpoint ${CheckpointCommit}: $relative"
        }

        if (-not $checkpointSources.ContainsKey($relative)) {
            $checkpointSources[$relative] = (& git -C $repoRoot show "${CheckpointCommit}:$relative") -join "`n"
            if ($LASTEXITCODE -ne 0) {
                throw "Cannot read accepted checkpoint source ${CheckpointCommit}:$relative"
            }
        }

        $symbol = $symbolMatch.Groups['symbol'].Value
        if ($checkpointSources[$relative].IndexOf($symbol, [System.StringComparison]::Ordinal) -lt 0) {
            $invalidDiagnostics.Add("$id $relative`:$diagnosticLine '$symbol' did not exist in $CheckpointCommit")
            continue
        }

        $entryKey = "$id|$($project.Assembly)|$relative|$diagnosticLine|$symbol"
        if (-not $entryKeys.Add($entryKey)) {
            continue
        }

        $entries.Add([pscustomobject][ordered]@{
            id = $id
            assembly = $project.Assembly
            path = $relative
            line = $diagnosticLine
            symbol = $symbol
            maximumObserved = [int]$metricMatch.Groups['value'].Value
            reason = "c0ac382e accepted RHI/RG checkpoint; new or worsened diagnostics remain hard failures"
        })
    }
}

if ($invalidDiagnostics.Count -ne 0) {
    throw "Only diagnostics on checkpoint symbols may be accepted:`n$($invalidDiagnostics -join "`n")"
}

$orderedEntries = $entries |
    Sort-Object assembly, path, line, id, symbol
$document = [ordered]@{
    schemaVersion = 1
    checkpointCommit = $CheckpointCommit
    entries = @($orderedEntries)
}
$document | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outputPath -Encoding utf8
Write-Host "Wrote $($orderedEntries.Count) accepted checkpoint diagnostics to $outputPath"

param(
    [string]$Series = 'initial',
    [string[]]$CaseId = @(),
    [ValidateSet('NativeAOT', 'JIT')][string]$Backend = 'NativeAOT'
)
$ErrorActionPreference = 'Stop'
$queueTool = 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$reportDirectory = Join-Path $repositoryRoot "artifacts/ecs-relations/measurements/$Series"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$executable = Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/native-publish/SomeEngine.ECS.Relations.Benchmarks.exe'
$managed = Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/SomeEngine.ECS.Relations.Benchmarks.dll'
$prefix = if ($Backend -eq 'NativeAOT') { @($executable) } else { @('dotnet', $managed) }
$previousDirectory = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    $listing = & $queueTool @($prefix + @('--mode', 'list')) | ConvertFrom-Json
    $listingResult = Join-Path $listing.OutputDirectory 'result.json'
    while (!(Test-Path -LiteralPath $listingResult)) { Start-Sleep -Seconds 1 }
    $completed = Get-Content -Raw -LiteralPath $listingResult | ConvertFrom-Json
    if ($completed.ExitCode -ne 0) { throw "Case listing failed: $($listing.Id)" }
    $cases = Get-Content -Raw -LiteralPath (Join-Path $listing.OutputDirectory 'stdout.log') | ConvertFrom-Json
    $selected = $cases | Where-Object { $CaseId.Count -eq 0 -or $_.Id -in $CaseId }
    $submissions = foreach ($case in $selected) {
        $report = Join-Path $reportDirectory "$($case.Id).json"
        [string[]]$arguments = $prefix + @('--case', $case.Class, '--size', $case.Size,
            '--variant', $case.Variant, '--source', $sourceCommit, '--backend', $Backend, '--output', $report)
        $submitted = & $queueTool @arguments | ConvertFrom-Json
        Write-Host "QUEUED $($submitted.Id) $($case.Id)"
        [pscustomobject]@{ CaseId = $case.Id; SourceCommit = $sourceCommit; Backend = $Backend;
            Report = $report; QueueId = $submitted.Id; QueueDirectory = $submitted.OutputDirectory }
    }
    $submissions | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportDirectory 'submissions.json') -Encoding utf8
}
finally { Set-Location -LiteralPath $previousDirectory }

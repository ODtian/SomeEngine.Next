param(
    [string]$RunId = $env:SOMEENGINE_AGENT_RUN_ID,
    [ValidateSet("Gate", "Hard", "Warning")]
    [string]$Mode = "Gate",
    [string]$Configuration = "Debug",
    [string]$Verbosity = "quiet",
    [switch]$NoBuild,
    [int]$ProductTestParallelism = 4
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:checks = @()
$script:failures = @()
$script:warnings = @()
$script:status = "PASS"
$script:built = $false
$script:qualityInfrastructureNoWarn = "RS2008%3BCS0618"
$script:qualitySoftRuleNoWarn = "SE010%3BSE031%3BSE052%3B$script:qualityInfrastructureNoWarn"
$script:qualityBoundaryRuleNoWarn = "SE001%3BSE002%3BSE020%3BSE021%3BSE022%3BSE023%3BSE024%3BSE030%3B$script:qualityInfrastructureNoWarn"
$script:qualityStyleRuleWarningsAsErrors = "SE010%3BSE031%3BSE052"
. (Join-Path $PSScriptRoot "ProcessExecution.ps1")

function Add-CheckResult {
    param(
        [string]$Name,
        [string]$Kind,
        [int]$ExitCode,
        [double]$Seconds,
        [string]$Status
    )

    $script:checks += [ordered]@{
        name = $Name
        kind = $Kind
        exitCode = $ExitCode
        seconds = [Math]::Round($Seconds, 3)
        status = $Status
    }
}

function Set-FailingStatusFromOutput {
    param([string]$Output)

    if ($script:status -eq "HARNESS_BROKEN") {
        return
    }

    if ($Output -match "HARNESS_BROKEN") {
        $script:status = "HARNESS_BROKEN"
        return
    }

    if ($script:status -eq "NEEDS_GRILL") {
        return
    }

    if ($Output -match "NEEDS_GRILL:") {
        $script:status = "NEEDS_GRILL"
        return
    }

    if ($script:status -eq "PASS") {
        $script:status = "NEEDS_FIX"
    }
}

function Invoke-HarnessStep {
    param(
        [string]$Name,
        [ValidateSet("hard", "warning")]
        [string]$Kind,
        [string]$FileName,
        [string[]]$Arguments
    )

    Write-Host "==> [$Kind] $Name"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $environment = @{}
    if (-not [string]::IsNullOrWhiteSpace($RunId)) {
        $environment["SOMEENGINE_AGENT_RUN_ID"] = $RunId
    }

    $execution = Start-CapturedProcess `
        -FileName $FileName `
        -Arguments $Arguments `
        -WorkingDirectory $repoRoot `
        -Environment $environment
    $result = Complete-CapturedProcess $execution
    $stdout = $result.StandardOutput
    $stderr = $result.StandardError
    $sw.Stop()

    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host $stdout.TrimEnd()
    }

    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr.TrimEnd()
    }

    $combined = "$stdout`n$stderr"
    if ($result.ExitCode -eq 0) {
        Add-CheckResult -Name $Name -Kind $Kind -ExitCode $result.ExitCode -Seconds $sw.Elapsed.TotalSeconds -Status "PASS"
        return $true
    }

    if ($Kind -eq "warning") {
        $script:warnings += $Name
        Add-CheckResult -Name $Name -Kind $Kind -ExitCode $result.ExitCode -Seconds $sw.Elapsed.TotalSeconds -Status "WARNING"
        Write-Host "WARNING: $Name failed but does not block PASS."
        return $true
    }

    $script:failures += $Name
    Set-FailingStatusFromOutput -Output $combined
    Add-CheckResult -Name $Name -Kind $Kind -ExitCode $result.ExitCode -Seconds $sw.Elapsed.TotalSeconds -Status $script:status
    return $false
}

function Invoke-BuildOnce {
    if ($NoBuild -or $script:built) {
        return $true
    }

    $ok = Invoke-HarnessStep -Name "build-declared-boundary" -Kind "hard" -FileName "pwsh" -Arguments @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        "harness/BuildDeclaredBoundary.ps1",
        "-Configuration",
        $Configuration,
        "-ProjectSet",
        "All",
        "-Verbosity",
        "minimal"
    )
    $script:built = $ok
    return $ok
}

function Invoke-HardBucket {
    if (-not (Invoke-HarnessStep -Name "harness-execution" -Kind "hard" -FileName "pwsh" -Arguments @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        "harness/TestHarnessExecution.ps1"
    ))) {
        return $false
    }

    if (-not (Invoke-BuildOnce)) {
        return $false
    }

    $steps = @(
        @{ Name = "product-tests"; File = "pwsh"; Args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "harness/RunProductTests.ps1", "-Configuration", $Configuration, "-Verbosity", $Verbosity, "-NoBuild", "-Parallelism", $ProductTestParallelism, "-TraitMode", "Hard") },
        @{ Name = "graphics-benchmark-soak"; File = "dotnet"; Args = @("run", "--project", "benchmarks/SomeEngine.Graphics.Benchmarks/SomeEngine.Graphics.Benchmarks.csproj", "--no-build", "--no-restore", "--configuration", $Configuration) },
        @{ Name = "architecture"; File = "dotnet"; Args = @("test", "harness/architecture/SomeEngine.Harness.Architecture/SomeEngine.Harness.Architecture.csproj", "--no-build", "--no-restore", "--configuration", $Configuration, "--verbosity", $Verbosity) },
        @{ Name = "behaviour"; File = "dotnet"; Args = @("test", "harness/behaviour/SomeEngine.Harness.Behaviour/SomeEngine.Harness.Behaviour.csproj", "--no-build", "--no-restore", "--configuration", $Configuration, "--verbosity", $Verbosity) },
        @{ Name = "quality-analyzer-tests"; File = "dotnet"; Args = @("test", "harness/quality/SomeEngine.Harness.QualityAnalyzer.Tests/SomeEngine.Harness.QualityAnalyzer.Tests.csproj", "--no-build", "--no-restore", "--configuration", $Configuration, "--verbosity", $Verbosity) },
        @{ Name = "quality-product-boundary"; File = "pwsh"; Args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "harness/BuildDeclaredBoundary.ps1", "-ProjectSet", "Source", "-NoRestore", "-NoIncremental", "-Configuration", $Configuration, "-Verbosity", "minimal", "-QualityAnalyzerEnabled", "-NoWarn", $script:qualitySoftRuleNoWarn, "-OutputRoot", "harness/artifacts/quality-hard") }
    )

    $hardBucketPassed = $true
    foreach ($step in $steps) {
        if (-not (Invoke-HarnessStep -Name $step.Name -Kind "hard" -FileName $step.File -Arguments $step.Args)) {
            $hardBucketPassed = $false
        }
    }

    return $hardBucketPassed
}

function Invoke-WarningBucket {
    if ($Mode -eq "Warning" -and -not (Invoke-BuildOnce)) {
        return
    }

    $steps = @(
        @{ Name = "product-tests-performance"; File = "pwsh"; Args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "harness/RunProductTests.ps1", "-Configuration", $Configuration, "-Verbosity", $Verbosity, "-NoBuild", "-Parallelism", $ProductTestParallelism, "-TraitMode", "Warning") },
        @{ Name = "maintainability"; File = "dotnet"; Args = @("test", "harness/maintainability/SomeEngine.Harness.Maintainability/SomeEngine.Harness.Maintainability.csproj", "--no-build", "--no-restore", "--configuration", $Configuration, "--verbosity", $Verbosity) },
        @{ Name = "quality-product-style"; File = "pwsh"; Args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "harness/BuildDeclaredBoundary.ps1", "-ProjectSet", "Source", "-NoRestore", "-NoIncremental", "-Configuration", $Configuration, "-Verbosity", "minimal", "-QualityAnalyzerEnabled", "-NoWarn", $script:qualityBoundaryRuleNoWarn, "-WarningsAsErrors", $script:qualityStyleRuleWarningsAsErrors, "-OutputRoot", "harness/artifacts/quality-style") },
        @{ Name = "coverage-collect"; File = "pwsh"; Args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "harness/coverage/GenerateCoverage.ps1", "-Configuration", $Configuration, "-NoBuild") },
        @{ Name = "coverage-gate"; File = "dotnet"; Args = @("test", "harness/coverage/SomeEngine.Harness.Coverage/SomeEngine.Harness.Coverage.csproj", "--no-build", "--no-restore", "--configuration", $Configuration, "--verbosity", $Verbosity) }
    )

    foreach ($step in $steps) {
        [void](Invoke-HarnessStep -Name $step.Name -Kind "warning" -FileName $step.File -Arguments $step.Args)
    }
}

function Write-HarnessRunSummary {
    $summary = [ordered]@{
        status = $script:status
        runId = if ([string]::IsNullOrWhiteSpace($RunId)) { $null } else { $RunId }
        mode = $Mode
        hardChecksExecuted = ($Mode -ne "Warning")
        failures = $script:failures
        warnings = $script:warnings
        checks = $script:checks
    }

    if (-not [string]::IsNullOrWhiteSpace($RunId)) {
        $batchDir = Join-Path $repoRoot ".agent-runs/$RunId/batch"
        if (-not (Test-Path $batchDir)) {
            New-Item -ItemType Directory -Path $batchDir | Out-Null
        }

        $summaryFileName = if ($Mode -eq "Warning") { "harness-warning-run.json" } else { "harness-run.json" }
        $summaryPath = Join-Path $batchDir $summaryFileName
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
        Write-Host "Harness run summary: $summaryPath"
    }

    Write-Host "Harness status: $($script:status)"
    if ($script:warnings.Count -gt 0) {
        Write-Host ("Harness warnings: " + ($script:warnings -join ", "))
    }
}

try {
    if ($Mode -eq "Hard") {
        [void](Invoke-HardBucket)
    }
    elseif ($Mode -eq "Warning") {
        Invoke-WarningBucket
    }
    else {
        if (Invoke-HardBucket) {
            Invoke-WarningBucket
        }
    }
}
finally {
    Write-HarnessRunSummary
}

if ($script:status -eq "PASS") {
    exit 0
}

exit 1


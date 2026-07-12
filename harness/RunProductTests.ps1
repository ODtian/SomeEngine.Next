param(
    [string]$Configuration = "Debug",
    [string]$Verbosity = "quiet",
    [switch]$NoBuild,
    [int]$Parallelism = 1,
    [ValidateSet("Hard", "Warning", "All")]
    [string]$TraitMode = "Hard"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$configPath = Join-Path $repoRoot "harness\config.json"
$config = Get-Content -Raw $configPath | ConvertFrom-Json
. (Join-Path $PSScriptRoot "ProcessExecution.ps1")

$projects = @($config.projects.testProjects |
    ForEach-Object { Get-Item (Join-Path $repoRoot $_.path) } |
    Sort-Object FullName)

function New-TraitFilter {
    $warningTraits = @($config.productTests.warningTraits)
    if ($warningTraits.Count -eq 0 -or $TraitMode -eq "All") {
        return ""
    }

    if ($TraitMode -eq "Warning") {
        return (($warningTraits | ForEach-Object { "$($_.name)=$($_.value)" }) -join "|")
    }

    return (($warningTraits | ForEach-Object { "$($_.name)!=$($_.value)" }) -join "&")
}

function New-TestArguments($projectPath) {
    $args = @(
        "test",
        $projectPath,
        "--no-restore",
        "--configuration",
        $Configuration,
        "--verbosity",
        $Verbosity
    )

    if ($NoBuild) {
        $args += "--no-build"
    }

    $traitFilter = New-TraitFilter
    if (-not [string]::IsNullOrWhiteSpace($traitFilter)) {
        $args += "--filter"
        $args += $traitFilter
    }

    return $args
}

function Invoke-TestProject($project) {
    dotnet @(New-TestArguments $project.FullName)
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

function Start-TestProject($project) {
    return [pscustomobject]@{
        Project = $project
        Execution = Start-CapturedProcess `
            -FileName "dotnet" `
            -Arguments (New-TestArguments $project.FullName) `
            -WorkingDirectory $repoRoot
    }
}

if ($Parallelism -le 1) {
    foreach ($project in $projects) {
        Invoke-TestProject $project
    }
    return
}

$queue = [System.Collections.Generic.Queue[object]]::new()
foreach ($project in $projects) {
    $queue.Enqueue($project)
}

$running = @()
$failedExitCode = 0
while ($queue.Count -gt 0 -or $running.Count -gt 0) {
    while ($queue.Count -gt 0 -and $running.Count -lt $Parallelism) {
        $running += Start-TestProject ($queue.Dequeue())
    }

    Start-Sleep -Milliseconds 100
    $stillRunning = @()
    foreach ($entry in $running) {
        if (-not $entry.Execution.Process.HasExited) {
            $stillRunning += $entry
            continue
        }

        $result = Complete-CapturedProcess $entry.Execution
        $stdout = $result.StandardOutput
        $stderr = $result.StandardError
        if (-not [string]::IsNullOrWhiteSpace($stdout)) {
            Write-Host $stdout.TrimEnd()
        }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            Write-Host $stderr.TrimEnd()
        }
        if ($result.ExitCode -ne 0 -and $failedExitCode -eq 0) {
            $failedExitCode = $result.ExitCode
        }
    }

    $running = $stillRunning
}

if ($failedExitCode -ne 0) {
    exit $failedExitCode
}

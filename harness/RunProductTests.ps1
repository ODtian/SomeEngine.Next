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
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.WorkingDirectory = $repoRoot
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false

    foreach ($arg in (New-TestArguments $project.FullName)) {
        [void]$psi.ArgumentList.Add($arg)
    }

    $process = [Diagnostics.Process]::Start($psi)
    if ($null -eq $process) {
        throw "Failed to start dotnet test for $($project.FullName)."
    }

    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()

    return [pscustomobject]@{
        Project = $project
        Process = $process
        StandardOutputTask = $standardOutputTask
        StandardErrorTask = $standardErrorTask
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
        if (-not $entry.Process.HasExited) {
            $stillRunning += $entry
            continue
        }

        $stdout = $entry.StandardOutputTask.GetAwaiter().GetResult()
        $stderr = $entry.StandardErrorTask.GetAwaiter().GetResult()
        if (-not [string]::IsNullOrWhiteSpace($stdout)) {
            Write-Host $stdout.TrimEnd()
        }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            Write-Host $stderr.TrimEnd()
        }
        if ($entry.Process.ExitCode -ne 0 -and $failedExitCode -eq 0) {
            $failedExitCode = $entry.Process.ExitCode
        }
    }

    $running = $stillRunning
}

if ($failedExitCode -ne 0) {
    exit $failedExitCode
}

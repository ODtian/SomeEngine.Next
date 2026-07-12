$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "ProcessExecution.ps1")

function Stop-FixtureProcesses {
    param([object[]]$Executions)

    foreach ($execution in $Executions) {
        if ($null -ne $execution -and -not $execution.Process.HasExited) {
            $execution.Process.Kill($true)
        }
    }
}

$fixture = Join-Path $PSScriptRoot "fixtures\WriteProcessOutput.ps1"
$lineCount = 4096
$executions = @()
try {
    for ($index = 0; $index -lt 4; $index++) {
        $executions += Start-CapturedProcess `
            -FileName "pwsh" `
            -WorkingDirectory $repoRoot `
            -Arguments @(
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                $fixture,
                "-LineCount",
                "$lineCount",
                "-StreamId",
                "parallel-$index"
            )
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (@($executions | Where-Object { -not $_.Process.HasExited }).Count -gt 0) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "HARNESS_BROKEN: captured parallel processes did not exit before the 30 second deadline."
        }
        Start-Sleep -Milliseconds 20
    }

    foreach ($execution in $executions) {
        $result = Complete-CapturedProcess $execution
        $stdoutLines = @($result.StandardOutput -split "\r?\n" | Where-Object { $_.Length -gt 0 }).Count
        $stderrLines = @($result.StandardError -split "\r?\n" | Where-Object { $_.Length -gt 0 }).Count
        if ($result.ExitCode -ne 0 -or $stdoutLines -ne $lineCount -or $stderrLines -ne $lineCount) {
            throw "HARNESS_BROKEN: captured process lost output or returned an unexpected exit code."
        }
    }
}
finally {
    Stop-FixtureProcesses $executions
}

$failureExecution = Start-CapturedProcess `
    -FileName "pwsh" `
    -WorkingDirectory $repoRoot `
    -Arguments @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $fixture,
        "-LineCount",
        "16",
        "-ExitCode",
        "17",
        "-StreamId",
        "failure"
    )
$failureResult = Complete-CapturedProcess $failureExecution
if ($failureResult.ExitCode -ne 17 -or
    -not $failureResult.StandardOutput.Contains("stdout-failure-15-") -or
    -not $failureResult.StandardError.Contains("stderr-failure-15-")) {
    throw "HARNESS_BROKEN: captured process did not preserve failure exit code and both output streams."
}

Write-Host "Harness process execution self-test passed: 4 parallel processes, 4096 lines per stream, and failure exit-code propagation."

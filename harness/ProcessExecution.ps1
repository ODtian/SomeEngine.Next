function Start-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [System.Collections.IDictionary]$Environment = @{}
    )

    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false

    foreach ($argument in $Arguments) {
        [void]$psi.ArgumentList.Add($argument)
    }
    foreach ($key in $Environment.Keys) {
        $psi.Environment[[string]$key] = [string]$Environment[$key]
    }

    $process = [Diagnostics.Process]::Start($psi)
    if ($null -eq $process) {
        throw "Failed to start captured process: $FileName"
    }

    return [pscustomobject]@{
        Process = $process
        StandardOutputTask = $process.StandardOutput.ReadToEndAsync()
        StandardErrorTask = $process.StandardError.ReadToEndAsync()
    }
}

function Complete-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)]
        $Execution
    )

    $Execution.Process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $Execution.Process.ExitCode
        StandardOutput = $Execution.StandardOutputTask.GetAwaiter().GetResult()
        StandardError = $Execution.StandardErrorTask.GetAwaiter().GetResult()
    }
}

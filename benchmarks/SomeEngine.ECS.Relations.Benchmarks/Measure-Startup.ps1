param(
    [Parameter(Mandatory)][string]$CaseClass,
    [Parameter(Mandatory)][string]$Size,
    [Parameter(Mandatory)][string]$Variant,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('Normal', 'Fixed')][string]$Machine = 'Fixed'
)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/native-publish/SomeEngine.ECS.Relations.Benchmarks.exe'
$samples = foreach ($pair in 0..6) {
    $sides = if ($pair % 2 -eq 0) { @('relation', 'handwritten') } else { @('handwritten', 'relation') }
    foreach ($side in $sides) {
        $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @('--mode', 'first-result', '--case', $CaseClass, '--size', $Size,
            '--variant', $Variant, '--source', $SourceCommit, '--side', $side, '--machine', $Machine)) {
            $startInfo.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        $startedAt = [DateTime]::UtcNow
        $started = [Diagnostics.Stopwatch]::GetTimestamp()
        $null = $process.Start()
        $firstResultTicks = 0L
        $firstResult = ''
        while (($line = $process.StandardOutput.ReadLine()) -ne $null) {
            if ($line.StartsWith('FIRST_RESULT ')) {
                $firstResultTicks = [Diagnostics.Stopwatch]::GetTimestamp() - $started
                $firstResult = $line
            }
        }
        $errors = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $end = [Diagnostics.Stopwatch]::GetTimestamp()
        if ($process.ExitCode -ne 0) { throw "Startup execution failed: $errors" }
        [pscustomobject]@{ Pair = $pair; SideOrder = $sides -join '/'; Side = $side;
            SourceCommit = $SourceCommit; Backend = 'NativeAOT'; Command = "$executable $($startInfo.ArgumentList -join ' ')";
            StartedAtUtc = $startedAt; ProcessToFirstResultTicks = $firstResultTicks;
            ProcessToExitTicks = $end - $started; StopwatchFrequency = [Diagnostics.Stopwatch]::Frequency;
            ChildFirstResult = $firstResult }
        $process.Dispose()
    }
}
New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force | Out-Null
$samples | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding utf8
Write-Output "STARTUP $CaseClass-$Size-$Variant samples=$($samples.Count) report=$Output"

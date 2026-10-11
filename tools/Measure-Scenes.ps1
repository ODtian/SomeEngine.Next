param([switch]$Trace, [string]$ApplicationPath)
$ErrorActionPreference = 'Stop'
$application = if ($ApplicationPath) { [IO.Path]::GetFullPath($ApplicationPath) }
    else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../templates/DefaultProject/Game.App/bin/Release/net10.0/Game.App.exe')) }
if (!(Test-Path -LiteralPath $application)) { throw 'Build DefaultProject/Game.App in Release first.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SceneTestKeys {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, UIntPtr key, IntPtr data);
}
'@
$started = [DateTime]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
$start = [Diagnostics.ProcessStartInfo]::new($application)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
if ($Trace) {
    $startupTraces = Join-Path ([IO.Path]::GetDirectoryName($application)) 'Performance'
    [void][IO.Directory]::CreateDirectory($startupTraces)
    $start.Environment['SOMEENGINE_STARTUP_TRACE'] = Join-Path $startupTraces ('startup-' + [Guid]::NewGuid().ToString('N') + '.setrace')
} else {
    [void]$start.Environment.Remove('SOMEENGINE_STARTUP_TRACE')
}
$process = [Diagnostics.Process]::Start($start)
$output = $process.StandardOutput.ReadToEndAsync()
$errors = $process.StandardError.ReadToEndAsync()
$report = $null
try {
    while (!$process.HasExited -and $process.MainWindowHandle -eq 0 -and $timer.Elapsed.TotalSeconds -lt 55) {
        Start-Sleep -Milliseconds 50
        $process.Refresh()
    }
    if ($process.HasExited) { throw 'DefaultGame exited before creating its test window.' }
    $key = if ($Trace) { 0x79 } else { 0x78 }
    [void][SceneTestKeys]::PostMessage($process.MainWindowHandle, 0x100, [UIntPtr]$key, [IntPtr]0)
    [void][SceneTestKeys]::PostMessage($process.MainWindowHandle, 0x101, [UIntPtr]$key, [IntPtr]0)
    $reports = Join-Path ([IO.Path]::GetDirectoryName($application)) 'Performance'
    while (!$process.HasExited -and $timer.Elapsed.TotalSeconds -lt 57) {
        $report = Get-ChildItem -LiteralPath $reports -Filter results.json -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -ge $started } | Select-Object -First 1
        if ($report) { break }
        Start-Sleep -Milliseconds 100
    }
    if (!$process.HasExited) {
        [void][SceneTestKeys]::PostMessage($process.MainWindowHandle, 0x10, [UIntPtr]0, [IntPtr]0)
        [void]$process.WaitForExit([Math]::Max(0, [int](59000 - $timer.Elapsed.TotalMilliseconds)))
    }
} finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $applicationOutput = $output.GetAwaiter().GetResult()
    $errorText = $errors.GetAwaiter().GetResult()
    if ($errorText) { Write-Host $errorText }
    $applicationExitCode = $process.ExitCode
    $process.Dispose()
}
if (!$report -and (Test-Path -LiteralPath $reports)) {
    $report = Get-ChildItem -LiteralPath $reports -Filter results.json -Recurse |
        Where-Object { $_.LastWriteTimeUtc -ge $started } | Select-Object -First 1
}
if (!$report -or $applicationExitCode -ne 0) {
    Write-Host $applicationOutput
    throw 'The scene test did not finish successfully within 60 seconds. No complete performance result is available.'
}
$result = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
$result.Scenes | Format-Table Scene, Status, Frames, MeanMs, MedianMs, P95Ms, PrepareMs, ExecuteMs
if ($result.Startup) {
    $result.Startup | Format-Table ApplicationToWindowMs, WindowToFirstFrameMs, ApplicationToFirstFrameMs
    $result.Startup.Stages | Format-Table Stage, ElapsedMs
}
Write-Host "Report: $($report.FullName) ($([Math]::Round($timer.Elapsed.TotalSeconds, 2)) seconds including application startup)"
$requiredScenes = @('1024 spheres', 'Sponza', 'Amazon Bistro')
if (!$result.Complete -or $result.Scenes.Count -ne $requiredScenes.Count -or
    @(Compare-Object $requiredScenes @($result.Scenes.Scene)).Count -ne 0 -or
    @($result.Scenes | Where-Object Status -ne 'Measured').Count -ne 0) {
    throw 'The multi-scene run is incomplete; do not use it as a passing performance comparison.'
}

param([switch]$Trace, [string]$ApplicationPath, [string]$OutputDirectory,
    [string[]]$ApplicationArguments, [string]$Scene, [string]$ShadingPath, [string]$Playback,
    [switch]$PassThru, [int]$MemoryMB = 8192)
$ErrorActionPreference = 'Stop'
function Write-LogTail([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $bytes = [byte[]]::new([int][Math]::Min(65536, $stream.Length))
        $stream.Position = $stream.Length - $bytes.Length
        $stream.ReadExactly($bytes, 0, $bytes.Length)
        Write-Host ([Text.Encoding]::UTF8.GetString($bytes))
    } finally { $stream.Dispose() }
}
$application = if ($ApplicationPath) { [IO.Path]::GetFullPath($ApplicationPath) }
    else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../samples/DefaultGame/bin/game/Release/net10.0/DefaultGame.exe')) }
if (!(Test-Path -LiteralPath $application)) { throw 'Publish samples/DefaultGame in Release first.' }
$watchdog = Join-Path $PSScriptRoot 'ProcessWatchdog/bin/Release/net10.0/SomeEngine.ProcessWatchdog.dll'
if (!(Test-Path -LiteralPath $watchdog)) {
    & dotnet build (Join-Path $PSScriptRoot 'ProcessWatchdog/ProcessWatchdog.csproj') -c Release -m:1 -nr:false -p:UseSharedCompilation=false -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SceneTestKeys {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, UIntPtr key, IntPtr data);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
$started = [DateTime]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$reports = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) }
    else { Join-Path ([IO.Path]::GetTempPath()) ('SomeEngine/scene-measurements/' + [Guid]::NewGuid().ToString('N')) }
[void][IO.Directory]::CreateDirectory($reports)
$processIdFile = Join-Path $reports 'application.pid'
[IO.File]::Delete($processIdFile)
$outputPath = Join-Path $reports 'stdout.log'
$errorPath = Join-Path $reports 'stderr.log'
foreach ($argument in @($watchdog, '--memory-mb', [string]$MemoryMB, '--timeout', '59', '--idle-timeout', '20', '--process-id-file', $processIdFile, '--', $application)) {
    $start.ArgumentList.Add($argument)
}
$start.Environment['SOMEENGINE_SCENE_MEASUREMENTS'] = $reports
if ($Scene) {
    $start.ArgumentList.Add('--measure-scene')
    $start.ArgumentList.Add($Scene)
}
foreach ($argument in $ApplicationArguments) { $start.ArgumentList.Add($argument) }
if ($ShadingPath) {
    $start.ArgumentList.Add('--shading-path')
    $start.ArgumentList.Add($ShadingPath)
}
if ($playback) {
    $start.ArgumentList.Add('--scene-playback')
    $start.ArgumentList.Add($playback)
}
if ($Trace) {
    $startupTraces = $reports
    [void][IO.Directory]::CreateDirectory($startupTraces)
    $start.Environment['SOMEENGINE_STARTUP_TRACE'] = Join-Path $startupTraces ('startup-' + [Guid]::NewGuid().ToString('N') + '.setrace')
} else {
    [void]$start.Environment.Remove('SOMEENGINE_STARTUP_TRACE')
}
$process = [Diagnostics.Process]::Start($start)
$outputFile = [IO.File]::Create($outputPath)
$errorFile = [IO.File]::Create($errorPath)
$output = $process.StandardOutput.BaseStream.CopyToAsync($outputFile)
$errors = $process.StandardError.BaseStream.CopyToAsync($errorFile)
$applicationProcess = $null
$report = $null
try {
    while (!$process.HasExited -and $timer.Elapsed.TotalSeconds -lt 55) {
        if (!$applicationProcess -and (Test-Path -LiteralPath $processIdFile)) {
            $applicationProcess = [Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText($processIdFile))
        }
        if ($applicationProcess) {
            $applicationProcess.Refresh()
            if ($applicationProcess.MainWindowHandle -ne 0) { break }
        }
        Start-Sleep -Milliseconds 50
    }
    if ($process.HasExited) { throw 'DefaultGame exited before creating its test window.' }
    $window = $applicationProcess.MainWindowHandle
    # Keep the measured client area above desktop tools at its configured size and position.
    [void][SceneTestKeys]::ShowWindowAsync($window, 5)
    [void][SceneTestKeys]::SetWindowPos($window, [IntPtr]::new(-1), 0, 0, 0, 0, 0x4043)
    $key = if ($Trace) { 0x79 } else { 0x78 }
    [void][SceneTestKeys]::PostMessage($window, 0x100, [UIntPtr]$key, [IntPtr]0)
    [void][SceneTestKeys]::PostMessage($window, 0x101, [UIntPtr]$key, [IntPtr]0)
    while (!$process.HasExited -and $timer.Elapsed.TotalSeconds -lt 58.5) {
        $report = Get-ChildItem -LiteralPath $reports -Filter results.json -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -ge $started } | Select-Object -First 1
        if ($report) { break }
        Start-Sleep -Milliseconds 100
    }
    if (!$process.HasExited) {
        [void][SceneTestKeys]::PostMessage($window, 0x10, [UIntPtr]0, [IntPtr]0)
        [void]$process.WaitForExit([Math]::Max(0, [int](59000 - $timer.Elapsed.TotalMilliseconds)))
    }
} finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    [void]$output.GetAwaiter().GetResult()
    [void]$errors.GetAwaiter().GetResult()
    $outputFile.Dispose()
    $errorFile.Dispose()
    Write-LogTail $errorPath
    $applicationExitCode = $process.ExitCode
    if ($applicationProcess) { $applicationProcess.Dispose() }
    $process.Dispose()
    [IO.File]::Delete($processIdFile)
    [IO.File]::Delete($processIdFile + '.tmp')
}
if (!$report -and (Test-Path -LiteralPath $reports)) {
    $report = Get-ChildItem -LiteralPath $reports -Filter results.json -Recurse |
        Where-Object { $_.LastWriteTimeUtc -ge $started } | Select-Object -First 1
}
if (!$report -or $applicationExitCode -ne 0) {
    Write-LogTail $outputPath
    throw 'The scene test did not finish successfully within 60 seconds. No complete performance result is available.'
}
$result = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
$result.Scenes | Format-Table Scene, Status, Frames, MeanMs, MedianMs, P95Ms, PrepareMs, ExecuteMs | Out-Host
if ($result.Startup) {
    $result.Startup | Format-Table ApplicationToWindowMs, WindowToFirstFrameMs, ApplicationToFirstFrameMs | Out-Host
    $result.Startup.Stages | Format-Table Stage, ElapsedMs | Out-Host
}
Write-Host "Report: $($report.FullName) ($([Math]::Round($timer.Elapsed.TotalSeconds, 2)) seconds including application startup)"
$requiredScenes = @('1024 spheres', 'Sponza', 'Amazon Bistro')
if (!$result.Complete -or $result.Scenes.Count -ne $requiredScenes.Count -or
    @(Compare-Object $requiredScenes @($result.Scenes.Scene)).Count -ne 0 -or
    @($result.Scenes | Where-Object Status -ne 'Measured').Count -ne 0) {
    throw 'The multi-scene run is incomplete; do not use it as a passing performance comparison.'
}
if ($PassThru) { [pscustomobject]@{ ReportPath = $report.FullName; Report = $result } }

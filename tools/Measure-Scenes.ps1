param([switch]$Trace, [string]$ApplicationPath, [string]$OutputDirectory,
    [string[]]$ApplicationArguments, [string]$Scene, [string]$ShadingPath, [string]$Playback,
    [switch]$PassThru, [int]$MemoryMB = 8192)
$ErrorActionPreference = 'Stop'
function Write-LogTail([string]$Path) {
    Get-Content -LiteralPath $Path -Tail 40 | Out-Host
}
$executable = if ($IsWindows) { 'DefaultGame.exe' } else { 'DefaultGame' }
$application = if ($ApplicationPath) { [IO.Path]::GetFullPath($ApplicationPath) }
    else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../samples/DefaultGame/bin/game/Release/net10.0/$executable")) }
if (!(Test-Path -LiteralPath $application)) { throw 'Publish samples/DefaultGame in Release first.' }
$reports = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) }
    else { Join-Path ([IO.Path]::GetTempPath()) ('SomeEngine/scene-measurements/' + [Guid]::NewGuid().ToString('N')) }
[void][IO.Directory]::CreateDirectory($reports)
$start = [Diagnostics.ProcessStartInfo]::new($application)
if ($IsWindows) {
    $watchdog = Join-Path $PSScriptRoot 'ProcessWatchdog/bin/Release/net10.0/SomeEngine.ProcessWatchdog.dll'
    if (!(Test-Path -LiteralPath $watchdog)) {
        & dotnet build (Join-Path $PSScriptRoot 'ProcessWatchdog/ProcessWatchdog.csproj') -c Release -m:1 -nr:false -p:UseSharedCompilation=false -v quiet
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    $start.FileName = 'dotnet'
    foreach ($argument in @($watchdog, '--memory-mb', [string]$MemoryMB, '--timeout', '59', '--idle-timeout', '20', '--', $application)) {
        $start.ArgumentList.Add($argument)
    }
}
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('--measure-scenes')
if ($IsLinux) { $start.ArgumentList.Add('--graphics-backend'); $start.ArgumentList.Add('vulkan'); $start.ArgumentList.Add('--skip-present') }
if ($Scene) { $start.ArgumentList.Add('--measure-scene'); $start.ArgumentList.Add($Scene) }
foreach ($argument in $ApplicationArguments) { $start.ArgumentList.Add($argument) }
if ($ShadingPath) { $start.ArgumentList.Add('--shading-path'); $start.ArgumentList.Add($ShadingPath) }
if ($Playback) { $start.ArgumentList.Add('--scene-playback'); $start.ArgumentList.Add($Playback) }
$start.Environment['SOMEENGINE_SCENE_MEASUREMENTS'] = $reports
if ($Trace) {
    $start.ArgumentList.Add('--trace-scenes')
    $start.Environment['SOMEENGINE_STARTUP_TRACE'] = Join-Path $reports 'startup.setrace'
} else { [void]$start.Environment.Remove('SOMEENGINE_STARTUP_TRACE') }
$started = [DateTime]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
$process = [Diagnostics.Process]::Start($start)
$outputPath = Join-Path $reports 'stdout.log'
$errorPath = Join-Path $reports 'stderr.log'
$outputFile = [IO.File]::Create($outputPath)
$errorFile = [IO.File]::Create($errorPath)
$output = $process.StandardOutput.BaseStream.CopyToAsync($outputFile)
$errors = $process.StandardError.BaseStream.CopyToAsync($errorFile)
try {
    if (!$process.WaitForExit(59000)) { $process.Kill($true); $process.WaitForExit() }
    [void]$output.GetAwaiter().GetResult()
    [void]$errors.GetAwaiter().GetResult()
    $applicationExitCode = $process.ExitCode
} finally {
    $outputFile.Dispose(); $errorFile.Dispose(); $process.Dispose()
}
$report = Get-ChildItem -LiteralPath $reports -Filter results.json -Recurse |
    Where-Object LastWriteTimeUtc -ge $started | Select-Object -First 1
if (!$report -or $applicationExitCode -ne 0) {
    Write-LogTail $outputPath
    Write-LogTail $errorPath
    throw 'The scene measurement did not complete successfully within 60 seconds.'
}
$result = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
$result.Scenes | Format-Table Scene, Status, Frames, MeanMs, MedianMs, P95Ms, PrepareMs, ExecuteMs | Out-Host
if ($result.Startup) {
    $result.Startup | Format-Table ApplicationToWindowMs, WindowToFirstFrameMs, ApplicationToFirstFrameMs | Out-Host
    $result.Startup.Stages | Format-Table Stage, ElapsedMs | Out-Host
}
Write-Host "Report: $($report.FullName) ($([Math]::Round($timer.Elapsed.TotalSeconds, 2)) seconds including application startup)"
$requiredScenes = if ($Scene) { @($Scene) } else { @('1024 spheres', 'Sponza', 'Amazon Bistro') }
if (!$result.Complete -or $result.Scenes.Count -ne $requiredScenes.Count -or
    @(Compare-Object $requiredScenes @($result.Scenes.Scene)).Count -ne 0 -or
    @($result.Scenes | Where-Object Status -ne 'Measured').Count -ne 0) {
    throw 'The requested scenes did not reach a complete measurement window.'
}
if ($PassThru) { [pscustomobject]@{ ReportPath = $report.FullName; Report = $result } }

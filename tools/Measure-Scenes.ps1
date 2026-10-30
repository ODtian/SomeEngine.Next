param(
    [switch]$Trace, [switch]$TraceDetail, [string]$ApplicationPath, [switch]$Managed,
    [string[]]$ApplicationArguments = @(),
    [string]$SceneConfigurationJson,
    [string]$PlatformConfigurationJson,
    [string]$PublishSourceRevision,
    [ValidateSet('deferred', 'forward')][string]$ShadingPath,
    [ValidateSet('static', 'motion', 'materials', 'camera')][string]$ScenePlayback = 'camera',
    [string]$ExpectedScene,
    [ValidateRange(1, 120)][int]$StartupFrames,
    [ValidateNotNullOrEmpty()][string]$Scene
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$queue = 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1'
$session = $env:SOMEENGINE_BENCHMARK_SESSION ?? $env:CODEX_THREAD_ID ?? $env:CODEX_SESSION_ID ?? $repository
$measurementArguments = @('-B', 'tools/Measure-Scenes.py', '--scene-playback', $ScenePlayback)
if ($ApplicationPath) {
    $measurementArguments += @('--application', $ApplicationPath)
    if (!$SceneConfigurationJson -and [IO.File]::Exists($ApplicationPath)) {
        $sceneConfiguration = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ApplicationPath))) 'test-scenes.json'
        $measurementArguments += @('--scene-configuration-json', [IO.File]::ReadAllText($sceneConfiguration))
    }
}
if ($SceneConfigurationJson) { $measurementArguments += @('--scene-configuration-json', $SceneConfigurationJson) }
if ($PlatformConfigurationJson) { $measurementArguments += @('--platform-configuration-json', $PlatformConfigurationJson) }
if ($PublishSourceRevision) { $measurementArguments += @('--publish-source-revision', $PublishSourceRevision) }
if ($Managed) { $measurementArguments += '--managed' }
if ($Trace) { $measurementArguments += '--trace' }
if ($TraceDetail) { $measurementArguments += '--trace-detail' }
if ($ShadingPath) { $measurementArguments += @('--shading-path', $ShadingPath) }
if ($ExpectedScene) { $measurementArguments += @('--expected-scene', $ExpectedScene) }
if ($StartupFrames) { $measurementArguments += @('--startup-frames', [string]$StartupFrames) }
if ($Scene) { $measurementArguments += @('--scene', $Scene) }
foreach ($argument in $ApplicationArguments) { $measurementArguments += "--application-argument=$argument" }

Push-Location $repository
try {
    $queueArguments = @('--gpu', '--session', $session)
    if ($PublishSourceRevision) { $queueArguments += @('--timeout', '1200') }
    $receiptText = & $queue @queueArguments python @measurementArguments
    if ($LASTEXITCODE -ne 0) { throw 'The GPU measurement queue submission failed.' }
} finally { Pop-Location }
$receipt = ($receiptText -split '\r?\n' | Where-Object { $_.Trim() } | Select-Object -Last 1) | ConvertFrom-Json
Write-Host "Queued $($receipt.Id); waiting for $($receipt.OutputDirectory)."
$resultPath = Join-Path $receipt.OutputDirectory 'result.json'
while (!(Test-Path -LiteralPath $resultPath)) { Start-Sleep -Seconds 1 }
$completed = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
$output = [IO.File]::ReadAllText((Join-Path $receipt.OutputDirectory 'stdout.log'))
$errors = [IO.File]::ReadAllText((Join-Path $receipt.OutputDirectory 'stderr.log'))
foreach ($line in ($output -split '\r?\n')) {
    if (!$line.StartsWith('SCENE_FRAME_SAMPLES ')) { [Console]::Out.WriteLine($line) }
}
[Console]::Error.Write($errors)
if ($StartupFrames) { exit [int]$completed.ExitCode }

$directory = $env:SOMEENGINE_MEASUREMENT_OUTPUT_DIRECTORY
$basename = $env:SOMEENGINE_MEASUREMENT_OUTPUT_BASENAME
if ($directory -or $basename) {
    if (!$directory -or !$basename) { throw 'Flat measurement output requires both directory and basename.' }
    $reportName = $basename + '-results.json'
    $samplesName = $basename + '-frame-samples.json'
} elseif ($ApplicationPath -and [IO.File]::Exists($ApplicationPath)) {
    $directory = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ApplicationPath))) ('Performance/' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
    [void][IO.Directory]::CreateDirectory($directory)
    $reportName = 'results.json'
    $samplesName = 'frame-samples.json'
}
if ($directory) {
    foreach ($entry in @(
        @{ Prefix = 'SCENE_REPORT '; Name = $reportName },
        @{ Prefix = 'SCENE_FRAME_SAMPLES '; Name = $samplesName }
    )) {
        $line = $output -split '\r?\n' | Where-Object { $_.StartsWith($entry.Prefix) } | Select-Object -Last 1
        if ($line) {
            $path = Join-Path $directory $entry.Name
            $json = $line.Substring($entry.Prefix.Length)
            if ($entry.Prefix -eq 'SCENE_REPORT ') {
                $report = $json | ConvertFrom-Json
                $report.FrameSamples = $samplesName
                $json = $report | ConvertTo-Json -Depth 30
            }
            $file = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
            try {
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
                $file.Write($bytes, 0, $bytes.Length)
            } finally { $file.Dispose() }
        }
    }
    Write-Host "Report: $(Join-Path $directory $reportName)"
}
exit [int]$completed.ExitCode

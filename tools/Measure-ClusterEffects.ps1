param(
    [Parameter(Mandatory)][string]$ApplicationPath,
    [string[]]$Effects = @('glass', 'smoke', 'foliage', 'deformed-phong', 'xray', 'decal-layers', 'decal-sparse', 'ordered'),
    [ValidateSet('full', 'xyz')][string[]]$Methods = @('full', 'xyz'),
    [switch]$Static,
    [string]$OutputPath = 'wiki/architecture/rendering/Cluster-Blend-Scenes-2026-10-16.json'
)
$ErrorActionPreference = 'Stop'
$application = [IO.Path]::GetFullPath($ApplicationPath)
$applicationDirectory = [IO.Path]::GetDirectoryName($application)
$sceneSettingsPath = Join-Path $applicationDirectory 'test-scenes.json'
$originalSceneSettings = [IO.File]::ReadAllText($sceneSettingsPath)
$sceneSettings = $originalSceneSettings | ConvertFrom-Json
$firstFrames = @{ '1024 spheres' = 2048; 'Sponza' = 1024; 'Amazon Bistro' = 2048 }
foreach ($scene in $sceneSettings) {
    $scene.MeasurementFrames.FirstFrame = $firstFrames[$scene.Name]
    $scene.MeasurementFrames.FrameCount = 256
    if ($Static) { $scene | Add-Member -NotePropertyName Animate -NotePropertyValue $false -Force }
}
$runs = [Collections.Generic.List[object]]::new()
try {
    $sceneSettings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $sceneSettingsPath -Encoding utf8
    foreach ($effect in $Effects) {
        foreach ($method in $Methods) {
            $arguments = @("--cluster-effect=$effect", "--blend-index=$method", '--measure-blending', '--no-async-compute', '--no-debug-ui')
            if ($Static) { $arguments += @('--cluster-static', '--no-taa') }
            $playback = if ($Static) { 'static' } else { 'camera' }
            Write-Host "Cluster workload: $effect; candidates: $method; playback: $playback"
            $started = [DateTime]::UtcNow
            & (Join-Path $PSScriptRoot 'Measure-Scenes.ps1') -ApplicationPath $application -ApplicationArguments $arguments -ScenePlayback $playback
            $reportFile = Get-ChildItem -LiteralPath (Join-Path $applicationDirectory 'Performance') -Filter results.json -Recurse |
                Where-Object LastWriteTimeUtc -ge $started | Select-Object -First 1
            $report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
            $framesPath = Join-Path $reportFile.DirectoryName 'frame-samples.json'
            $frames = Get-Content -LiteralPath $framesPath -Raw | ConvertFrom-Json
            foreach ($scene in $frames.Scenes) {
                if ($scene.BlendFrames.Count -ne $scene.Frames.Count) {
                    throw "GPU sample count differs from presented-frame samples for $($scene.Scene)."
                }
            }
            $runs.Add(@{ Effect = $effect; Method = $method; Arguments = $arguments; Report = $report; FrameSamples = $frames })
            @{ Complete = $runs.Count -eq $Effects.Count * $Methods.Count; Trace = $false; SceneSettings = $sceneSettings; Runs = $runs } |
                ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $OutputPath -Encoding utf8
            Remove-Item -LiteralPath $reportFile.FullName
            Remove-Item -LiteralPath $framesPath
            Remove-Item -LiteralPath $reportFile.DirectoryName
        }
    }
}
finally { [IO.File]::WriteAllText($sceneSettingsPath, $originalSceneSettings) }

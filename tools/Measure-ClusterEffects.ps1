param(
    [Parameter(Mandatory)][string]$ApplicationPath,
    [string[]]$Effects = @('glass', 'smoke', 'foliage', 'deformed-phong', 'xray', 'decal-layers', 'decal-sparse', 'ordered'),
    [ValidateSet('full', 'xy', 'xyz')][string[]]$Methods = @('xy', 'xyz'),
    [hashtable]$FirstFrames,
    [int]$FrameCount = 64,
    [switch]$Static,
    [string]$OutputPath = 'artifacts/cluster-blend-scenes.json'
)
$ErrorActionPreference = 'Stop'
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))
$application = [IO.Path]::GetFullPath($ApplicationPath)
$applicationDirectory = [IO.Path]::GetDirectoryName($application)
$sceneSettingsPath = Join-Path $applicationDirectory 'test-scenes.json'
$originalSceneSettings = [IO.File]::ReadAllText($sceneSettingsPath)
$effectFirstFrames = @{
    'glass' = @{ '1024 spheres' = 384; 'Sponza' = 384; 'Amazon Bistro' = 384 }
    'smoke' = @{ '1024 spheres' = 256; 'Sponza' = 256; 'Amazon Bistro' = 256 }
    'foliage' = @{ '1024 spheres' = 1280; 'Sponza' = 384; 'Amazon Bistro' = 1280 }
    'deformed-phong' = @{ '1024 spheres' = 192; 'Sponza' = 192; 'Amazon Bistro' = 512 }
    'xray' = @{ '1024 spheres' = 1280; 'Sponza' = 384; 'Amazon Bistro' = 1280 }
    'decal-layers' = @{ '1024 spheres' = 768; 'Sponza' = 384; 'Amazon Bistro' = 1280 }
    'decal-sparse' = @{ '1024 spheres' = 1280; 'Sponza' = 384; 'Amazon Bistro' = 1280 }
    'ordered' = @{ '1024 spheres' = 1280; 'Sponza' = 384; 'Amazon Bistro' = 1280 }
}
$runs = [Collections.Generic.List[object]]::new()
try {
    foreach ($effect in $Effects) {
        $sceneSettings = $originalSceneSettings | ConvertFrom-Json
        foreach ($scene in $sceneSettings) {
            $scene.MeasurementFrames.FirstFrame = if ($FirstFrames) { $FirstFrames[$scene.Name] }
                else { $effectFirstFrames[$effect][$scene.Name] }
            $scene.MeasurementFrames.FrameCount = $FrameCount
            if ($Static) { $scene | Add-Member -NotePropertyName Animate -NotePropertyValue $false -Force }
        }
        $sceneSettings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $sceneSettingsPath -Encoding utf8
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
            $runs.Add(@{ Effect = $effect; Method = $method; Arguments = $arguments; SceneSettings = $sceneSettings; WallSeconds = ([DateTime]::UtcNow - $started).TotalSeconds; Report = $report; FrameSamples = $frames })
            @{ Complete = $runs.Count -eq $Effects.Count * $Methods.Count; Trace = $false; Runs = $runs } |
                ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $OutputPath -Encoding utf8
            Remove-Item -LiteralPath $reportFile.FullName
            Remove-Item -LiteralPath $framesPath
            Remove-Item -LiteralPath $reportFile.DirectoryName
        }
    }
}
finally { [IO.File]::WriteAllText($sceneSettingsPath, $originalSceneSettings) }

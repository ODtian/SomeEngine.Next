param(
    [Parameter(Mandatory)][string]$ApplicationPath,
    [string]$ReferenceShaderPath,
    [string]$OutputPath = 'wiki/architecture/rendering/Transparency-Workload-Scenes-2026-10-16.json'
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
}
$cases = [Collections.Generic.List[object]]::new()
foreach ($scene in @('Assembly', 'DecalLayers')) {
    if ($ReferenceShaderPath) {
        $cases.Add(@{ Name = "$scene reference"; Arguments = @("--blend-scene=$scene", '--blend-method=Cluster', "--blend-shader=$([IO.Path]::GetFullPath($ReferenceShaderPath))") })
    }
    $cases.Add(@{ Name = "$scene cluster"; Arguments = @("--blend-scene=$scene", '--blend-method=Cluster') })
    $cases.Add(@{ Name = "$scene forward"; Arguments = @("--blend-scene=$scene", '--blend-method=Forward') })
}
$cases.Add(@{ Name = 'Glass software'; Arguments = @('--oit-scene=glass', '--transparency-tiles-software') })
$cases.Add(@{ Name = 'Glass hardware'; Arguments = @('--oit-scene=glass', '--transparency-tiles-hardware') })
$runs = [Collections.Generic.List[object]]::new()
try {
    $sceneSettings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $sceneSettingsPath -Encoding utf8
    foreach ($case in $cases) {
        Write-Host "Workload: $($case.Name)"
        $started = [DateTime]::UtcNow
        & (Join-Path $PSScriptRoot 'Measure-Scenes.ps1') -ApplicationPath $application -ApplicationArguments $case.Arguments
        $reportFile = Get-ChildItem -LiteralPath (Join-Path $applicationDirectory 'Performance') -Filter results.json -Recurse |
            Where-Object LastWriteTimeUtc -ge $started | Select-Object -First 1
        $runs.Add(@{
            Name = $case.Name
            Arguments = $case.Arguments
            Report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
            FrameSamples = Get-Content -LiteralPath (Join-Path $reportFile.DirectoryName 'frame-samples.json') -Raw | ConvertFrom-Json
        })
        @{ Complete = $runs.Count -eq $cases.Count; SceneSettings = $sceneSettings; Runs = $runs } |
            ConvertTo-Json -Depth 35 | Set-Content -LiteralPath $OutputPath -Encoding utf8
        Remove-Item -LiteralPath $reportFile.FullName
        Remove-Item -LiteralPath (Join-Path $reportFile.DirectoryName 'frame-samples.json')
        Remove-Item -LiteralPath $reportFile.DirectoryName
    }
}
finally { [IO.File]::WriteAllText($sceneSettingsPath, $originalSceneSettings) }

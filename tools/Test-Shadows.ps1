param([string]$EvidenceDirectory, [switch]$DownloadScenes)
$ErrorActionPreference = 'Stop'
$engineDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$defaultDirectory = Join-Path $engineDirectory 'templates/DefaultProject'
$applicationDirectory = Join-Path $defaultDirectory 'Game.App/bin/Release/net10.0'

function Invoke-CheckedDotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

Push-Location $engineDirectory
try {
    Invoke-CheckedDotnet @('run', '--project', 'templates/DefaultProject/Game.Build', '-c', 'Release', '--',
        '--project', $defaultDirectory, '--engine', $engineDirectory)
    Invoke-CheckedDotnet @('build', 'templates/DefaultProject/Game.App', '-c', 'Release')
    if ($DownloadScenes) {
        & python (Join-Path $engineDirectory 'samples/ImportedScenes/download_scenes.py')
        if ($LASTEXITCODE -ne 0) { throw 'Scene download failed.' }
        & python (Join-Path $engineDirectory 'samples/ImportedScenes/repair_bistro.py')
        if ($LASTEXITCODE -ne 0) { throw 'Bistro source repair failed.' }
    }
    foreach ($scene in @('Sponza', 'Bistro')) {
        $contentDirectory = Join-Path $engineDirectory "samples/ImportedScenes/Apps/$scene"
        $source = if ($scene -eq 'Sponza') {
            Join-Path $engineDirectory 'samples/ImportedScenes/Sources/Sponza/Sponza.gltf'
        } else { Join-Path $engineDirectory 'samples/ImportedScenes/Sources/BistroRepaired/Bistro.glb' }
        if (!(Test-Path -LiteralPath $source)) {
            $source = if ($scene -eq 'Sponza') { Join-Path $contentDirectory 'assets/Imported/Sponza/Sponza.gltf' }
                else { Join-Path $contentDirectory 'assets/Imported/Bistro/Bistro.glb' }
        }
        if (!(Test-Path -LiteralPath $source)) { throw "Prepare $scene with -DownloadScenes before running its RT shadow comparison." }
        Invoke-CheckedDotnet @('run', '--project', 'templates/DefaultProject/Game.Build', '-c', 'Release', '--no-build', '--',
            '--import-scene', $applicationDirectory, $source, $contentDirectory, $engineDirectory)
    }
    if ($EvidenceDirectory) {
        $env:SOMEENGINE_SHADOW_EVIDENCE = [IO.Path]::GetFullPath($EvidenceDirectory)
        New-Item -ItemType Directory -Path $env:SOMEENGINE_SHADOW_EVIDENCE -Force | Out-Null
    }
    $env:SOMEENGINE_SHADOW_SOURCE = (& git rev-parse HEAD)
    Invoke-CheckedDotnet @('test', 'tests/SomeEngine.Render.Cluster.Tests', '-c', 'Release', '--filter',
        'FullyQualifiedName~DefaultGameRayShadowTests|FullyQualifiedName~DefaultGameShadowReferenceTests|FullyQualifiedName~LocalLightRayShadowTests|FullyQualifiedName~RayShadowImageSoftLocalTests|FullyQualifiedName~RayShadowImageSamplingTests|FullyQualifiedName~VirtualShadowRenderingTests')
} finally { Pop-Location }

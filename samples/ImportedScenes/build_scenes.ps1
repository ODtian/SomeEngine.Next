param([switch]$Download)
$ErrorActionPreference = 'Stop'
$sceneRoot = $PSScriptRoot
$engineRoot = [IO.Path]::GetFullPath((Join-Path $sceneRoot '../..'))
$buildRoot = Join-Path $sceneRoot 'Build'
$applicationRoot = Join-Path $engineRoot 'templates/DefaultProject/Game.App/bin/Release/net10.0'
if ($Download) {
    python (Join-Path $sceneRoot 'download_scenes.py')
    if ($LASTEXITCODE -ne 0) { throw 'Scene download failed.' }
}
python (Join-Path $sceneRoot 'repair_bistro.py')
if ($LASTEXITCODE -ne 0) { throw 'Bistro source repair failed.' }
dotnet build (Join-Path $engineRoot 'tools/SomeEngine.SceneImport/SomeEngine.SceneImport.csproj') -c Release --artifacts-path $buildRoot --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Scene importer build failed.' }
$previousPath = $env:PATH
try {
    $env:PATH = (Join-Path $engineRoot 'external/dxc/bin/x64') + ';' + $env:PATH
    foreach ($sceneName in @('Sponza', 'Bistro')) {
        $sourceFile = if ($sceneName -eq 'Sponza') { 'Sources/Sponza/Sponza.gltf' } else { 'Sources/BistroRepaired/Bistro.glb' }
        $destination = Join-Path $sceneRoot "Apps/$sceneName"
        dotnet (Join-Path $buildRoot 'bin/SomeEngine.SceneImport/release/SomeEngine.SceneImport.dll') $applicationRoot (Join-Path $sceneRoot $sourceFile) $destination $engineRoot
        if ($LASTEXITCODE -ne 0) { throw "$sceneName import failed." }
    }
} finally { $env:PATH = $previousPath }

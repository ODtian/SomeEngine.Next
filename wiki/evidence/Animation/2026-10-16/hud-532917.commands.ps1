$ErrorActionPreference = 'Stop'

& dotnet build tools/SomeEngine.Animation.Checks/SomeEngine.Animation.Checks.csproj -c Release -v quiet '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet build tools/SomeEngine.Animation.HudChecks/SomeEngine.Animation.HudChecks.csproj -c Release -v quiet '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$animationTree = (Get-Location).Path
& (Join-Path $animationTree 'tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.exe')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $animationTree 'tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.exe') --view-switch-benchmark
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$animationChecksFilter = 'FullyQualifiedName~DefaultSystemsAnimateBuiltInValuesWithTheSuppliedClock|FullyQualifiedName~AnimatingLayerWeightFadesInItsAdditiveEffect|FullyQualifiedName~PublicTrackDataSamplesOnlyItsCurrentTimelineStage'
& dotnet test tests/SomeEngine.Animation.Tests/SomeEngine.Animation.Tests.csproj -c Release --filter $animationChecksFilter -v quiet '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $animationTree 'tools/SomeEngine.Animation.HudChecks/bin/Release/net10.0/SomeEngine.Animation.HudChecks.exe') --capture-rgba artifacts/hud-final.rgba
exit $LASTEXITCODE

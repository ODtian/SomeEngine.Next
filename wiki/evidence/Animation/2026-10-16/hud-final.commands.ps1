$ErrorActionPreference = 'Stop'

& dotnet build tools/SomeEngine.Animation.HudChecks/SomeEngine.Animation.HudChecks.csproj -c Release -v quiet '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$animationTree = (Get-Location).Path
& (Join-Path $animationTree 'tools/SomeEngine.Animation.HudChecks/bin/Release/net10.0/SomeEngine.Animation.HudChecks.exe') --capture-rgba artifacts/hud-spans.rgba
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet test tests/SomeEngine.Animation.Tests/SomeEngine.Animation.Tests.csproj -c Release '--filter=FullyQualifiedName~CompletedSourceRespondsToLayerWeightChanges' -v quiet '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false'
exit $LASTEXITCODE

# World origin projection

Public behavior: `VirtualShadowMap.PrepareViews` consumes `RenderView.WorldOrigin`.
With native level6, WorldUnitsPerCentimeter1 and world origin `2^40+1/8`, the local
origin projects to horizontal NDC `-1/2048`. Source sphere coordinates and public
camera/light positions remain local float offsets from the world's double origin.

Command: `dotnet test tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj --no-build --filter FullyQualifiedName~FarWorldOriginKeepsTheSubUnitClipmapSnappingOffset`.

Before: `red.trx`, one failed test, expected `-0.00048828125`, observed zero.
After: `green.trx`, one passed test. Source hashes accompany both runs.

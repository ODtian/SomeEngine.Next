# Native clipmap centimeters in meter scenes — #138

Integration base: `6e32800210cfec765f6995f3940530b59e2e63ec`.
The Native reference is commit `396c9f059903aed5fec78ecd3d437a40c6415368`:
`Engine/Source/Runtime/Renderer/Private/VirtualShadowMaps/VirtualShadowMapClipmap.cpp:175–180,361–364,456–459`.
Its radius is `2^(absoluteLevel+1)` centimeters and its full snapped projection
width is four radii. First level6 therefore has radius128cm and width512cm.
For meter scene data, the edge lies2.56m from the center.

`WorldUnitsPerCentimeter` is the single settings input, default0.01. CPU projection
uses it to convert radius; every typed shadow view carries the same scalar;
shader distance converts to centimeters before computing the absolute level.
The scalar occupies the existing4-byte padding before light position, preserving
the176-byte C#/Slang stride. Default absolute levels6..22 match the Native range.
Self-fed centimeter shader/scrolling fixtures declare units1; meter scenes use0.01.

The one focused public test invokes `VirtualShadowMap.PrepareViews`, projects the
authored world point2.56m with the returned light matrix and expects `abs(x/w)`
in `[.99999,1.00001]`. That expected edge comes from Native geometry and unit
conversion. The same selected test body and expectation are used for red/green.

```powershell
dotnet test tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj -c Release --no-build --filter FullyQualifiedName~VirtualShadowViewTests.DirectionalFirstLevelSixCoversTheNativeExtentInMeters --results-directory <proof> --logger 'trx;LogFileName=unit.trx'
```

Red uses the preserved pre-fix compiled output: production1ffc3728, source
identical to integration6e328002, plus the new public test. It ran **1 failed /
1 executed / 0 skipped**, actual0.00999999978, expected the unchanged edge interval.
The actual compiled DLL hashes are recorded in `red-executable.json`.
Green runs the rebuilt unit source with the same selected public test:
**1 passed / 1 executed / 0 skipped**. Both default rule checks report0violations.
The necessary Release build reports0warnings/0errors.
Actual TRX/logs record each result, and `build.log` records the necessary Release
compilation. Native source provenance is retained outside the repo in
`native-vsm-sources-396c9f05.json`; original private source remains there during use.

This slice aligns distance/projection units. Perlight/moving parameters, nominal
local camera footprint, camera calibration and remaining #138 criteria continue
with their own focused cases. Broader backend, default-scene quality and
performance validation join the unified final acceptance.

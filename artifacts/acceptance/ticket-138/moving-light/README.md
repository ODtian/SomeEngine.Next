# Native movable-light bias over rendered frames — #138

Fixed reference396c9f059903aed5fec78ecd3d437a40c6415368:
`Engine/Source/Runtime/Renderer/Private/Shadows/ShadowScene.cpp:20–25,98–130,186–233`
owns activity and its10-frame factor. `VirtualShadowMapArray.cpp:5712–5714`
interpolates nonmoving bias toward max(nonmoving,moving). Directional projection
adds camera/perlight bias and applies the absolute bias lower bound0
(`VirtualShadowMapClipmap.cpp:251–254`).

`RenderLightSetSystem` advances one rendered-frame clock. Its existing light rows
hold each light's activity start/factor beside the value and identity. Added or
moved lights restart activity; static lights factor0. VSM reads these values and
produces fixed per-frame views. Global moving defaults are directional0/local1.
Its formula is nonmoving + factor*(max(nonmoving,moving)-nonmoving) + perlight.
Direction, point position and spot position/direction supply movement comparisons.

The one public case updates the real frame systems, reads public shadow-view
bias and completes actual graph frame boundaries. Native hand-worked literals
are1,.9,.8,.7,.6,.5,.4,.3,.2,.1,0; changing position then restarts1. The same
12-frame case and1e−6 scalar tolerance run red/green.

```powershell
dotnet test tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj -c Release --no-build --filter FullyQualifiedName~VirtualShadowViewTests.MovingPointLightBiasDecaysOverTenRenderedFramesAndRestartsAfterMovement --results-directory <proof> --logger 'trx;LogFileName=moving.trx'
```

- Red on prior perlight implementation:1failed/1executed/0skipped, initialactual0,
  Nativeexpected1. Preserved old executable identity is in red-executable.json.
- Green:1passed/1executed/0skipped, same literals/tolerance retained.
- Default behavior rules0violations in both runs; necessary Release build
  0warnings/0errors. D3D12 WARP.

Actual TRX/logs accompany this record. Original private source and SHA/GitBlob
provenance remain outside the repo during use, with ShadowScene sources in
native-shadow-mobility-manifest.json. Remaining #138 camera/local nominal and
conditional inputs continue; broader behavior/backend/default quality and
performance are covered by the unified final batch.

# Per-light shadow resolution inputs — #138

Native reference: UE5.8.3 commit396c9f059903aed5fec78ecd3d437a40c6415368,
`Engine/Source/Runtime/Renderer/Private/Shadows/ShadowSceneRenderer.cpp:275–280,361`
and `VirtualShadowMaps/VirtualShadowMapClipmap.cpp:251–254`. Each light's bias
adds to its light-kind resolution setting after the movement interpolation.

The common `LightShadowSettings` data is a compatible optional tail parameter of
Game/Render directional, point and spot records. It carries resolution bias and
static/movable semantics through the existing `LightRenderExtractor` and
`RenderLightSet`. View production applies each light's bias; scene codecs and
their field descriptors serialize both inputs.

The single public case authors two Static point lights in GameWorld with biases
.25 and .75, extracts them, updates the render-light set, then reads their public
shadow-view parameters. With global nonmoving bias.5, Native hand calculation
gives .75 and1.25. The same selected test and literals run red/green.

```powershell
dotnet test tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj -c Release --no-build --filter FullyQualifiedName~VirtualShadowViewTests.PointShadowViewsUseEachLightResolutionBiasFromTheGameWorld --results-directory <proof> --logger 'trx;LogFileName=per-light.trx'
```

- Red: caller data interface/test compiled before extraction/producer integration,
  **1 failed / 1 executed / 0 skipped**, expected.75, actual.5.
- Green: integrated per-light data with the same case,
  **1 passed / 1 executed / 0 skipped**, expected.75 and1.25 retained.
- Default behavior rules:0violations in both runs. Necessary Release build:
  0warnings/0errors. D3D12 WARP.

Actual outputs/TRX and the old red executable hashes accompany this note.
Native sources/provenance remain outside the repo during implementation use.
The movable-light10-rendered-frame history and global moving bias are the next
focused #138 slice. Unified final validation covers the broader behavior set.

# Native orthographic clipmap parameters — #138

Fixed source396c9f059903aed5fec78ecd3d437a40c6415368:
`Engine/Source/Runtime/Engine/Private/SceneView.cpp:715–725` gives perspective
projection scaleabs(P00) and single-eye ortho scale1.
`VirtualShadowMaps/VirtualShadowMapClipmap.cpp:228–254,298–320` gives viewport/
ortho-width calibration and first/last-level calculation. Fresh SceneView source
GitBlob/SHA/bytes are in outside-only native-camera-manifest.json.

The one public case uses a4-meter-wide ortho camera, viewport128 and virtual512.
Native width400cm dominates viewport128. Scale.5*512/400=.64 gives absolute bias
lower bound0. Half-width200cm gives firstfloor(log2(200))=7, last22=>16views.
Its first projection edge lies5.12m from center. These literals define the
unchanged case, rather than recomputing expected fields with implementation code.

Production computes one concrete camera parameter value, supplies actual view
count to shadow projection, requests and material sampling, and uses the common
distance helper for directional selection. `DirectionalMinCameraViewportWidth`
is an explicit VSM configuration input; default0 follows Native.

```powershell
dotnet test tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj -c Release --no-build --filter FullyQualifiedName~VirtualShadowViewTests.OrthographicCameraUsesTheNativeClipmapWidthAndFirstLevel --results-directory <proof> --logger 'trx;LogFileName=ortho.trx'
```

- Red: preserved pre-fix compiled moving implementation plus the new public case;
  1failed/1executed/0skipped, expectedbias0, actual1.
- Green: same case on camera source, 1passed/1executed/0skipped; bias0,16views
  and5.12m atNDC1±1e−5 retained.
- Default behavior rules0violations in both runs; necessary Release build
  0warnings/0errors. D3D12 WARP.

Actual logs/TRX/build and red executable identity accompany this note. Local
nominal camera pixel radius and conditional receiver/DOF data follow as #138
source slices; broader backend/default quality and performance join final
unified acceptance.

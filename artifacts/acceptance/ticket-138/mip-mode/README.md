# Native local mip parity — #138

Reference396c9f059903aed5fec78ecd3d437a40c6415368,
`Engine/Shaders/Shared/VirtualShadowMapDefinitions.h:177–196`:
floor/clamp the biased footprint mip, then mode1 selects even andmode2 odd.

Public `LocalMipMode` is produced into named typed-view flags, consumed by the
common local request/sampling mip helper. DefaultAll retains every mip.
The one public shader case gives nominal mip3 andEven configuration. Native
selects mip2, which holds authored shadow depth; mip3 is authored open space.
Expected visibility0 and all source literals are retained across red/green.

```powershell
dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NativeShadowProjectionTests.SpotSamplingUsesEvenMipLevelsWhenConfigured --results-directory <proof> --logger 'trx;LogFileName=even.trx'
```

Red preserves the prior shader with new input flags ignored:1failed/1executed/
0skipped, expected0/actual1. Green runs the Native mode source:1passed/1executed/
0skipped. Both default rules0violations; necessary Release build0warnings/errors.
D3D12 WARP. Actual rows/logs and old shader/test identities accompany this note.
Temporary source restore copies were removed after use. Broader cases/backend
and final default-scene quality/performance join the unified final batch.

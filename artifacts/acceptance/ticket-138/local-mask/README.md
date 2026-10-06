# Local receiver-mask-off sampling — #138 second slice

Integration base: `6f05fa7148628620f1338539a0fb546a9c6a5cf8`.
Branch: `codex/rg70-ticket-138`.

The fixed Native reference is UE5.8.3 commit
`396c9f059903aed5fec78ecd3d437a40c6415368`,
`Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapProjectionCommon.ush:122–134`.
GitBlob `302b78ccc27d19931b193aa7e4aee0ddcc14ce60`, SHA256
`47628D6CDB7439236231C22634DF30B7D479D9F92BB2E7E719F2AEAB191A2DF0`.
The source is retained outside the repository while Native implementation uses it.

Native local sampling begins at mip zero when the projection's receiver mask is
disabled. Page marking has its own nominal-mip rule in `VirtualShadowMapPageMarking.ush:41–48`.
The shared local sampler now follows this distinction: point and spot sampling
use the mask-aware sampling mip; page requests keep their footprint entry point.

The public Spot test supplies typed view data with the receiver mask disabled,
four resident mips and authored depth. Mip zero contains a depth of .25 in front
of the receiver at .5; each coarser mip contains .75 behind it. Thus Native mip
zero returns shadow visibility0. The older derivative-based sampling selected
a coarse mip and returned visibility1. The assertion observes the public shader
sampler through the existing public RHI compute/readback fixture.

## Actual red and green

```powershell
dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NativeShadowProjectionTests.SpotSamplingUsesMipZeroWhenTheReceiverMaskIsDisabled --results-directory <proof> --logger 'trx;LogFileName=spot-mask-off.trx'
```

- Red on unchanged production9ab7b389 (source identical to integrated6f05):
  **1 executed / 1 failed / 0 skipped**, expected0, actual1.
- Green on the minimal common sampler condition with the same test:
  **1 executed / 1 passed / 0 skipped**, expected0 retained.
- Both default behavior-rule checks report **0 violations**.
- Release test project compilation: **0 warnings / 0 errors**.
- D3D12 WARP, .NET SDK10.0.112/runtime10.0.12, Slang2026.17.1.
  The fixture compiles the shader include from the owned checkout during each run.

Unchanged red/green test working-file SHA256:
`BE2EE2F2DB379DC1BA6C154B89801FE26D31A9C024DDE3A9DD0419055A171439`.
Green shader working-file SHA256:
`06C07357EB1139A3F29B82150560DA67F06170C3A7FA35ABE1A1F65861FF7243`.
Git blobs in this commit define checkout-independent source identity; the hashes
record the actual working bytes used for the tests. Actual outputs and result
rows are retained in `red.{log,trx}` and `green.{log,trx}`; `build.log` records compilation.

This slice validates the Spot sampling behavior. Native nominal camera radius,
centimeter-to-meter conversion, camera calibration, perlight/movement inputs,
conditional depth-of-field inputs, affected Wiki and legacy fixtures continue
as subsequent #138 slices. Parent #70 correctness, quality and performance
acceptance continues with each delivered source and its effective configuration.

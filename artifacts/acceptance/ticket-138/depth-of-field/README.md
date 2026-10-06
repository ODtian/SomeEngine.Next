# Native conditional depth-of-field resolution bias — #138

Fixed396c9f059903aed5fec78ecd3d437a40c6415368 sources:
`DiaphragmDOF/DOFCommon.ush:209–225`,
`VirtualShadowMaps/VirtualShadowMapProjectionCommon.ush:20–38,48–69`;
host `VirtualShadowMapProjection.cpp:386–389` supplies compiled camera CoC values.
Original source/provenance remain outside repo during use (native-dof-manifest.json).

Nullable `CameraDepthOfField` carries focus distance, infinity/min/max circle
radii, depth-blur radius/exponent and shadow bias strength along SceneCamera
codec→RuntimeScene→RuntimeViewFrame→RenderView→the existing shared frame value.
Receiver projection is80B including true view-depth plane and32B camera optics;
request/sampling uniforms are224/144B. Default optics/strength0 follows the DOF
disabled path. User shaders may provide their own CoC to the same conditional
bias helper. Both requests and standard material use the common input.

CoC follows Native focus/infinity radius, signed depth-blur maximum and range.
The bias is min(strength,strength*abs(CoC)/8) for available scene depth. Selector
applies receiver bias before finding the VSM whose own resolution bias is read.

One public shader case:192cm distance gives7.x; focus1/depth2/infinity radius8
gives4px CoC; strength2 givesbias1. First6 then reads index2's bias1 and selects
authored shadow atindex3. Negative scene depth supplieszero receiver bias and
keeps litindex1. Same literals and visibility0/1 are used for red/green.

```powershell
dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NativeShadowProjectionTests.DirectionalSamplingAppliesDepthOfFieldBeforeReadingTheSelectedViewBias --results-directory <proof> --logger 'trx;LogFileName=dof.trx'
```

Red on preserved zero-bias shader scaffold:1failed/1executed/0skipped, expected0/
actual1. Green on final Native conditional/order:1passed/1executed/0skipped,
positive-depth shadow0 andnegative-depth lit1 retained. Both rules0violations;
necessary test/Runtime Release builds0warnings/0errors. D3D12 WARP. Logs/TRX and
old source identities accompany this note. Temporary source copies were removed.
Broader backend/default quality/performance are part of final unified acceptance.

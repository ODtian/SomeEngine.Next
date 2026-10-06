# Native local nominal camera radius — #138

Fixed Native396c9f059903aed5fec78ecd3d437a40c6415368,
`Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapProjectionCommon.ush:96–134`:
camera screen radius=min(1/(viewportXY*projectionXY)), scaled by camera clipW;
the local-light projection turns that world radius into a shadow texel footprint.
Native sampling mip is floor(log2(footprint)+bias), bounded to the available range.

One32-byte `VirtualShadowReceiverProjection` value carries world→camera clipW and
screen radius. Feature computes it once from RenderView and writes both existing
page-demand and material-sampling uniforms. Point/Spot sampler APIs explicitly
receive nominal world radius. Derivative positions describe the receiver plane.
Page requests and sampling share the same local nominal formula; receiver-mask-
off sampling remains mip0. The source uniforms are176B demand/96B sampling.

The one public shader/RHI case supplies camera250px/projection1/depth1=>radius.004,
light scale1/depth1 andvirtual1024=>footprint8.192=>mip3. Mip3 contains authored
shadow depth.25, fine mips contain open depth.99. Receiver depth~.9 therefore
requires visibility0. Tiny geometric neighbors would make the previous rule
choose mip0 and visibility1. The selected test body/literals are unchanged.

```powershell
dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NativeShadowProjectionTests.SpotSamplingUsesTheNominalCameraPixelRadius --results-directory <proof> --logger 'trx;LogFileName=nominal.trx'
```

- Red on the preserved old derivative shader and temporary new scalar-input
  overload forwarding that rule:1failed/1executed/0skipped, expected0/actual1.
  Shader/test working-byte identity is in red-shader.json.
- Green on final explicit-input nominal formula:1passed/1executed/0skipped,
  expected0 retained. Default rules0violations in both runs.
- Necessary Release compilation of test/Feature:0warnings/0errors. D3D12 WARP.

Actual output/TRX and build logs accompany the note. Temporary restore source
copies were removed after use. Existing Spot fixtures now state Native nominal
camera radii; receiver stretch/shear leaves their nominal mip unchanged. Wider
behavior/backend/default-scene acceptance joins the final batch. Remaining #138
conditional receiver depth/DOF inputs and mip modes continue as source slices.

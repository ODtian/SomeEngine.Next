# RGB scalar buffer layout (#152)

The public rendering case writes a StandardMaterial emissive value, renders an authored plane, and reads its linear RGBA16F center pixel. It uses a user shader with the same float4 / float / float / float3 fields as the standard material. The first frame expects red (100,0,0,1); the old implementation produces blue (0,0,100,1). The same case updates the material to green and blue after the fix.

`ClusterMaterialStages.ResolveScalarLayout` now requests `DefaultStructuredBuffer`, matching `MaterialScalarRegion.Load<T>`. Its previous `Default` rule describes a constant buffer. For this DXIL field sequence the float3 begins at byte 32 in constant-buffer data and byte 24 in buffer data. Slang provides the field offsets and sizes; material encoding continues to use those reflected values.

Primary source: [Slang v2026.17.1 layout selection and buffer rules](https://github.com/shader-slang/slang/blob/v2026.17.1/source/slang/slang-type-layout.cpp).

Minimal check: `dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter FullyQualifiedName~UpdatingEmissiveColorsPreservesTheirRgbComponents`.

| Run | Executed | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: |
| Old layout | 1 | 0 | 1 | 0 |
| Buffer layout | 1 | 1 | 0 | 0 |

The necessary Release build completed with zero warnings and errors. Each test invocation ran the repository behavior rules with zero violations. `red.trx`, `green.trx` and the red comparison retain the direct behavior result. The existing Bistro source/material/frame evidence is in `performance-81/final-2a55/Bistro-Emissive-152.md`; the integrated three-scene run also exercises its actual standard material.

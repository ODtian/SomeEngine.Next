# #144: programmable software triangle pixels

The public `EncodedUvValuesProduceTheAuthoredDepthGradientAcrossTheWholePlane` fixture uses a four-vertex plane with authored UV corners (0,0), (1,0), (1,1), (0,1). Its shader returns depth `0.5*u + 0.25*v`. The independent 64×64 image expectation is `0.5*(x+0.5)/64 + 0.25*(1-(y+0.5)/64)`.

Before this slice, Vulkan software raster left 1520 pixels clear in the second triangle's last 32 scanlines; every covered value equalled the expected gradient. The first differing float belongs to pixel (32,32), byte offset 8322. D3D12 hardware/software and Vulkan hardware each passed the same full-image comparison. `red.trx`, `red.log`, and `red-images` retain all four actual outcomes and exact image bytes.

The fixed Native reference is UE 5.8.3 commit `396c9f059903aed5fec78ecd3d437a40c6415368`, `Engine/Shaders/Private/Nanite/NaniteRasterizer.usf:602–747`. Its programmable pixel path retains the three material vertices in the triangle lane and calls `RasterizeTri_Adaptive` there. The engine's extra WaveQueue pixel distribution moved that triangle data across lanes after divergent scanline loops. The final missing batch corresponded to work head 96.

This slice retains opaque user varyings in the triangle lane and directly executes that triangle's pixel rows. Selection uses the shader's compile-time Varying type. Empty varyings keep the existing WaveQueue. Clipping, edge tests, barycentric interpolation, depth handling, and output writes share one implementation. User shaders continue to interpret vertex bytes and material data.

Validation of this slice:

- Public authored-UV SPIR-V shader compilation: 1 executed, 1 passed; `compile.trx` and `compile.log`.
- Vulkan software full-image gradient: 1 executed, 1 passed, 0 failed, 0 skipped; `green.trx` and `green.log`. All 4096 depth values compare byte-for-byte.
- The test commands ran the default behavior rule checks: 0 violations.

Earlier source-based cache and subgroup synchronization slices have bounded evidence in `F:/SomeEngine.AgentWork/rg70-remaining/ticket-144-validation`: vertex cache (one original SampleLevel device loss), triangle cache (one original SampleLevel device loss), subgroup synchronization (one gradient failure with the same first byte difference). The delivered production source uses the successful triangle execution slice.

The next #144 defect gate is the original authored-UV `CasterMaterialChangeTests.InPlaceVertexScalarChangeMatchesIndependentTranslatedCasterInItsFirstFrame` Vulkan software row. Root coordinates its device window and the final combined backend regression.

Base: integration `6e32800210cfec765f6995f3940530b59e2e63ec`. DefaultGame shader content is rebuilt by the unified final acceptance workflow.

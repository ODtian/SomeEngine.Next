# #144: original authored UV SampleLevel

The original public row is
`CasterMaterialChangeTests.InPlaceVertexScalarChangeMatchesIndependentTranslatedCasterInItsFirstFrame(backend: "Vulkan", hardware: false)`.
The user shader's `PatternMask` transports the four original encoded UV corners
through its `readLane` and interpolation methods, then samples
`AlbedoMap.SampleLevel(MaterialSampler, varying.UV, 0)`.

Its original device-loss red is retained in
`artifacts/acceptance/review-fixes/remaining/resume/material-red.trx` and
`material-red.log`, with source checkpoint `18dce4562c538fcc9e35df1a4e7a7b913b17fd32`.
The same custom shader, vertices, UVs, caster/receiver coordinates, clipmap
levels, resolution, software threshold, and full-image assertions are used here.

The unit slice #138 introduced an explicit centimeter scale. This authored
fixture declares `WorldUnitsPerCentimeter = 1` to retain its original projection
density and geometry classification. One fixture setting replaces the previous
implicit factor1; all eight original backend/mode rows share that same input.

After the programmable triangle pixel fix `45ea8f733d21cc8971c73bfbfc4c1dd1a42c0569`,
the selected original Vulkan software row passes: **1 executed, 1 passed,
0 failed, 0 skipped**, 28seconds. It proves the first changed raster image equals
the independently translated ordinary caster's fresh image; the public test
also verifies that the earlier image differs. `green.trx` and `green.log` retain
this actual outcome. The executed source is integration `612ac669898d280d6e036e5b026e3090e3ef46e4`
plus the explicit unit fixture input. The necessary incremental build took
6.75seconds with0warnings/0errors (`build.log`). Default behavior checks reported
0violations.

The delivered source follows current integration `71e9ba27b41bebb8c9cbac939964f7a0503de6aa`.
The root's combined final acceptance runs the complete scalar/alpha backend
set and refreshes the unified DefaultGame shader content and scene evidence.

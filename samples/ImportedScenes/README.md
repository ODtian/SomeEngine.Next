# Sponza and Bistro import samples

These samples download glTF/GLB sources and use DefaultGame's development `import-scene` command to run
`AssetProject.ImportAsync`. The selected scene importer assembles each ECS `WorldAsset`
in the same import as its mesh, material, and texture assets. DefaultGame loads them
through its in-game scene selector. Each scene has its own content directory,
asset manifest, material definitions and imported files.

From the repository root on Windows:

```powershell
./samples/ImportedScenes/build_scenes.ps1 -Download
./samples/ImportedScenes/view_scene.ps1
./tools/Measure-Scenes.ps1
```

The build script uses the published DefaultGame application as its base and
compiles the scene import code through `samples/DefaultGame/DefaultGame.csproj`.
Build the sample's content and publish it first with `tools/Build.ps1`.
The development application is built into `samples/DefaultGame/bin/development`;
published game content is read from `samples/DefaultGame/bin/game/Release/net10.0`.
Downloads and generated applications are excluded from Git. Source checksums
remain beside the downloaded files; import counts and repeat-import results are
written to `Apps/<scene>/scene-import.json`.

DefaultGame uses WASD to move, right mouse button to look, Space/Ctrl vertically,
and Shift for faster movement. It does not animate imported material roughness.
Allow the mesh streaming system to finish loading before judging the picture.

## Source scenes and attribution

- **Sponza:** [Khronos glTF Sample Assets](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/Sponza).
  Crytek/Frank Meinl's scene, with the attribution and terms in the downloaded
  `LICENSE.md`, `README.md` and `Cryengine-Agreement.txt`. One mesh with 25 material
  regions, 262,267 triangles, 25 materials and 69 texture assets.
- **Bistro exterior:** Amazon Lumberyard's
  [ORCA Bistro](https://developer.nvidia.com/orca/amazon-lumberyard-bistro), released
  under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
  This sample uses the [GLB conversion distributed with The Modern Vulkan Cookbook](https://github.com/PacktPublishing/The-Modern-Vulkan-Cookbook/blob/main/source/common/resources/assets/Bistro.glb).
  Git LFS SHA-256: `5cc23d52b95fa1ba8b585d998185904becf8bcaa8f424fd74cf6950addb63deb`.
  1,532 meshes/instances, 1,696 source nodes, 2,828,266 triangles, 131 materials,
  381 embedded PNG images and 382 imported texture objects. The interior is not
  present in this particular GLB conversion.

The original Bistro GLB has 411 invalid tangent vectors across 58 accessors.
`repair_bistro.py` retains the original, normalizes invalid nonzero tangents and
uses a normal-orthogonal fallback for zero tangents on degenerate UVs. The repaired
GLB and an explicit change record go into `Sources/BistroRepaired`. Geometry,
material parameters, textures, nodes and valid tangent values are retained.
The original file's validation rejection was an actual importer test; validation
has not been disabled.

## Import and vertex architecture

GLB's accessors, strides, offsets, integer normalization and sparse patches are
interpreted at import time. The Cluster importer's standard encoder writes the bytes
consumed directly by its shader. No glTF vertex-layout table or accessor query
is added to the renderer or GPU shader.

For these two scenes all material channels use UV0. The Cluster importer group
also produces a geometry Block and an attributes Block containing every shading
stream, including additional UV and color sets. The retained combined Block
continues to supply this sample's shader. All source attributes participate in
the new geometry's welding. Sources whose materials require other
UV sets are explicitly rejected by this sample material integration; supporting
them requires an appropriate import conversion and fixed shader representation.
This is not a general implementation of every glTF material/animation extension.

Mesh source attributes are collected across all primitives, rather than only the
first primitive. Indexed and non-indexed triangles, triangle strips and fans are
converted during import. Tests exercise interleaved accessors, nonzero offsets,
normalized integer UVs, sparse UVs and attributes introduced by a later primitive
through the public material encoder interface.

The vertex-input cases are tested both as external-buffer glTF files and as
binary GLB containers. Accessor metadata is consumed before the encoder receives
semantic values; it is not retained as a runtime vertex-layout description.

The importer accepts a caller-held `PageArena`. This tool uses an 8 GiB decoded
import budget because its atomic import transaction retains the batch's decoded
outputs until commit. It verifies that imported output pages are released after
the transaction. Engine defaults and runtime memory budgets are unchanged.
This does not solve the broader issue that import peak memory grows with the
whole source file's decoded resources.

Static node world transforms and parent-child relationships are written to ECS
entities. At load time, `MeshMaterialsBlock` supplies each mesh's ordered material
bindings. glTF animation is not represented in this static sample WorldAsset.

## Material coverage and current limits

The sample's ordinary user material handles base color, metallic/roughness,
normal maps and normal scale, independent occlusion and strength, emissive maps,
emissive strength and alpha-mask cutoff. Color textures are decoded from sRGB in
the material shader; normal/metallic/roughness/occlusion remain linear. Missing
metallic/roughness maps use white, not an occlusion map's unrelated G/B channels.
Mask shader variants are compiled at import time.

VSM remains enabled, with shadow filtering disabled by default. Zero filter radius
selects a single center-depth comparison and skips blocker search and disk sampling.
Soft-shadow filtering can be selected explicitly through the shadow radius settings.
When selected, its minimum footprint is a 1-texel radius, bounded by the configured
maximum. The blocker-search radius is independent of that footprint.

Remaining display limitations are explicit:

- The current renderer fixes back-face culling. Three double-sided materials in
  each source therefore do not yet have correct back-face visibility.
- Bistro's `Vespa_Odometer_Glass` uses alpha blending. Its color and factors are
  imported, but this opaque/masked sample path does not blend its transparency.
- Texture import currently produces a single mip level. This sample does not
  add filtering/mipmap generation or environment lighting.

These limitations do not count as fully supported glTF material behavior.

## Validation

Bistro also exposed two renderer scaling failures while completing this test:
resource synchronization used insertion sort for arbitrarily large access lists,
and command recording multiplied graph access counts into native descriptor
heap preallocation. Large access lists now use comparison sorting; small lists
retain the insertion path. Descriptor storage grows and is retained by the RHI
according to actual bindings. Graph synchronization ranges are not treated as
descriptor allocation requests. A public GPU readback test checks 96 shared
buffer writers declared in reverse execution order over three frames.

Both sample imports check complete per-primitive material bindings, then repeat
the unchanged import and require the same resource IDs, versions and content
hashes. Sponza produces 95 imported assets; Bistro produces 2,045.

The focused asset tests passed 21 cases; synchronization, persistent-graph and
large-access-order tests passed 55 cases. Both viewers completed 64-frame captures.
Sponza finished at 95/95 resident geometry pages with no outstanding requests.
Bistro finished at 1,312/2,376 pages, with 309 queued and 64 active requests and
zero reported streaming failures. Bistro's visible restaurant is rendered, but
its streaming has not converged and its CPU rendering preparation remains slow.
This capture is not a steady-state performance baseline.

The subsequent frame-only timing run is recorded in `performance.json`. Sponza
averaged 3.069 ms over 192 frames after 320 warmup frames. Bistro was stopped on
request after 126 rendered frames: mean 14.692 seconds, P95 18.589 seconds, still
with 193 queued and 64 active page requests. Its last 30 frames averaged 17.353
seconds. These are elapsed frame intervals, not isolated CPU work or GPU timings;
GPU timings were not obtained in this interrupted measurement.

```powershell
dotnet test tests/SomeEngine.Assets.Tests/SomeEngine.Assets.Tests.csproj -c Release `
  --artifacts-path samples/ImportedScenes/Build `
  --filter 'FullyQualifiedName~GltfVertexInputTests|FullyQualifiedName~GltfSourceImporterTests|FullyQualifiedName~ClusterBuilderTests'
```

Open the F1 panel to select Sponza or Amazon Bistro, or use F3/F4.
F9 measures all three standard scenes; F10 records all three CPU traces.
The old standalone Viewer and scene command-line switches have been removed.
Public engine shaders and pipeline assets are refreshed on every content build.

The timings above are historical observations. Current work and unresolved performance
requirements are recorded in [the unified platform evidence](../../docs/Scene-Performance-2026-09-22.md).

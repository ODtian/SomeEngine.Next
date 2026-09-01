# Cluster Algorithm Reconstruction

The previous renderer is an algorithm reference, not a source tree to transplant. The current
default Cluster renderer is accepted only through SomeEngine.Next-owned asset, RenderWorld,
RenderGraph, Graphics, residency, and publication contracts.

## End-to-end execution contract

Runtime discovers one `RuntimeConfiguration` by manifest type. That asset names the scene,
Cluster renderer, and UI shader by strong GUID references. The Cluster renderer asset contains
semantic operations rather than one field per legacy shader file. Its default operation graph is:

1. BVH traversal and bounded page-fault production;
2. phase-one cull against previous HiZ and phase-two cull against current HiZ;
3. shared raster/deform bin reset, count, reserve, and scatter;
4. deform-cache population with bounded allocation and explicit overflow fallback;
5. software and hardware visibility raster, depth merge, and HiZ construction;
6. material binning and material shading;
7. motion vectors, visibility resolve, temporal resolve, tone map, and presentation.

Shader source paths and concrete entry-point names are authoring data. Runtime selects an
operation by `ClusterShaderOperationRole`, resolves the referenced cooked shader through the
canonical `AssetLoader`, and constructs pipeline layouts from cooked reflection.

## Deform binning is not optional baggage

`RasterDeformBinningReset`, `RasterDeformBinningCount`,
`RasterDeformBinningReserve`, and `RasterDeformBinningScatter` produce both raster batches and
one deform entry per visible cluster, keyed through the material-slot vertex-evaluator field.
`DeformCachePopulate` consumes those deform bins and writes cache offsets and payloads. Cached
software raster, hardware raster, and shading consume the offsets; clusters that do not fit in
the bounded arena retain the explicit overflow sentinel and evaluate vertices directly.

The cache allocator commits bytes with a bounded compare/exchange loop. Its used-byte counter
never exceeds the arena, while separate counters distinguish binned clusters from clusters that
actually populated the cache. Empty phase-two work cannot erase phase-one diagnostic evidence.

The removed standalone `cluster_deform_binning.slang` was not the authority for this path. It was
an unreferenced copy whose key calculation no longer matched the material-slot contract. The
working deform-binning algorithm remains in the semantic raster/deform operation quartet.

## Rejected copy-shaped implementation

The following shapes were direct transplantation and are not part of the accepted design:

- a `ClusterShaders` schema with one property per legacy file and runtime-owned entry-point
  literals;
- a public two-shader `ClusterPipelineSystem` that ran only traversal and single-phase cull as a
  special startup path;
- duplicate raster-only binning entry points beside the working raster/deform chain;
- standalone deform-binning and BVH-patch shader assets with no product referencers;
- material pass records for raster/deform work that the material runtime did not consume,
  including a nonexistent deform-cache-request entry point;
- copied pass resource lists containing bindings that the reflected entry point did not read;
- fixed built-in GUIDs used to locate default scene, renderer, or runtime configuration;
- an unbounded deform-cache append counter and compile-time software-raster tuning constants.

These were removed or replaced by semantic operations, exact reflected resource dependencies,
authoring-time GUID propagation, a bounded allocator, and explicit pipeline options.

## Adopted kernels versus independent rewrites

End-to-end ownership does not make every shader an independent source-level rewrite. A line-based
audit against the reference repository still finds nearly identical implementations. In
particular, `brdf.slang`, `temporal_resolve.slang`, `hiz_build.slang`, `depth_merge.slang`, and
several small utility shaders remain textually identical; `cluster_bin_io.slang`,
`sw_raster.slang`, `cluster_shade_binning.slang`, `cluster_draw.slang`,
`cluster_shade_pipeline.slang`, `cluster_structures.slang`, and `vertex_evaluate.slang` remain
very close.

Those files are accurately described as **adopted reference kernels**, not clean-room or
independently derived implementations. Rewording formulas or renaming locals merely to lower a
similarity score is not a refactor. If provenance, licensing, or independent-implementation
requirements prohibit source carry-over, these kernels remain explicit work: specify the
observable algorithm and ABI as tests, then reimplement without consulting the old source.

For ordinary architectural acceptance, an adopted kernel must satisfy all of the following:

- it is selected by a current semantic operation or a current material target, not by a legacy
  filename inventory;
- its resource effects and managed ABI follow current Graphics/RenderGraph invariants;
- its configured entry points compile from cooked assets for SPIR-V and DXIL where supported;
- tests assert operation roles, ABI, reflection, capacity, and behavior rather than old source
  strings;
- the standard Runtime entry reaches it through manifest dependencies and produces observable
  frame output;
- deleting a legacy-only module or entry point cannot change the product dependency closure.

## Default-scene evidence

The default authored scene contains 1,024 instances and is launched through the ordinary Runtime
entry without scene, renderer, shader, or GUID arguments. A verified eight-frame D3D12 run after
the reconstruction produced resident mesh/page counts of `1/1` and `3/3`, no missing pages or
streaming failures, non-empty visibility and shading, more than 256 output colors, and substantial
screen coverage.

The same run observed 330,752 deform-bin entries. Roughly 21,000 clusters populated the 64 MiB
cache before the bounded arena filled; remaining clusters followed the explicit direct-evaluation
fallback. Exact counts and frame hashes may vary with scheduling and animation, so acceptance is
based on invariants and non-empty ranges rather than a copied legacy baseline.

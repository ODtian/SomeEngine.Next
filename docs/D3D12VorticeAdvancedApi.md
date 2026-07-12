# Direct3D12 / Vortice backend

The Direct3D12 backend is native-only. Constructing `SomeEngine.Graphics.Direct3D12.Device` creates a real Vortice `ID3D12Device`, graphics/compute/copy queues, fences, descriptor storage, command allocators and command lists. Unsupported hosts fail during construction instead of entering a software authoring mode.

There is no Direct3D12-specific Null oracle, delegated validation backend, or runtime fallback to `Graphics.Null`. Cross-platform contract validation belongs to `SomeEngine.Graphics.Null`; native lowering is proved separately by the required Windows/WARP lane.

## Proven production core

The backend owns generational Graphics handles, native resources and placed heaps, CPU descriptor pages, shader-visible descriptor heap rollover/replay, root signatures, raster/compute PSOs, queue submission, fences and deferred retirement. Capability claims remain tied to the checked-in run-0004 continuity ledger and executable tests.

Native query heaps and queue clock calibration are covered by required WARP execution tests.

Unsupported optional capabilities must fail closed. Feature-level 12_0, shader model 6.2, and traditional bind-group binding are the production baseline; bindless, shader model 6.6, direct heap indexing, and Resource Binding Tier 3 are not device admission requirements.

## Advanced capability truth

The following entries deliberately distinguish checkpoint fragments from current supported execution:

- Mesh shader current level: absent; checkpoint evidence: native-call only. The checkpoint called `DispatchMesh` but did not prove tier discovery, a valid mesh PSO, or observable output.
- Variable-rate shading current level: absent; checkpoint evidence: native-call only. The checkpoint called VRS methods without closing tier, legal-state, shading-rate-image, or output semantics.
- DXR current level: absent; checkpoint evidence: native-call only. The checkpoint contained state-object, acceleration-structure, and dispatch fragments, but not trustworthy native prebuild sizing, SBT/capability closure, or hardware output proof.
- Sparse/tiled resources current level: absent in both the checkpoint and current product. There is no reserved-resource or tile-mapping contract.
- Sampler feedback current level: metadata only. The existing `Feedback` bit describes shader effects; it is not a feedback-map, resolve, decode, or streaming implementation.
- Work Graph current level: absent; checkpoint evidence stopped at Null execution. Checkpoint native program creation explicitly threw and therefore was never native execution.

These advanced families are record-only in run 0004. They are not advertised as supported APIs, and their missing semantic layers remain enumerated in `harness/capabilities/graphics-rendergraph-capabilities.v1.json`.

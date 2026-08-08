# RHI Refactor Checkpoint — 2026-08-09

> This is the durable cross-machine continuation record for the destructive RHI refactor. The
> checkpoint is intentionally incomplete: it preserves all current implementation work and the exact
> remaining decisions, but it is not a certification or compliance claim.

## Repository checkpoint

- Repository: `https://github.com/ODtian/SomeEngine.Next.git`
- Branch: `codex/project-checkpoint-20260809`
- Workspace at checkpoint: `F:\SomeEngine.Next`
- Goal: complete the RHI refactor strictly against `wiki/architecture/RHI`, destructively removing
  obsolete APIs. Do not add compatibility overloads, migrations, fallbacks or legacy facades.
- The formal 8192-warmup / 16384-measured vendor performance certification has not been completed and
  must not be reported as Pass.
- No benchmark, testhost or build process was running when this checkpoint was prepared.

## Non-negotiable shader-layout decision

S#/Slang reflection is already sufficient and is the only shader binding authority. The terminal
implementation must not create a `Contract` or another normalized shader-layout model in any layer.

Consequences:

1. `SomeEngine.Graphics/Descriptors/ParameterBindingContract.cs` and every
   `ParameterBindingContract*` use remain deleted.
2. The recently introduced
   `external/SlangShaderSharp/src/Reflection/ParameterBlockLayoutReflection.cs` is also a duplicate
   model and must be deleted. Moving the flattening model into S# does not make it authoritative.
3. Delete `ParameterBindingRangeReflection` and `ParameterBindingElementReflection` together with that
   aggregate helper. Extend S# only if a one-to-one native Slang accessor is genuinely missing; do not
   add another snapshot or semantic namespace.
4. D3D12 pipeline creation reads the existing raw `VariableLayoutReflection`,
   `TypeLayoutReflection`, binding-range, descriptor-range, field-range-offset and subobject-range
   APIs directly and emits only a backend-private root-signature/descriptor artifact.
5. Validation independently reads the same raw S# reflection and may keep only validation-private
   diagnosis state. Direct Pipeline objects never carry validation metadata, and Validation never
   consumes the D3D12 root artifact.
6. The canonical order of `ParameterBlockBindings.Resources` is the target-specific Slang binding
   range order: ascending Slang binding-range index, then ascending bounded array element. The leaf
   field is obtained directly from Slang. Parameter-block/subobject boundaries are not recursively
   flattened across. Unbounded ranges use their explicit bindless path or are rejected; they are not
   represented as a bounded range with an invented count.
7. Register, space and count are exact Slang results. Zero is a valid value. Do not use parent
   fallback, `base + child` reconstruction, DXIL parsing or “zero means missing” inference.
8. Ordinary data uses exact Slang facts: `PushConstant`/`InlineUniformData` becomes D3D12 root
   constants, `ConstantBuffer` follows its reflected binding, and unresolved facts fail pipeline
   creation. Immutable Slang samplers become D3D12 static samplers directly.

This decision closes the real specification hole without creating another contract object: the Wiki
must state the flat span order above, while raw S# reflection remains the implementation authority.

## Current implementation state

The working implementation already includes substantial destructive changes:

- receiver-based backend-neutral RHI, sealed Silk.NET Direct3D12 backend and optional Validation Layer;
- old public RHI paths removed rather than adapted;
- common `ParameterBindingContract` removed;
- normalized future-state suppression based on compatibility/content rather than wrapper identity;
- D3D12 root constants/static-sampler and root-layout work in progress;
- deterministic pipeline-cache compatibility digest, float canonicalization and DXR/Work Graph replay;
- D3D12 borrowed native access, Queue lock, command-list borrow, retention modes and dirty-state handling;
- immutable capability/format/sample-count snapshots and direct capability guards;
- external handle ownership repairs and Validation Dispose cleanup protection;
- terminal typed descriptor-table slots and exact typed-null publication/retirement;
- generated XML lifetime/concurrency/ownership documentation and a mechanical public-type gate;
- `DeviceQueueDesc.NodeIndex` is the Queue construction node selection;
  `CommandContextDesc.NodeIndex` was destructively removed at the end of this session and contexts now
  inherit the selected Queue's private node mask. The final edit updated 83 call sites and still needs
  a clean post-checkpoint build/test run.
- benchmark resume evidence now checks protocol, workload/sample shape, adapter, completion status,
  executable hash and managed payload hash before reusing raw evidence;
- dynamic-PGO and 16-byte transient ordinary-data hot-path work is present; formal certification is
  deliberately frozen.

Previously observed targeted evidence (rerun after the checkpoint before relying on it):

- native access tests: 5/5;
- typed descriptor tests: 7/7;
- capability plus pipeline-cache tests: 16/16;
- XML/NodeIndex contract tests before the final CommandContext edit: 2/2;
- benchmark tooling tests: 48/48;
- relevant project builds had zero warnings/errors before the final NodeIndex edit.

These numbers are regression evidence only; they are not proof that the complete Wiki is closed.

## Remaining high-priority work

Work in this order, preserving the destructive/no-compatibility rule:

1. **Remove the duplicate S# aggregate reflection model.** Rewrite D3D12
   `D3D12ParameterBlockShape` and Validation parameter binding diagnosis to consume raw existing S#
   APIs. Update affected tests to assert raw Slang order and native artifacts, not the deleted wrapper.
2. **Verify the final NodeIndex edit.** Confirm public `NodeIndex` exists only on
   `DeviceQueueDesc` and `WorkGraphEntryPointLayout`, CommandContext derives the Queue node mask, and
   linked-adapter visibility validation uses the executing Queue node.
3. **Native external-object import.** OS shared handles remain caller-closed; native COM pointer
   import separately accepts `NativeObjectOwnership.Borrowed` or `Transferred`. Do not conflate these
   ownership domains. Test actual post-return use, failure paths and release ownership.
4. **Capability completion.** Ensure every direct Mesh/VRS/DXR/WorkGraph/SamplerFeedback/Sparse entry
   checks the immutable Device snapshot before native calls, with no repeated hot
   `CheckFeatureSupport`. Ensure Validation checks the new pipeline-ray-tracing prerequisite.
5. **Descriptor and binding closure.** Retain exact typed nulls for CBV, buffer/texture SRV/UAV,
   acceleration structure and sampler publication/retirement. Persistent and transient bindings must
   use raw S# order and content equality without another layout model.
6. **Lifetime/XML closure.** Keep every exported RHI type's generated XML declaration for
   thread-safety, ownership and post-Dispose state; add member-specific exceptions only where the
   member differs from the type default. Throwing diagnostic sinks must never interrupt Dispose.
7. **Pipeline cache closure.** Recheck golden/corrupt/merge/cross-run/family replay coverage and full
   compatibility invalidation (Slang version, backend ABI, root compiler, schema, immutable
   capability/limit/format snapshot).
8. **Ordinary product verification.** Build and test `SomeEngine.slnx`, then run focused D3D12 WARP,
   Validation, binding, lifetime and RenderGraph regression tests. Keep independent legacy mapper and
   RenderGraph migration bugs classified separately from RHI design defects.
9. **Performance diagnostics only after correctness.** Fast diagnostics may use the three relevant
   draw workloads, 256–512 warmup frames and 512–1024 samples. They are never Gate evidence. Do not
   parallelize GPU workers and do not parse huge raw JSON through PowerShell `ConvertFrom-Json`.
10. **Formal certification last.** Only after every explicit Wiki item and ordinary regression is
    closed, execute one fixed formal protocol: six workloads, 8192 warmup frames, 16384 measured
    frames, five independent processes per variant. Incomplete/raw diagnostic files are never Pass.

## Known bug classification

Keep these independent during continuation:

- legacy compute `ShaderResource` state: D3D12 legacy mapper bug;
- copy-queue enhanced layout/common mapping: D3D12 mapper bug;
- equal-state cross-Queue handoff, Begin-thread/encode-thread mismatch and
  `GraphTextureAspect + 1`: RenderGraph migration/implementation bugs;
- none of those justify compatibility paths in the new RHI.

## Cross-machine continuation procedure

On the new machine:

```powershell
git clone https://github.com/ODtian/SomeEngine.Next.git
cd SomeEngine.Next
git switch codex/project-checkpoint-20260809
git pull --ff-only origin codex/project-checkpoint-20260809
git status --short
```

Then instruct the new Codex task to read this file and every RHI Wiki contract before modifying code.
Do not resume a benchmark first. Start by removing the duplicate `ParameterBlockLayoutReflection`
model and directly consuming raw S# reflection.


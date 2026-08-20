# RHI Refactor Checkpoint

This filename is retained as a historical navigation target. Its content was refreshed on
2026-08-17 and describes the current model rather than the superseded August 9 proposal.

## Current converged model

- `IGraphicsBackend` is the only product receiver.
- Public resources are abstract identities with backend-private sealed implementations and safe
  provenance checks.
- Slang/S# live reflection and component code are the only shader authority.
- Descriptor identity is `DescriptorTable` plus numeric slot value; table slots are fixed.
- Parameter binding uses `VariableLayoutReflection`, `ResourceBinding` and exact ordinary bytes.
- Synchronization uses real Pipeline scopes, real memory accesses, portable Texture layouts and
  `BarrierPhase` for split transitions.
- Public wrapper lifetime is Active/Disposed; accepted work is retained internally through Queue
  completion.
- Optional functionality is discovered through typed Device capabilities.
- Pipeline creation is synchronous or asynchronous on the same `IGraphicsBackend`; Task completion
  returns a ready Pipeline.
- D3D12 owns the resource allocator, Pipeline worker queue, DRED and presentation telemetry privately.
- Validation is an ordinary non-generic wrapper and independently consumes raw S# facts.

## Current evidence

Debug and Release builds are green. Core Graphics, benchmark-gate and Render Graph tests are green.
The ordinary D3D12/WARP batch and isolated Device Lost, presentation and destructive groups are green.
Current performance measurements are recorded in [[Performance-Evidence-2026-08-17]].

## Not yet a final production-completion claim

A final 3A claim still requires clean-checkout/submodule reproducibility, classified temporary files,
current native-runner rebuild, formal vendor certification, unavailable AMD/multi-node hardware
reporting and the complete hardware feature matrix. Historical completion statements do not override
those requirements.

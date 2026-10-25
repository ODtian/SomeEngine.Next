# RHI-DSC-004 — Backend-private heap generations 

Shader-visible heap growth, copying and generation switching are backend details. D3D12 may keep CPU
staging descriptors, allocate another shader-visible heap generation and copy stable table slots into
it before one no-throw commit.

Public code observes only stable table indices and explicit `PublishDescriptors`. Heap generation,
fragmentation, temporary descriptor ranges and completion retirement do not become public address
spaces or compatibility values.

The Direct3D 12 command descriptor arena retains the resource/sampler published-table prefixes it has
observed across recordings. Its first local parameter binding reserves and fills those prefixes from
the current published generation before allocating local descriptors. Repeated binding sequences use
the same heaps for their later unbounded tables. The existing capacity check retains sufficient heaps
and grows them when required;
recorded commands retain the actual heaps and descriptor generation through their execution lifetime.
The implementation is in
[`ResetDescriptorArenaState` and `EnsureDescriptorArenaReady`](../../../../src/SomeEngine.Graphics.Direct3D12/CommandBindings.cs).
^rhi-dsc-004


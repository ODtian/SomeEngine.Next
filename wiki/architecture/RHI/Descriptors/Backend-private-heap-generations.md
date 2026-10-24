# RHI-DSC-004 — Backend-private heap generations 

Shader-visible heap growth, copying and generation switching are backend details. D3D12 may keep CPU
staging descriptors, allocate another shader-visible heap generation and copy stable table slots into
it before one no-throw commit.

Public code observes only stable table indices and explicit `PublishDescriptors`. Heap generation,
fragmentation, temporary descriptor ranges and completion retirement do not become public address
spaces or compatibility values.
^rhi-dsc-004


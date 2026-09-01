# RHI-QUE-007 — Canonical initial synchronization facts 

Every Buffer and Texture exposes immutable initial synchronization facts established by its creation
path: initial `PipelineSync`, `ResourceAccess`, and for Textures an initial `TextureLayout`. A
swapchain image exposes the corresponding facts for its current acquisition sequence.

These facts are the starting point for explicit barriers. They do not represent hidden tracked global
state shared between independent command recordings. Validation may track submitted facts to diagnose
incorrect use, while the direct backend encodes exactly the caller-provided barriers.
^rhi-que-007


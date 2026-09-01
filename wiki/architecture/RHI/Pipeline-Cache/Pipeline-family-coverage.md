# RHI-CACHE-003 — Pipeline-family coverage 

The cache covers Graphics, Compute, Mesh, Ray Tracing and Work Graph Pipeline families. Every key
contains exact linked/specialized Slang code identity, selected entry/export/node identity, static
sampler state, node mask, serialized native root-signature bytes and all family-specific public state.
Labels, object addresses, descriptor contents, command values and backend-private lookup indices are
excluded.

A cache hit still creates a new caller-owned Pipeline and new physical Slang/native ownership. D3D12
classic PSOs may consume cached PSO data. State-object families store and replay the exact data their
native path supports; the public contract does not pretend every family is an
`ID3D12PipelineLibrary` entry.
^rhi-cache-003


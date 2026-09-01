# RHI-CAP-002 — Sparse resources and Queue mapping updates 

`SparseResources` reports tile size, supported sparse format sets and mapping limits. Sparse Buffer or
Texture creation produces ordinary Resource identities with immutable sparse metadata.

Mapping and mapping-copy updates are explicit Queue operations. Each batch validates resource/Heap
provenance, tile coordinates, packed-mip rules, bounds and overlaps before the native update.
Successful mapping creates a backend-private immutable mapping generation that retains exactly the
Heaps used by that generation. Recorded or submitted work retains the generation it captured until
completion. A later update never rewrites an older accepted generation.
^rhi-cap-002


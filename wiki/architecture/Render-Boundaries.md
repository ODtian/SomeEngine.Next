# Render Boundaries

Compatibility entry for rendering architecture. Current design is split into atomic pages under [[architecture/Rendering]].

## Main boundaries
- [[architecture/RHI/Index|RHI]] — backend-neutral Graphics execution and native backends
- [[architecture/render-graph/Retained-Graph|RenderGraph]] — retained scheduling and resource dependency
- [[architecture/rendering/RenderDevice|RenderDevice]] — per-device render-side asset realization and retired Graphics resources
- [[architecture/rendering/GameWorld-and-RenderWorld|GameWorld and RenderWorld]] — data handoff boundary
- [[architecture/cluster/Cluster-Scope|Cluster]] — triangle virtual geometry and programmable raster capability
- [[architecture/features/Lighting|Lighting]] / [[architecture/features/Virtual-Shadow-Maps|VSM]] / [[architecture/features/Motion|Motion]] — independent rendering features

## History
- [[history/Rendering/Legacy-Render-Boundaries|Previous monolithic render boundaries]]

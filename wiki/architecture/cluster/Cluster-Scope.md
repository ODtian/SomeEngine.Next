# Cluster Scope

## Definition
Cluster 是 triangle virtual geometry 与 programmable raster 的可替换 pipeline template，不是引擎默认 renderer，也不是唯一 renderer。

## Includes
- triangle virtual geometry representation
- LOD/BVH traversal
- geometry page streaming
- stage grouping
- Deform cache
- software/hardware raster
- tessellation/dicing integration

## Excludes
Lighting、shadow/VSM、motion、streaming feedback consumer、temporal 与 presentation 位于各自 feature。ray tracing bridge、debug 与 translucency 的旧实现已于 2026-10-04 删除，历史边界见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

## Relations
[[architecture/features/Virtual-Shadow-Maps]] · [[architecture/features/Lighting]] · [[Raster-Execution]]
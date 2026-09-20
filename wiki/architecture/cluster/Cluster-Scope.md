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
Lighting、shadow/VSM、motion、ray tracing bridge、debug、streaming feedback consumer、translucency composition、temporal 与 presentation 都是独立 feature。

## Relations
[[architecture/features/Virtual-Shadow-Maps]] · [[architecture/features/Lighting]] · [[Raster-Execution]]
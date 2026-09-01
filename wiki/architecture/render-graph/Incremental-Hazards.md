# Incremental Hazards

## Definition
资源访问发生结构变化时，只重建该资源相关的 access timeline 与由它产生的 hazard edges。

## Invariants
- 无关资源不重新分析。
- BufferRange 与 Texture subresource 是 hazard 粒度的一部分。
- 删除旧 access 时同时删除只由该 access 产生的旧 hazard edge。

## Relations
[[Resource-Access]] · [[Incremental-Reconciliation]] · [[Incremental-Scheduling]]
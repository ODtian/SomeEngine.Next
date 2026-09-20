# Graph Versioning

## Definition
每个 frame 在开始时取得一版完整图状态，并在该 frame 生命周期中只使用这一版。

## Invariants
- 结构编辑可以与正在执行的旧 frame 并行进行。
- commit 后的新结构只影响未来 frame。
- 旧 frame 继续使用旧结构和旧 graph-owned Graphics 资源。
- 被替换资源在所有仍引用旧版本的提交完成后才能销毁。

## Relations
[[Graph-Identity]] · [[Range-Completions]] · [[Resource-Retirement]]
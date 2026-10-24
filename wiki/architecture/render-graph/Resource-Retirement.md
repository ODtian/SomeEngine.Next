# Resource Retirement

## Definition
被图替换或删除的 Graphics 资源在所有已经提交且仍可能访问它的工作结束后才能释放。

## Invariants
- 释放依据真实 queue completion，而不是 frame 编号猜测。
- 资源替换不要求 CPU 等待 GPU 空闲。
- future frame 已经切换到新资源后，旧资源才能进入等待释放集合。

## Relations
[[Graph-Versioning]] · [[Range-Completions]]
# Range Completions

## Definition
给定 Buffer 与 BufferRange，可以取得当前已经提交且仍可能访问该范围的 queue completions；每条 queue 至多保留一个需要等待的最大值。

## Invariants
- 不把不同 queue 的整数 completion 值互相比较。
- API 只报告已经提交的访问事实，不判断业务数据是否可以退休。
- 调用者必须先保证未来 frame 不再声明该范围。
- 查询不执行 CPU wait。

## Uses
Cluster page range 复用、Texture backing 回收、descriptor 回收和其它长期资源替换。

## Relations
[[architecture/RHI/Queue/Completion-value-domain|Completion Value Domain]] · [[architecture/cluster-streaming/Page-Eviction]]
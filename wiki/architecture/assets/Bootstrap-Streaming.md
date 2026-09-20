# Bootstrap Streaming

## Definition
任何渐进细化内容必须先有一个足以产生有效结果的常驻 Bootstrap，后续数据只提高细节。

## Invariants
- 对象只有在 Bootstrap 可用后才视为加载成功。
- Bootstrap 在对象有效期间不可被普通细节驱逐策略移除。
- refinement 失败时保留当前有效表示，不切换到 correctness fallback。
- 正常首用不得因等待细节数据而同步阻塞 I/O。

## Relations
[[Texture-Bootstrap]] · [[architecture/cluster-streaming/Page-Heap]]
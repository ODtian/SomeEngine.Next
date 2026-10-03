# GameWorld and RenderWorld

## Definition
GameWorld 承担游戏逻辑，RenderWorld 只保存渲染所需数据；二者通过 extraction 交付变化。

## Invariants
- renderer 不直接依赖 GameWorld 的活跃可变状态。
- RenderWorld 可以使用已加载的 Mesh、Material、Texture 等对象引用。
- 两个 world 的分离用于允许 CPU 工作重叠，而不是建立额外复制层本身。

## Relations
[[Extraction]] · [[Frame-CPU-Overlap]] · [[Instance-Storage]]

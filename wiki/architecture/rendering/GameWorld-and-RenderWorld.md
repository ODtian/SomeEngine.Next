# GameWorld and RenderWorld

## Definition
GameWorld 承担游戏逻辑，RenderWorld 只保存渲染所需数据；二者通过 extraction 交付变化。

## Invariants
- renderer 不直接依赖 GameWorld 的活跃可变状态。
- RenderWorld 可以使用已加载的 Mesh、Material、Texture 等对象引用。
- 两个 world 的分离用于允许 CPU 工作重叠，而不是建立额外复制层本身。

## Relations
[[Extraction]] · [[Frame-CPU-Overlap]]

## 场景切换

`ApplicationRenderer` 随 Device 创建，持有 RenderWorld、提取与准备系统、RenderGraph、ShaderLibrary、RenderPipelines 和各渲染 feature。`ApplicationSceneContent` 持有一个场景的 GameWorld、文件访问、mesh、材质与游戏系统。

`ApplicationScene.LoadScene(directory)` 发起后台内容加载，当前场景继续执行。内容就绪后，提取系统切换 GameWorld 来源，将旧镜像作为删除、新来源作为新增交给下一次准备。场景的阴影、透明预算和贴花索引设置通过各 feature 的 `SetSettings` 应用，TAA 设置随帧传入 TemporalFeature，帧序号在同一个 renderer 上持续推进。旧内容通过 `RenderGraph.RetireAfterSubmittedFrames` 释放，设备选项变化通过 `Reload` 重建 renderer。

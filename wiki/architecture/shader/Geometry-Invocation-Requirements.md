# Geometry Invocation Requirements

## Definition

几何实现可能被调用零次、一次或多次；multi-view、缓存、硬件/软件 raster 路径与调度方式都可以改变调用次数和顺序。

## Requirements

- 实现只依赖显式输入。隐藏调用次数、隐式可变状态或未声明状态不构成引擎保证。
- 引擎不建立 shader parser、purity 类型系统、可写资源分析器或重复调用证明器。
- 当前入口实际使用的资源由 Slang `IMetadata` 与直接反射给出，并进入 RenderGraph 的访问、同步和生命周期描述。
- 需要显式顺序的写操作应成为独立 graph pass，其输出再作为后续输入。
- 可写资源本身不会触发额外拒绝；这里约束用户实现的行为，不要求框架证明纯度。

## Relations

[[Shader-Resource-Effects]] · [[architecture/render-graph/Shader-Resource-Closure]]

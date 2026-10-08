# Resource Access

## Definition
RenderGraph 的同步与生命周期事实来自 pass 对真实 Graphics 资源及其范围的 Read、Write、ReadWrite 声明。

## Invariants
- descriptor 数值不是依赖关系。
- parameter binding 可以同时完成绑定和 graph access 声明。
- persistent pass 可逐 frame 增加普通 resource access。
- descriptor 内元素的访问通过普通 resource access 声明表达；元素范围变化属于帧数据，传统绑定与图结构保持原有形式。
- `SetBufferRanges` 将缓冲的当帧范围集合应用到使用该缓冲的读取 descriptor，依赖分析、同步和范围完成查询共用这些普通访问行。
- `DisjointWrites` 声明部分 UAV 写触及的字节或像素与同批其它非顺序写的访问分离。同队列写合并同步，后续读取等待所有写入者；跨队列访问继续遵循资源交接规则。内容定义状态沿初始化或显式结果声明维护。
- Cluster 的材质接收面、材质表面数据和运动输出通过各 bin 的唯一像素及数据索引使用该声明。用户材质的颜色输出遵循它原有的访问顺序。

## Failure

当 Buffer、Texture、Query 或 RayTracingShaderTable 的读取范围包含未定义内容时，帧执行抛出公共 `UndefinedResourceReadException`。调用方通过异常类型区分资源内容失败，诊断消息提供 pass 与资源上下文。

## Relations
[[Shader-Resource-Closure]] · [[architecture/material/MaterialBindless]] · [[architecture/cluster-streaming/Resident-Ranges]]

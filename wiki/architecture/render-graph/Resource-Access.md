# Resource Access

## Definition
RenderGraph 的同步与生命周期事实来自 pass 对真实 Graphics 资源及其范围的 Read、Write、ReadWrite 声明。

## Invariants
- descriptor 数值不是依赖关系。
- parameter binding 可以同时完成绑定和 graph access 声明。
- persistent pass 可逐 frame 增加普通 resource access。
- descriptor 内元素的访问通过普通 resource access 声明表达；元素范围变化属于帧数据，传统绑定与图结构保持原有形式。
- `SetBufferRanges` 将缓冲的当帧范围集合应用到使用该缓冲的读取 descriptor，依赖分析、同步和范围完成查询共用这些普通访问行。

## Relations
[[Shader-Resource-Closure]] · [[architecture/material/MaterialBindless]] · [[architecture/cluster-streaming/Resident-Ranges]]

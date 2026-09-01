# Resource Access

## Definition
RenderGraph 的同步与生命周期事实来自 pass 对真实 Graphics 资源及其范围的 Read、Write、ReadWrite 声明。

## Invariants
- descriptor 数值不是依赖关系。
- parameter binding 可以同时完成绑定和 graph access 声明。
- persistent pass 可逐 frame 增加普通 resource access。
- 不建立单独的 bindless access-set 数据通路。

## Relations
[[Shader-Resource-Closure]] · [[architecture/material/MaterialBindless]] · [[architecture/cluster-streaming/Resident-Ranges]]
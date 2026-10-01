# RHI-DSC-005 — Slang-derived binding and direct native placement

驻留代码与当前直接 Slang 反射定义资源顺序、parameter-object identity、ordinary data、static samplers 和其他 binding 事实。

D3D12/Vulkan 在 pipeline 冷路径直接遍历 `VariableLayoutReflection` / `TypeLayoutReflection`，建立只属于该原生 pipeline 的 placement。command 热路径只消费已计算的 slot arrays/native destinations，不遍历反射、不解析名字、不计算 register/space。

后端派生表与原生对象同生命周期，既不公开为通用 Shader 查询模型，也不写入 Asset。Slang 绑定缺口应在官方绑定层解决，不能在 RHI 复制一棵规范化布局树作为第二来源。

参见 [[architecture/shader/Shader-Block-Residency-Refactor]]。
^rhi-dsc-005

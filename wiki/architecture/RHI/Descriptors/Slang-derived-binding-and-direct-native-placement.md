# RHI-DSC-005 — Slang-derived binding and direct native placement

Cooked shader code 与 Slang# detached reflection/layout 定义资源顺序、parameter-object identity、ordinary data、static samplers 与其它 shader binding 事实。

D3D12/Vulkan 在 pipeline cold path 直接从这些 Slang# 类型建立 backend-private native placement。command hot path 只消费预先计算的 slot arrays/native destinations，不遍历 reflection、不解析名字、不计算 register/space、不 hash string。

如果 Slang# 缺少必要事实，扩展 Slang# fork；RHI 不建立 normalized shader layout 作为第二真相源。

参见 [[architecture/shader/Detached-Reflection]]。
^rhi-dsc-005

# RHI-CORE-004 — Slang/S# is the sole shader authority

Slang 与 Slang# 是 shader type/layout/reflection/specialization 事实的唯一来源。

Shader import/cook 阶段完成 source load、composition、specialization、link、entry lookup、reflection 与 backend code generation。资产保存 backend code 与可脱离 compiler/session 存活的 Slang# reflection/layout 数据。

Graphics pipeline creation 直接消费这些 Slang# 类型和 cooked code，不在 shipping 进程重新读取 source、重新 link 或依赖 native reflection handle。

Slang# 已覆盖的事实直接使用；缺失事实扩展 `external/SlangShaderSharp` fork。RHI 不复制平行 shader tree/register map，也不从名字、DXIL/SPIR-V 解析或 fallback identity 重建 Slang 已有语义。

参见 [[architecture/shader/Slang-Authority]] · [[architecture/shader/Detached-Reflection]]。
^rhi-core-004

# Slang Authority

## Definition
Slang 与 Slang# 是 shader language、reflection、specialization 与 entry metadata 的唯一语义来源。

## Invariants
- Slang# 已覆盖的类型直接使用，不在 SomeEngine 中复制平行类型体系。
- 缺失能力修改 `external/SlangShaderSharp` fork，而不是在 Graphics/Render 上层补一套镜像结构。
- Graphics backend 不从名字、DXIL/SPIR-V 反推 Slang 已经能提供的事实。

## Relations
[[Shader-Build]] · [[Detached-Reflection]]
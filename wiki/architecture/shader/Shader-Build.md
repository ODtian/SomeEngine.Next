# Shader Build

## Definition
source load、compose、specialize、link、entry lookup、reflection 与 backend code generation 全部发生在 import/cook/build 阶段。

## Invariants
- shipping render path 不读取 `.slang` source。
- pipeline creation 不进行 source compilation 或动态 link。
- 最终资产保存 backend code、Slang# detached reflection 与 entry metadata。
- 参数名字在 build 阶段解析，热路径使用已确定绑定位置。

## Relations
[[Slang-Authority]] · [[Shader-Composition]] · [[Detached-Reflection]]
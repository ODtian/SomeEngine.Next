# Detached Reflection

## Definition
Slang# fork 提供可脱离 compiler/session native handle 生命周期保存的 reflection/layout 数据，资产与 Graphics 直接使用这些 Slang# 类型。

## Required facts
- descriptor ranges / set-space / count
- stage usage
- ordinary data offsets and sizes
- parameter-block layout
- push constants
- nested layouts
- static samplers
- resource kinds and image/view information
- entry relations and resource effects

## Invariants
SomeEngine 不建立第二套 shader layout DTO；如果 Slang# 表达能力不够，扩展 fork。

## Relations
[[Slang-Authority]] · [[Shader-Build]] · [[architecture/RHI/Core/Slang-S-is-the-sole-shader-authority]]
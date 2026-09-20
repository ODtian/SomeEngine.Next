# Material

## Definition
Material 是普通强类型业务对象。不同具体 Material 类型可以拥有完全不同的字段、数量和资源类型。

## Invariants
- 不存在全引擎统一 Material stride。
- 不存在全引擎统一 Material data struct。
- Material core 不包含 bindless descriptor 或 Graphics view。
- Standard 只是一个内置具体 Material，不定义 Material core ABI。

## Relations
[[Material-Slots]] · [[Stage-Compatibility]] · [[MaterialBindless]]
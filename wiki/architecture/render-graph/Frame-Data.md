# Frame Data

## Definition
Frame data 是在既有图结构中变化的值和绑定。

## Includes
- camera / view 数据
- constants
- external resource 的实际绑定
- dynamic buffer/texture ranges
- material values
- instance values
- frame-local resource accesses

## Invariants
更新这些数据不得改变 graph identity，也不得触发结构重建。

## Relations
[[Retained-Graph]] · [[Structural-Changes]] · [[Resource-Access]]
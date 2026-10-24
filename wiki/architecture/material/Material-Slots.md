# Material Slots

## Definition
具体 Material 类型的每个字段在该类型内部对应固定 slot；slot 只表示该 Material 内一个位置当前保存的值。

## Invariants
- slot 可保存 Texture、Sampler、float、vector 等业务值。
- slot 不规定之后用传统 binding、descriptor table、instance data 或其它方式送入 shader。
- 不同 Material 的 slot 数量、顺序和类型完全独立。
- dictionary/字符串形式只用于 editor/import/script，并在进入热路径前解析成具体类型的 slot。

## Relations
[[Material]] · [[MaterialBindless]]
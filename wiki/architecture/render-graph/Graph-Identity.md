# Graph Identity

## Definition
Graph ID 表示图中的节点身份，不表示底层 Graphics 对象身份，也不表示资产身份。

## Invariants
- 未删除对象的 ID 跨结构提交保持不变。
- 只有该对象本身删除后 ID 才失效。
- 同一个 Graphics 资源可以被多个 graph resource ID 引用。
- external resource 可以在不改变 graph resource ID 的情况下重新绑定。

## Relations
[[Graph-Versioning]] · [[Resource-Access]]
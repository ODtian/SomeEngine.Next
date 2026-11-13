# Graph Identity

## Definition
Graph ID 表示图中的节点身份，不表示底层 Graphics 对象身份，也不表示资产身份。

## Invariants
- 未删除对象的 ID 跨结构提交保持不变。
- 只有该对象本身删除后 ID 才失效。
- 同一个 Graphics 资源重复注册时返回已存在的 graph resource ID。
- 替换外部 Graphics 对象时，调用方注册新对象并显式修改使用它的 pass。
- 图创建的持久资源通过描述修改更换底层分配，graph resource ID 保持连续。

## Relations
[[Graph-Versioning]] · [[Resource-Access]]

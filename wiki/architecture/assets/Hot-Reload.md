# Hot Reload

## Definition
Reload 在后台构造完整替换内容，并在明确更新边界写入原长期对象；失败时继续使用旧有效内容。

## Invariants
- 后台 I/O、解压和数据检查不逐字段修改 renderer 正在读取的对象。
- 成功时原对象引用保持不变并增加 revision。
- 失败不破坏当前有效内容。
- 不在 Material/Texture getter、setter 或普通 render 访问路径加入 reader 计数、等待或对象锁来协调 reload。
- 边界内部可以使用完成交付和资源生命周期所必需的同步，但它不成为 renderer 的逐访问协议。

## Relations
[[Asset-Update-Boundary]] · [[Texture-Device-Realization]]
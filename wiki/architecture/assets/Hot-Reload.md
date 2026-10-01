# Hot Reload

## Definition
Reload 在后台构造完整替换内容，再在消费方的明确更新边界发布。发布方式由使用对象的业务语义决定；切换前失败时继续使用旧有效内容。

## Invariants
- 后台 I/O、解压和数据检查不逐字段修改 renderer 正在读取的对象。
- `Shader` 等不可变对象创建完整新实例，由已有消费方交换外层引用；已开始的异步消费者继续持有旧实例。Shader 不提供原地更新或 Dispose。
- Material、Texture、Mesh 在其业务需要保留可变身份时，通过各自更新 API 切换完整内容，再发布新 revision。
- 切换前失败不改变当前内容；切换后的通知或旧资源清理失败不得破坏已经发布的新内容。
- 不在 Material/Texture getter、setter 或普通 render 访问路径加入 reader 计数、等待或对象锁来协调 reload。
- 边界内部可以使用完成交付和资源生命周期所必需的同步，但它不成为 renderer 的逐访问协议。

## Relations
[[Asset-Update-Boundary]] · [[Texture-Device-Realization]]

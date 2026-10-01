# Asset Update Boundary

## Definition
后台完成的资产变化只能在消费方的明确交付边界发布。不可变对象交换外层引用；确有可变身份的对象通过自身更新 API 切换内容。

## Invariants
- 应用变化时 renderer 不并发读取该对象的可变加载字段。
- `Shader` 等不可变对象在完整创建后交换引用；已开始的工作继续使用其捕获的旧对象。
- 保留可变身份的 Material、Texture、Mesh 在内容切换完成后，才把新 revision 暴露给后续 render prepare。
- render frame 在交付后使用已经捕获的数据或 device realization，不跨线程反复读取可变资产字段。
- 普通 Material/Texture 读取不为 reload 做逐访问 reader 登记、等待或阻塞；所需同步只存在于交付边界和资源生命周期处理中。

## Relations
[[Hot-Reload]] · [[architecture/rendering/Extraction]] · [[architecture/rendering/Frame-CPU-Overlap]]

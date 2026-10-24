# Asset Update Boundary

## Definition
后台完成的资产变化只能在消费方的明确交付边界发布。不可变对象交换外层引用；确有可变身份的对象通过自身更新 API 切换内容。

## Invariants
- 应用变化时 renderer 不并发读取该对象的可变加载字段。
- `Shader` 等不可变对象在完整创建后交换引用；已开始的工作继续使用其捕获的旧对象。
- 保留可变身份的 Material、Texture、Mesh 在内容切换完成后，才把新 revision 暴露给后续 render prepare。
- render frame 在交付后使用已经捕获的数据或 device realization，不跨线程反复读取可变资产字段。
- 普通 Material/Texture 读取不为 reload 做逐访问 reader 登记、等待或阻塞；所需同步只存在于交付边界和资源生命周期处理中。

## 当前更新入口

`MaterialAsset.UpdateAsync<TMaterial>(material, files, shaders, textures)` 完整解析资产内容，按强类型字典选择实际 Shader、Texture，再通过 MaterialGen 生成的 `IMaterial<TMaterial>.Update` 写入现有具体材料。生成代码直接复制全部具体字段，保留 MaterialId，并在字段就绪后将内容 Revision 推进一次。Reset 后重新创建的字段来自父模板与类字段初始化。

业务通过具体 `ShaderAsset.CreateAsync` 创建新的共同 Shader，并在下一次材质 `UpdateAsync<TMaterial>` 提供该实际引用。`TextureBlock.UpdateAsync` 从完整纹理内容创建替换 Texture，交给已有 Texture 的 `UpdateAsync`；完成结果仍是同一个业务 Texture。

业务启动纹理更新后持续推进渲染帧。Texture 的 `Versions` 包含当前整份内容 Revision、PixelsRevision，以及设备上传采用的替换内容 Revision。排队整份替换先改变替换内容版本，各设备完成后才提交当前内容与字段。新增像素推进 PixelsRevision，并保留整份内容 Revision 与现有 GPU 分配。

RenderExtractionSystems 在 Game world 的同一次读取中收集实际材料、网格和纹理版本，并在 Rendering world 的同一次应用中发布。渲染消费方通过 RenderWorld 的已应用版本决定缓存与 GPU 资源更新。具体资源状态由资源对象持有，GPU 分配、视图及旧帧完成由 RenderDevice 和管线持有。

## Relations
[[Hot-Reload]] · [[architecture/rendering/Extraction]] · [[architecture/rendering/Frame-CPU-Overlap]]

# Texture Device Realization

## Definition
长期 Texture 在某个 RenderDevice 上对应一份私有 Graphics.Texture 与相关视图状态。

## Invariants
- 映射维度是 Texture identity × Graphics.Device。
- 通用 realization 不属于 MaterialBindless 或任何可选材质扩展。
- 创建 backing、上传 Bootstrap、上传后续 mip、重建 backing 和旧资源回收都是明确步骤。
- render hot path 不同步等待资产 I/O。
- format/dimension 改变时先建立新 backing，再切换使用，旧 backing 等相关 queue completions 结束后释放。

## 实际引用与设备退出

RenderDevice 的长期集合保存 GPU 记录。记录通过短弱引用关联业务 Texture；实际帧登记持有正在使用的 Texture 至该帧 GPU 完成，上传事务持有像素来源至图帧收尾，完整更新登记持有原 Texture 与替换内容至全部参与设备完成、失败、取消或退出。各自结束时清理对应临时引用。

TextureDevices 的终结路径只提交释放请求。RenderDevice.CollectCompleted 在渲染线程处理队列；记录先等待已登记帧结束，再复用 RenderDevice 的已提交队列完成边界回收 Graphics.Texture 及其视图。

更新登记、终结入队与设备关闭共享设备记录的同步范围。设备退出先原子地使全部记录失效并接收已入队请求，再在锁外撤掉 Texture 关联、推进完整更新任务并释放原生资源。后续查询及更新收集同样清理失效关联。

完整替换、分配更换与设备退出推进 TextureVersions.Allocation。Game world 的既有提取将此版本与实际 Texture 一起交给 Rendering world，消费方据此重新取得 RHI Texture、SRV、UAV，并显式修改持久图资源关联。旧分配继续由先前帧的 GPU 完成边界保护。

## Relations
[[architecture/rendering/RenderDevice]] · [[Texture-View-Cache]] · [[architecture/render-graph/Range-Completions]]

纹理 tile 的就绪像素页在重排期间保持引用，按后端复制布局直接写入 RenderGraph
当帧映射上传区。每个 tile 写入完成后释放 CPU 像素页引用，帧持有上传区至 GPU
完成。行间填充和末尾填充在写入时清零；tile 复制和提交后的纹理状态继续由
原上传批次及设备资源状态维护。

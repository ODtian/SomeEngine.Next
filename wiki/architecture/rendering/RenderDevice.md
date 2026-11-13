# RenderDevice

`RenderDevice` 保存一台 Graphics.Device 上的资产纹理、纹理视图与材质 descriptor 索引。构造接收同一 renderer 的 `RenderGraph` 和 `RenderPipelines`。图持有 GPU 资源、同步状态与完成信息，pipeline 服务持有 pipeline cache 及创建结果。

纹理资源在帧准备阶段通过 `PrepareTexture(Texture)` 或现有冷路径访问分配，使用 `CreatePersistentTexture` 取得图身份。帧内 `PrepareTexture(ref frame, ...)` 只把当前驻留 tile 放入帧上传页，声明 copy 与 shader 消费依赖。上传 tile、完成 pass 和本帧 chunk 集合由 `frame.GetFrameData` 保存。

只有完整提交回调会推进已上传 chunk、首个驻留 mip、设备资产关联与内容修订。丢弃帧后，下一次暂存继续读取上次完整提交的纹理数据。替换后的旧 graph 资源通过图的结构移除退役，视图与 descriptor 通过 `graph.RetireAfterSubmittedFrames` 退役。

`CollectCompleted` 先推进图的完成帧，再消费资产的设备释放请求。`Dispose` 等待图中已提交帧完成，移除图资源并释放设备描述数据。

图进入 Lost 后，`Dispose` 释放资产视图、descriptor 和 CPU 数据，图持有的 GPU 资源在 `RenderGraph.Dispose` 中释放。renderer 最终关闭时，先等待提交并卸载 UI、呈现、时间处理、外部 feature 和透明消费方，再由 `RenderGraph.Dispose` 释放录制与图资源，随后释放内部 feature 和 `RenderDevice` 的 CPU 数据及原生包装，最后结束 `RenderPipelines`。

参见 [[architecture/assets/Texture-Device-Realization]]、[[architecture/render-graph/Resource-Access]]、[[architecture/render-graph/Frame-Submission]]。

# RenderDevice

## Definition
RenderDevice 是一个 Graphics.Device 对应的 render-side 长期设备状态，位于 SomeEngine.Render。

## Responsibilities
- per-device asset realization
- Texture SRV/UAV cache
- streaming uploads
- backing replacement
- device-side missing resources
- 已提交工作结束后的旧 Graphics 资源回收

## Invariants
- RenderDevice 与 RenderGraph 正交；两者都建立在 Graphics 之上。
- RenderGraph 不认识资产 Texture 或 RenderDevice。
- RenderDevice 不包含 RenderGraph；同一 Graphics.Device 可以服务多张图。
- feature 先显式取得真实 Graphics 资源，再显式交给 RenderGraph 声明访问。

## Relations

应用的呈现 pass 通过 `OrderTextureUploadsBefore` 排在当帧纹理上传完成之后，并通过 Cluster 的 `PageFaultReadbackPass` 排在缺页反馈的提前提交之后。呈现与当帧图像读回由同一次交换链图像提交覆盖；首个场景帧沿这一公共路径验收。

[[architecture/assets/Texture-Device-Realization]] · [[architecture/render-graph/Resource-Access]]

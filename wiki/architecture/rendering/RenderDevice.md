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
[[architecture/assets/Texture-Device-Realization]] · [[architecture/render-graph/Resource-Access]]
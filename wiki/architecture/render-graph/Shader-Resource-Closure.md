# Shader Resource Closure

## Definition
最终 shader entry 真正访问的设备资源必须全部对应到实际 pass 的 RenderGraph resource accesses。

## Invariants
- bindless descriptor 背后的 Texture/Buffer 仍需普通 graph access。
- resource reflection 只用于把 shader 使用事实映射到真实资源、同步和生命周期。
- descriptor index 不允许反向查找资源。
- 资源集合与最终 build/link 后的 shader entry 同源。

## Relations
[[Resource-Access]] · [[architecture/shader/Shader-Resource-Effects]] · [[architecture/material/Bindless-Resource-Dependency]]
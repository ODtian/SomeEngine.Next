# Shader Resource Effects

## Definition

某个最终入口实际使用哪些资源，由当前驻留 `Shader` 的直接 Slang `ShaderReflection`、`EntryPointReflection` 与 `IMetadata.IsParameterLocationUsed` 给出。

## Invariants

- 不把资源使用事实保存成引擎表，也不从旧 Asset 元数据恢复。
- 材质和 pipeline 创建的冷路径从 Slang 直接派生绑定、RenderGraph access、同步与生命周期数据。
- D3D12 root signature、Vulkan pipeline layout 等派生数据只服务对应原生对象，不成为通用反射来源。
- 不把资源使用反射扩展成 purity 证明系统。
- 当前入口实际使用的可写资源必须映射成相应 graph access；其他入口使用的资源不应被误判为当前入口依赖。

## Relations

[[architecture/render-graph/Shader-Resource-Closure]] · [[Geometry-Invocation-Requirements]]

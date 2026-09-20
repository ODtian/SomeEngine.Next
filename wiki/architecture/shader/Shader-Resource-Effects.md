# Shader Resource Effects

## Definition
最终 shader entry 对资源的 read/write/atomic 等使用事实来自 build/link 后的 reflection。

## Invariants
- 这些事实用于实际 resource binding、RenderGraph access、同步和生命周期。
- 不把 resource-effect reflection 扩展成复杂 purity 证明系统。
- writable resource 若真实存在，必须映射成相应 graph access。

## Relations
[[architecture/render-graph/Shader-Resource-Closure]] · [[Geometry-Invocation-Contract]]
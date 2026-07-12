# Current RHI and RenderGraph semantics are preserved

## What to review

审查迁移是否以 `c0ac382e` 当前架构做语义移植，而不是把 ZIP monolith、CPU mirror、fake capabilities、Null delegation 或旧 retained compiler 覆盖回来。

## Pass conditions

- `DeviceDomain`、slot/generation、cross-device/stale rejection、多队列 pin/last-use/retirement继续适用于所有新增对象。
- D3D12 allocation requirements/copy footprints、CPU descriptor page pool 和 RG exact range/subresource dependency 保持权威。
- immediate recording 是公开语义，transparent cache 可关闭且输出不变。
- 新资源/命令进入同一 submission、barrier、failure cleanup 与 retirement 模型。

## Fail conditions

- 恢复 raw integer handle、CPU shadow resource、默认 full capabilities、catch/rethrow 噪音或后端执行期才发现 unsupported。
- 新功能绕开 RG DAG、resource lifetime、device domain 或 completion tracking。
- 出现第二套 graph compiler 或 cache 改变 observable behavior。

## NEEDS_GRILL

只有在 accepted current invariant 本身阻止 mandatory capability 且没有语义等价实现时使用 `NEEDS_GRILL:`。

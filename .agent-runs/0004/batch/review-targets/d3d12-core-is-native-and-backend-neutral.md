# D3D12 core is native and backend-neutral

## What to review

审查 mandatory RHI core 的 public contract、Null oracle、D3D12 lowering 和 WARP evidence，重点覆盖 indirect、query/calibration、swapchain、pipeline cache、diagnostics 与 capability discovery。

## Pass conditions

- public Graphics/RG API 不暴露 Vortice、DXGI、D3D12 native 类型。
- traditional binding 在 FL12_0 + SM6.2 完整工作；optional bindless misuse fail-close。
- indirect 使用缓存的 command signatures 与 `ExecuteIndirect`，query 使用真实 heaps/frequency/clock calibration，swapchain 使用真实 DXGI acquire/present/resize。
- required WARP tests 校验 UAV/像素/readback 或真实状态，不靠 no-throw；InfoQueue 中无 error/corruption。

## Fail conditions

- required backend 路径抛 `NotSupportedException`、返回默认值、CPU 模拟 native 输出或静默 fallback。
- capability 由接口 cast、默认 true、设备型号白名单或未验证 shader model/tier 推断。
- 在 Windows required lane 使用 early return、dynamic skip 或零发现。

## NEEDS_GRILL

真实 FL12_0/SM6.2 或 WARP 限制证明 accepted mandatory native evidence 无法成立，且不能用等价 required hardware lane表达时使用 `NEEDS_GRILL:`。

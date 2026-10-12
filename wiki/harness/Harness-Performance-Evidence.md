# Performance Evidence

性能结论必须对应明确 workload、设备、backend、构建配置与计时边界；证据页面可以按日期保存，但不得把一次 benchmark 的实现选择提升成长期架构规则。

当一个算法选择明确依赖性能数据时，architecture 页面只固定语义约束，具体候选和结论链接到相应 evidence/benchmark 记录。

参见 [[evidence/RHI/Performance-Evidence-2026-08-20]] · [[architecture/cluster/Deform-Cache-Allocation]].

## Animated scene comparisons

`Animate: true` with the same fixed timestep does not by itself select the same
animation workload. The legacy F9 window starts after wall-clock warmup and ends
after two wall-clock seconds, so different render rates can choose different
simulation frames.

For an animated A/B, choose the same `MeasurementFrames` in both
`test-scenes.json` files before either run. For example:

```json
{
  "Name": "1024 spheres",
  "ContentDirectory": ".",
  "Animate": true,
  "MeasurementFrames": { "FirstFrame": 2400, "FrameCount": 1200 }
}
```

Frame indices are zero-based. The scene continues from frame zero with the
existing 1/60-second timestep and unchanged animation, TAA and HiZ histories.
The original readiness requirements and warmup must finish before `FirstFrame`.
Choose enough warmup frames for both builds; the example is not a universal
warmup duration.

The sampler retains every observed frame in the requested interval, including
slow or unready frames. Missing frames, readiness loss, insufficient warmup or
early termination make the run incomplete. The window is never shifted,
extended or filled with replacement frames. Such data remains diagnostic and
must not be reported as a successful performance comparison.

Reports include the requested interval, actual first/last simulation frame,
wall-clock sample duration and each frame's simulation index. `ElapsedMs` keeps
its original frame-start-through-presentation boundary. Unconfigured scenes use
the previous timed sampling behavior. Historical reports without explicit
matching frame windows retain their original comparability limits.

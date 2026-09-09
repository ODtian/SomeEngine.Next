# Profiler Bridge Only

当前的统一插桩设计见 [[Engine-Trace]]：引擎包含 C# 原始事件采集基础设施，独立工具承担分析与报告，统计采样继续使用 `.nettrace`。

`SomeEngine.Core.Diagnostics.Profiler` 中的旧 Tracy 桥接代码仍只负责其桥接职责；新的引擎插桩路径使用 `SomeEngine.Diagnostics.EngineTrace`。不再把“引擎只能桥接外部 profiler、不能采集”作为项目的通用约束。

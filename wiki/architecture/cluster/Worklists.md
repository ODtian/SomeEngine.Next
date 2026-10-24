# Worklists

所有 candidate、visible、append、binning、fault 与其它设备 worklists 都有预配置 fixed capacity 与清楚的 overflow semantics。

每个 worklist 只保留保证 memory safety 所需的最小边界逻辑；容量与质量策略由上层配置。

参见 [[Overflow]] · [[architecture/validation/Memory-Safety-Guards]]。

# RHI-CORE-006 — Allocation and native-call boundary

设备调用前先完成会分配、会增长 managed/native storage、会失败且可在 native 调用前确定的准备工作。native mutation 或 submission 被接受以后，不再执行可能导致部分成功后回滚困难的容量增长、descriptor allocation 或数据结构扩张。

内部 API 使用错误与引擎不变量只在开发检查中诊断，shipping 热路径不为这些错误永久执行 validation/checked arithmetic。真实 native failure、Device Lost、I/O failure 和 memory-safety guard 仍按其实际语义处理。

warmed command path 重用已建立的 high-water capacity，不进行 managed allocation、reflection walk 或 string lookup。

参见 [[architecture/validation/Validation]]。
^rhi-core-006

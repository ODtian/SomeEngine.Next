# Profiler Bridge Only

Profiler 只能作为**外部 profiler 的插桩桥接器**。

禁止在引擎内做：profile 存储、计时表、调用树、计数聚合、本地报告、profile 输出文件。

需要计数时走外部 profiler sink（[[Tracy-Toolchain]]）。

这是 [[SRP]] 在性能观测上的应用：引擎不承担观测职责，观测交给外部工具。

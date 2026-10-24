# Random lookup  Parent: [[Generated-Job-API]] 

可存储的 `ComponentLookupHandle<T>` 只声明未来访问；owner 生效后生成 `ComponentLookupView<T>`。只读 view 登记 topology Read + whole-family Read；可写 view 登记 topology Read + whole-family Write，并默认禁止 `ScheduleParallel`。即使 view 只读，只要同一 parallel owner写 T，也会被 packet alias matrix拒绝。不提供通用 “disable parallel restriction” 属性；用户改用当前 row 的 `ref T`、immutable snapshot、two-pass、可证明的不相交 partition、容器专用 `ParallelWriter`，或未来经认证的`CommandBuffer.ParallelWriter`。当前普通共享CommandBuffer在Job内一律拒绝。


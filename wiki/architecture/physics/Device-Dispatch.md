# 设备线性工作分发

本页维护当前线性计算工作在 X/Y 线程组上的分发与索引接口。
源码入口为 [DevicePhysicsDispatch](../../../src/SomeEngine.Physics/DevicePhysicsDispatch.cs)
和 [physics_dispatch](../../../assets/Shaders/physics_dispatch.slang)。
当前调用者为 [设备活动](../../../src/SomeEngine.Physics/DevicePhysicsActivity.cs)
与 [接触匹配](../../../src/SomeEngine.Physics/DeviceContactMatches.cs)。

`DevicePhysicsDispatch.CreateGroups(groupCount)` 接收所需线程组数量，
将 X 方向设为 `min(groupCount, 65535)`，Y 方向设为 `ceil(groupCount / 65535)`，Z 方向设为 1。
`groupCount` 来自实际工作条数与具体内核的线程组宽度。

对应内核包含 `physics_dispatch.slang`，使用
`devicePhysicsThreadIndex(id, threadsPerGroup)` 取得线性工作索引。
它将 `SV_DispatchThreadID.x` 与 Y 方向的整行跨度合并，
每行跨度为 `65535 * threadsPerGroup`。
具体入口继续按自身工作条数限定有效索引，C# 分发与 Slang 索引使用相同线程组宽度。

下面是容量换算的例子：8,000,001 条记录采用每组 64 个线程，
需要 125,001 个线程组，分发为 X=65,535、Y=2。
两行共同覆盖这些记录，原条数判定限定实际读写范围。

`DevicePhysicsActivity` 与 `DeviceContactMatches` 的内置入口采用每组 64 个线程。
项目提供给活动阶段的具体 `ReadRows` 入口同样调用上述线性索引函数，
读取原图中的约束行；具体约束组与设备表示继续由项目选择。
资源访问、阶段屏障和 RHI 完成链沿各阶段的原提交路径执行。

# Incremental Reconciliation

结构编辑通过 `GraphEditChanges` 记录受触碰的身份。结构存储以 copy-on-write 分块保存；未修改分块由相邻结构版本共享。`GraphStructureIndex` 持有 resource→access、pass→access 与显式依赖索引，提交时从变化身份更新索引。

提交后的变化集中交给图唯一的 `GraphSchedule`，在下一帧开始时一次消费。`FrameResources` 保存资源、上传与读回页、命令复用缓存、提交完成点和当帧段数据。每帧资源通过 `ScheduleVersion` 跟进共享调度版本。

高频当帧工作由 Start / End 当帧段承载。当帧段数量或内容变化时，在该段重新分析，并使用共享持久调度的边界完成连接。

参见 [[Incremental-Scheduling]]、[[Retained-Graph]]、[[Transient-Placement]]。

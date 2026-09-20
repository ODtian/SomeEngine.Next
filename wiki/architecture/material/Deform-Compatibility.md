# Deform Compatibility

Deform key 只表示一组 cluster 是否可以由同一 Deform implementation 与其执行状态一起处理。

同一个 cluster 的所有 Material ranges 必须共享一个 Deform compatibility；这一约束在 mesh cook 时形成，而不是运行时从第一个 Material 猜测。

共享 Deform execution key 的 Material 如果 direct command binding 不同，可以形成不同执行 bin；不能因此拒绝该 Material 组合。bin 在 scene/pipeline 更新边界注册，frame 只使用已分配 bin index。

参见 [[architecture/cluster/Cluster-Deform-Compatibility]]。
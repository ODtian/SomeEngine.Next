# Material Ranges

一个 cluster 可以包含多个 mesh-local material ranges，每个 range 覆盖不同数量的 triangles。

实例化时 mesh-local material slot 映射到真实 Material，再分别参与 Deform/Raster/Shade compatibility。Cluster 不建立 universal Material device-data struct。

同 cluster 中不同 ranges 可以拥有不同 Raster/Shade compatibility，但必须满足 [[Cluster-Deform-Compatibility]]。
## 实例组合的准备

`ClusterMaterialAssignmentSystem` 从 Rendering world 的材质绑定缓冲与几何编码读取实例组合。
来源为当前 `ClusterMaterialAssignmentSet` 的不可变字典；只读查询范围各自比较已有组合、
构造新的 `ClusterMaterialSequence` 并分组。成员变化与普通行版本变化使用同一范围收集算法，
查询读取期覆盖范围工作及其汇合。

范围结果汇合后，原 `ClusterMaterialAssignmentSet.Update` 集中更新实例映射、序列引用数、
空闲区间与新分配偏移。每个新增序列的条目工作写入独立输出区间，
同步工作完成后由原 Update 合并条目、材质地址集合及变化集合，再发布完整 assignment set。
绑定相同材质 ID 顺序与几何编码的实例共享同一序列范围；最后一个引用移除后，
后续等长序列可复用该范围，其他实例已有的偏移继续有效。

持久实例、序列与材质引用由 assignment set 持有；系统缓存的各范围收集器仅保存本轮结果，
汇合后重置。条目填充使用 Rendering world 所选的 Scheduler；普通同步 World 使用共同 Scheduler。
`ClusterMaterialOffsetProducer` 继续从已发布集合向各实例写入序列偏移和有序遮挡值。
源码入口为 `src/SomeEngine.Render.Cluster.Pipeline/ClusterMaterialAssignments.cs`。

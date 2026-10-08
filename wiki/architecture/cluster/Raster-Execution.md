# Raster Execution

## Definition
Indirect draw/dispatch 只是 Cluster triangle raster pipeline 当前内部命令机制，不是 Cluster 对外业务语义。

## Invariants
- Cluster 产生 bounded work sets 并按 stage compatibility grouping。
- Cluster 自己拥有 SW/HW raster command recording；调用方不安装 raster command callback，也不选择如何发 draw/dispatch。
- 当前内部可以使用 indirect draw/dispatch；将来具体 pipeline/backend 可以改用 mesh shader、work graph 或其它机制，而不改变 Cluster 对外 geometry/material 语义。
- 不公开只为替换底层命令方式而存在的 raster executor 对象。

## Relations
[[Hardware-Raster]] · [[Software-Raster]] · [[architecture/material/Raster-Compatibility]]

## 工作记录与选择

候选工作采用12B的实例、页、cluster和完整视图身份，16B可见工作在同一缓冲内附带标志、深度桶及压紧页窗口。实例身份对应UE的24bit范围，视图身份保留20bit（含目标视图选择位）。页分配的16B对齐空间承接叶子标志。

主视图与目标视图按最大三角边的投影大小选择HW/SW；默认阈值32px，近裁剪面的交点进入HW。SW每轴8页，HW每轴4页，从该cluster首个覆盖页开始分窗。每个窗口与映射、请求、缓存刷新和receiver范围相交后入箱，SW工作组保留当前窗口的8×8页缓存。

## 分箱与深度

所有材质范围经过计数、区间分配和散射，工作容量沿实际材质/VRB批次增长。软件列表在普通散射中按相反追加次序排列；带深度桶的列表按当前视图前向距离排序。16B记录携带扩展深度数据，`EnableDepthBuckets`与像素可编程材质共同启用256个对数桶；10–1000m对应UE的1000–100000cm范围。计数、桶前缀和散射共用一个桶缓冲。

用户`VisibilityEvaluate`类型通过`VisibilityDepth("opaque"/"masked"/"offset")`声明深度行为，材料解析时形成调度数据。masked和offset进入像素可编程分桶。opaque和masked在HW/SW用户像素求值之前消费实际已写深度，硬件保持接受quad的导数执行；offset继续以用户返回的`VisibilityPixelResult.Depth`形成最终深度。主视图深度与visibility保持同一64bit winner，目标视图使用同一深度页。

位置投射由共享helper将用户Game-origin位置平移到每视图近端坐标；过去顶点另消费过去Game origin到当前Game origin的平移。HiZ用当前位置投射到过去视图。first-person变换在近端空间保持同一组合次序。

目标页硬件光栅先根据当前窗口、页请求、页表和接收区域解析 atlas 像素地址，
通过筛选的像素再调用用户的可见性 shader，并将其深度原子合并到目标像素。
地址解析和写入共用原页表及 atlas，用户 shader 继续解释自己的 varying 和深度。

目标页 mesh 光栅将用户 shader 输出的位置投影到当前光栅窗口。整个三角形的
保守边界位于实际请求页窗口之外时，输出退化三角形，使光栅器提前丢弃。
跨越投影 w 边界的三角形沿原路径处理。位置暂存属于当前 mesh 工作组。

目标页 mesh 光栅同时复用工作组页缓存，将三角形保守像素边界与请求页的接收区域
遮罩相交。边界向相邻像素扩展，覆盖光栅量化误差；最终像素继续检查请求和接收
区域。页缓存按工作组线程索引分配写入项，只初始化当前窗口包含的页，读写共用
原页表、代数和遮罩。每个软件窗口的页范围由8页分窗形成。

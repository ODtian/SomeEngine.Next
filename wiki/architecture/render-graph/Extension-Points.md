# Frame Regions

## Definition

当帧区段是持久 RenderGraph 树中用于每帧重建子图的有序位置。Feature 在安装时通过
`RenderGraphEdit.AddFrameRegion(label, parent = default)` 创建 `GraphRegionId`，每帧向该
位置添加当帧 pass。它统一承接原 extension point 的插入工作。

## Frame work

`RenderGraphFrame.AddRasterPass`、`AddComputePass`、`AddCopyPass` 和 `AddGeneralPass`
接受末尾的可选 `region` 参数，包含 raster/compute 的管线重载。当帧区段接受当帧 pass；
持久 pass 和子区段使用持久父区段。省略参数的调用继续在根区段末尾添加当帧工作。

当帧 pass 的声明序号分配在区段的编号区间中，资源使用者按 [[Regions]] 的规则排序。
因此其执行位置晚于前面的区段、早于后面的区段。帧内先构建哪个区段由调用方决定，
执行位置始终由持久树给出。区段与全部祖先的启用状态共同控制当帧 pass。

帧资源仍通过 `RenderGraphFrame` 的创建、上传和导入接口声明，访问通过普通
`PassDefinition` 表达。帧完成或取消时，内容沿已有帧清理与资源回收路径处理。

## Analysis correspondence

当帧 pass 按“区段身份 + 区段内序号”对应到该帧执行器保存的执行行。资源访问按该
pass 与访问声明序号对应，包含 `ReadRanges` 的范围集合读取，支持创建 pass 后通过
`GetPass` 补充访问。显式顺序边同样按对应的执行行比较。

形状相同的区段复用依赖分析，同时更新当帧的回调和命令数据。一个区段增长或收缩时，
变化从该区段与共享资源使用者传播；持久 pass 的增删保持其它当帧区段的对应关系。
`RenderGraphStatistics.AnalysisReused` 与 `AnalyzedPassCount` 观察分析复用及局部开销，
`RebuiltRowCount` 记录实际刷新的行。当帧行仍随当帧声明刷新。

## Behavior evidence

`FrameRegionTests` 使用 WARP、自给的复制与清除工作和公共缓冲回读，覆盖区段顺序、
命令数据更新、局部增减、持久 pass 编辑、祖先启用、活动帧边界及显式顺序复用。
`PassCullingAndAliasingTests.FrameRegionParticipatesInCullingAndScheduling_DoesNotThrow` 覆盖当帧资源
与跨队列调度。

## Relations

[[Regions]] · [[Retained-Graph]] · [[Structural-Changes]] · [[Frame-Data]] · [[Region-Reconciliation]]

# Frame Submission

`RenderGraphFrame` 暂存一次完整帧的数据。暂存阶段读取 feature 的已提交状态；当帧数据由帧持有。

`Execute` 完成全部命令提交后，逐一调用 `IRenderGraphFrameExecutionObserver.OnFrameSubmitted(in RenderGraphSubmission)`。观察者在此更新 feature 状态。提交数据提供帧号与读回完成帧号，GPU 完成跟踪由 RenderGraph 集中持有。

执行期异常使图进入 Lost 状态，并继续向调用方传播。后续 `TryBeginFrame` 抛出 `RenderGraphLostException`。设备重建建立新的图及资源。

`IsLost` 公开图的终止状态。设备仍可查询完成点时，`CollectCompleted`、`IsFrameComplete` 和 `WaitForFrame` 继续完成此前提交的帧与读回。冷路径释放 feature 时，图处于 Lost 的资源交由 `RenderGraph.Dispose` 统一释放，feature 继续释放自身的视图、descriptor、查询和 CPU 引用。

renderer 最终关闭时先等待提交并卸载外部消费方，再结束整张图，随后清理内部 feature。`IsDisposed` 表示图已等待提交并释放其 GPU 资源；feature 据此直接清理自己的包装与 CPU 数据。此后 `RetireAfterSubmittedFrames` 直接释放传入对象。

帧在执行之前被释放时，直接丢弃当帧数据。feature 状态保留上一次完整提交的值。

相关规则：[Range Completions](Range-Completions.md)、[Execution Failures](../validation/Execution-Failures.md)。

`frame.GetFrameData` 按 feature 引用保存本帧对象。同一观察者引用登记一次。长期 pass 的帧参数先写入帧对象，在执行开始时应用；长期 shader input 在本帧生效，并在完整提交后保存供后续帧使用。

pass 与 region 的启用值同样暂存在本帧。有效启用状态结合当前覆盖值与 region 祖先，资源视图、访问、读回及当帧段边界读取同一结果；完整提交后才推进持久默认值，取消帧丢弃覆盖值。其它在途帧资源在再次使用时跟进这些默认值，当前帧继续保留刚录制的复用命令。启用变化只更新连接与命令数据，持久 `GraphSchedule.Version` 保持原值。

Cluster 的场景 uniform、空间分组索引、资源读取范围、额外上传、HiZ 选择和计量帧号均由帧对象承载。提交观察者使用本帧捕获的 batch、修订和世界原点推进历史。几何资源投影中的读取范围通过 `GetFrameRanges(ref frame)` 从当前帧取得。

`TryOpenNextFrame` 预留的 `RenderGraphNextFrame` 与执行用的 `RenderGraphFrame` 共用 `FrameResources` 中同一份 feature 数据。准备阶段用 `GetFrameData` 写 CPU 输入并维护 RenderWorld 实体，随后允许持久图编辑；`BeginFrame` 才取得完成编辑后的图版本。取消预留或取消执行均清理这份帧数据。VSM 在准备阶段计算并写入阴影视图实体，记录阶段读取同一份帧数据上传 GPU 参数。

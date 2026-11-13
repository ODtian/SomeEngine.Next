# Extension Points

外部 feature 通过明确的 region 边界向长期图插入当帧工作。长期 pass 安装一次，帧只更新其输入与数据；真正一次性工作写入 `FrameSectionPosition.Start` 或 `End` 当帧段。

feature 维护业务身份到 graph ID 的映射，在帧准备阶段显式创建、修改和移除持久资源。当前帧的上传、读回、回调参数和提交观察者由帧数据对象保存。`frame.GetFrameData` 按 feature 引用取得同一帧对象，`ObserveExecution` 按观察者引用去重。

当帧段的资源访问连接到共享持久调度保存的 region 边界。图统一持有资源状态与提交完成点，feature 在完整提交回调中推进业务状态。

参见 [[Retained-Graph]]、[[Structural-Changes]]、[[Frame-Submission]]、[[Resource-Access]]。

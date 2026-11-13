# Retained Graph

RenderGraph 的结构长期存在。pass、资源和关系具有跨帧身份，通过 `RenderGraphEdit` 显式 Create / Remove / Update 维护。普通帧更新数据、输入和启用状态。

持久调度由 `RenderGraph` 上唯一的 `GraphSchedule` 保存。帧资源通过调度版本物化自己使用的资源和录制数据，多个在途帧共用持久调度。

接近每帧刷新且重建更便宜的工作写入 region 的开始或结束当帧段。各段保留本帧数据和独立依赖分析，持久主体图继续复用。

参见 [[Structural-Changes]]、[[Frame-Data]]、[[Graph-Identity]]、[[Graph-Versioning]]、[[Incremental-Reconciliation]]。

# Blend measurement feature

混合测量位于 `tools/SomeEngine.Transparency.Measurements`。工具通过
`ApplicationScene.CreateFrameFeatures` 或 `SceneScript.CreateFrameFeatures` 安装
`BlendMeasurementFeature(ApplicationRendering, Action<BlendFrameMetrics>)`。

`Reconcile` 读取透明、贴花、材质和材质请求的现有 pass 身份，为所选集合配置 GPU timestamp
和回读。资源描述来自 RenderGraph，实例与材质数据继续由渲染系统持有。
`Record` 读取当帧 pass 启用状态，记录工作量与该帧是否复用了索引。完成后的回调带有模拟帧
编号和图提交帧编号，计时数据与工作、容量和原生分配需求来自同一帧。

引擎 feature 提供已有图区段、资源身份及描述的只读投影。测量查询池、结果缓冲、反馈和
结果汇总由工具持有；应用关闭时通过帧功能生命周期释放。

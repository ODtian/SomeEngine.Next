# Hot Reload

## Definition
Reload 在后台构造完整替换内容，再在消费方的明确更新边界发布。发布方式由使用对象的业务语义决定；切换前失败时继续使用旧有效内容。

## Invariants
- 后台 I/O、解压和数据检查不逐字段修改 renderer 正在读取的对象。
- `Shader` 等不可变对象创建完整新实例，由已有消费方交换外层引用；已开始的异步消费者继续持有旧实例。Shader 不提供原地更新或 Dispose。
- Material、Texture、Mesh 在其业务需要保留可变身份时，通过各自更新 API 切换完整内容，再发布新 revision。
- 切换前失败不改变当前内容；切换后的通知或旧资源清理失败不得破坏已经发布的新内容。
- 不在 Material/Texture getter、setter 或普通 render 访问路径加入 reader 计数、等待或对象锁来协调 reload。
- 边界内部可以使用完成交付和资源生命周期所必需的同步，但它不成为 renderer 的逐访问协议。


开发应用由 `SOMEENGINE_DEVELOPMENT` 构建入口写入 `ShaderSourceDirectoryAttribute`。`ShaderSourceWatcher` 监听该项目及引擎内容根的 `.slang`、`.shader`，按 `SourceBuildRecord.Inputs` 找到直接变更，再沿 `AssetInputs` 收集需要重新链接的消费者。各项目通过同一 `BuildAllAsync` 构建入口更新资产。

构建成功后，`ShaderLibrary.RefreshAsync` 在专用线程创建新的活动 Slang session。未改变 Shader 的强缓存继续保留，使用时将其 IR 恢复到活动 session；新代码按资产身份和内容版本加载。Watcher 将完整的 Shader 替换排队，在帧准备边界调用 `Replace` 并更新材质外层引用和使用该 Shader 的 feature 管线。编译诊断写入标准错误，现有消费者继续持有已加载 Shader。

`RenderPipelines.CreateWithShader` 复用原管线的图形状态和描述，仅替换入口反射与代码。Linked compute/raster 包装在冷路径交换 PSO，feature 将受影响 pass 的管线和参数布局交回 RG 编辑。

## Relations
[[Asset-Update-Boundary]] · [[Texture-Device-Realization]]

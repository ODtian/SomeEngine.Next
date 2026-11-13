# Screen Binning

`ScreenBinningPasses` 是静态登记辅助，在调用方提供的 `ref RenderGraphEdit` 中登记屏幕矩形分箱。使用方提供条目的矩形、逐 cell 相交测试、cell 下标和输出条目下标；图持有全部工作缓冲与结果缓冲。辅助返回 `ScreenBinningResources`，其中保存 region、缓冲、视图、完成 pass 和诊断读回的图编号。

功能类型持有自己的图，调用 `BeginEdit`，把同一编辑传给共享辅助，`Commit` 后才把返回的编号保存为字段。共享辅助的局部集合只用于登记时收集编号。shader、PSO、间接布局及反馈分别由内容、`RenderPipelines`、功能类型及诊断接收方持有。

入口接受条目容量、cell 数和列表容量。使用方的 shader 通过 `import screen_binning` 引用共享类型与泛型函数，宏配置源也可 include `screen_binning.slang`，实现 `IScreenBinningItem`，并以 `CountScreenItems`、`ExpandScreenItems`、`FillScreenItems` 三个 compute 入口调用共享泛型函数。有序使用方的输出下标在各 cell 中保持与条目下标相同的优先级顺序。

调用方独立加载共享 shader 与条目 shader，再把两者交给登记辅助。共享 shader 提供 Scan、FinishItems、ScanCells、FinishCells 等计算入口；条目 shader 提供上述三个具体入口。Slang import 引入共享定义，计算入口按各自 shader 的入口表取得。

`Add` 接收调用方的输入 `GraphBufferSrvId` 列表。Count、Expand、Fill 三个条目 pass 各为这些输入声明一次计算阶段的读取，范围语义为有效条目区段。使用方的 resolver 通过 `ReadOnlyBufferDescriptor` 交出对应描述符。各阶段的工作缓冲读写和参数缓冲读取由算法固定声明，shader 反射负责按当前入口的资源使用生成描述符流。层 tile 分箱在自己的登记中为上述三个入口声明 `LayerTileBinning` 参数读取。

内容替换时，使用方在自己的图编辑中调用 `ScreenBinningPasses.ReplaceShader(ref edit, resources, pipelines, previous, next, resolver)`。辅助从现有 pass 取得 PSO，由 `RenderPipelines` 的既有描述定位 `previous` 对应的 compute 入口并创建替换 PSO，随后更新参数布局和描述符流。扫描层级同样重绑已有的值视图与组和视图。返回值由调用方在提交后采用，公开缓冲和 pass 编号继续用于原有下游连接；PSO 仍由渲染器的 `RenderPipelines` 持有。

条目容量或 cell 网格变化时，使用方在自己的编辑中调用 `ScreenBinningPasses.Rebuild`。辅助移除旧算法 region、扫描和工作缓冲，经同一 `AddCore` 按新尺寸登记，同时保留 `Cells`、`Indices` 和 `Diagnostics` 的缓冲编号。使用方把算法 region 放在自己持有的固定外层 region 中，提交后采用新的算法编号；外层 region 继续表达与下游的顺序。材质贴花的数量增长和网格设置使用这条流程。

每帧通过静态 `SetFrameParameters` 传入资源编号、条目数、条目容量、cell 数和每 cell 上限。五个步骤的持久 pass 组依次执行：

1. Count 计算每个条目覆盖的矩形大小，同时清空 cell 计数和结果列表。
2. Scan 以 256 元素工作组计算局部前缀，递归扫描工作组和，再向下加回工作组前缀。总覆盖数生成展开派发的间接参数。
3. Expand 在完整覆盖空间中为每个矩形位置派发一个线程，二分查找条目，执行逐 cell 相交测试并累计 cell 数量。
4. ScanCells 对应用每 cell 上限后的数量做同一多工作组扫描。只有整个列表段处于总容量以内的 cell 保留结果，后续 cell 的数量写为零。
5. Fill 再次展开覆盖空间，对保留段逐级执行 `InterlockedMin` 插入。该插入按输出条目下标排序，逐 cell 保留下标最小的若干条目；调度顺序只影响插入过程。最终列表和每 cell 截断结果由条目优先级决定。

cell 偏移来自 Scan。每个矩形位置独立执行相交测试和写入；内核中的循环处理前缀扫描、二分查找和单 cell 有序插入。

光照与贴花使用上述 `ScreenBinningFill<TItem>`。层 tile 列表使用 `ScreenBinningFillUnordered<TItem>`：每 cell 上限为 `uint.MaxValue`，完整保留的 cell 数量等于原相交计数。`FinishCells` 完成偏移和诊断后，无序 Fill 对原 `ScreenBinningCellCounts` 递减一次，取得唯一的逆向写入位置并写入条目；计数器复用同一份缓冲，下帧由原有 Count 清零。容量溢出的空段直接结束写入。C# 登记为 Fill 固定声明计数缓冲的读写访问，两种输出使用同一份算法资源生命周期。

`Cells` 保存每个 cell 的 `(列表偏移, 条目数)`，`Indices` 保存条目下标。`Diagnostics` 的五个 uint 依次为溢出 cell 数、应用每 cell 上限后的列表需求、矩形总覆盖数、实际列表容量、实际保留的索引数。最后一项由前缀中最后一个完整保留的 cell 唯一写出。一个 cell 发生每 cell 截断或总容量溢出时计入一次。功能类型传入自己的 `IGraphReadbackReceiver`，由 RG 交付这些公共结果，计数和质量字段保存在功能类型。诊断 tag 记录产生结果的 `FrameIdentity`。

Light 与 Decal 功能在构造时注册自己的 prepare system。`TryOpenNextFrame` 交付完成的读回后，现有 `RenderPrepareSystems.Update` 在 `BeginFrame` 前读取反馈，功能内部打开图编辑、调用静态 `SetListCapacity` 并提交扩容。Runtime 使用两个在途帧，验收对照帧 N 的反馈及 N+F 的结果，F=2；`OnReadback` 更新读回字段。shader 从缓冲描述查询当前容量，图编辑与缓冲大小拥有相同的提交边界。

每帧重写的矩形、灯光、贴花矩阵和视图数据通过 `GraphBufferInput` 接收 `frame.Upload` 的有效前缀。输入视图在当帧关联到上传切片；主要图中的分箱 pass 及工作缓冲保持持久。

独立使用前缀扫描时，调用静态 `ScreenBinningScanPasses.Add`，传入同一 `ref RenderGraphEdit`、已有 `RenderPipelines`、扫描 shader、uint 缓冲、元素数及前序 pass。返回的 `ScreenBinningScanResources` 同样只包含图编号。整个 B1 分箱和 V9 可用页压缩复用这一份多工作组扫描登记。扫描的两条 PSO 从渲染器已有 `RenderPipelines` 取得，各递归层级复用这两条 PSO。

每层扫描给值缓冲创建范围为 `[0, count × sizeof(uint))` 的结构化 UAV，并对同一范围声明读写。Scan 和 AddGroupPrefixes 通过该 UAV 的 `GetDimensions` 取得元素数；扫描计数与图视图范围具有同一来源。缓冲在扫描前缀之后的元素保留原值，可供使用方保存额外计数。`ScreenBinningScanResources` 保存各层扫描 pass、加回 pass、值视图与组和视图的图编号，热替换继续复用这些范围。

仅展开覆盖位置并执行操作的使用方实现 `IScreenBinningCoverage`，提供 `GetBounds` 与纯判断的 `Intersects`，再实现 `IScreenBinningOutput.Write(itemIndex, coordinate)`。`ScreenBinningExpand<TCoverage, TOutput>` 把每个相交位置交给输出类型。原有 `IScreenBinningItem` 继承覆盖接口，单类型 `ScreenBinningExpand<TItem>` 选择默认的 cell 计数输出，原有五步分箱继续使用相同调用。

静态 `ScreenBinningExpansionPasses.Add` 登记 CountCoverage、共享多工作组 Scan、间接派发参数写出和 Expand。该路径持有的图资源为参数、条目偏移、派发参数和扫描中间缓冲；输出数据通过使用方的资源绑定传入。返回的 `ScreenBinningExpansionResources` 同样只含图编号，调用方提交编辑后保存。投影物失效的每个条目由变化记录、视图、mip 及当前或上一帧包围记录确定，每页由一个展开线程执行失效动作。

变化条目接近每帧刷新时，调用 `ScreenBinningExpansionPasses.Add(ref RenderGraphFrame, ...)`，把实际条目数和既有当帧 region 交给辅助。该入口按实际长度创建条目偏移、组和与间接参数，复用 `ScreenBinningScanPasses.Add(ref RenderGraphFrame, ...)` 的相同扫描层级。扫描值 UAV 的范围限定为实际前缀，shader 从该视图的 `GetDimensions` 取得元素数。返回值同时提供 `FirstPass` 和 `CompletePass`，使用方在当帧显式连接前后工作；持久主体图保存固定 region，当帧数据保存这次展开的编号。VSM 投射物失效使用这条路径。

行为检查 `ScreenBinningBehaviorTests` 从公共图入口提交矩形条目，读取 cell 段和列表，覆盖 80×45×16 的展开、连续两帧顺序、半容量时保留完整 cell，以及 shader 资源声明顺序和编译裁剪变化后的热替换。热替换检查对照公开缓冲与完成 pass 编号、一次提交的结构版本增量和实际列表结果。`ScreenBinningScanTests` 覆盖多组前缀、逐层加回和扫描前缀之外的数据保留。

调用方在释放分箱前，先通过图编辑移除读取结果的下游 pass 和读回，在同一编辑中调用静态 `Remove`，按返回的 region 和资源编号卸载 pass、视图、缓冲与诊断读回，最后提交编辑。功能类型把自己持有的间接布局交给 RG 等待已提交帧后释放。PSO 随渲染器已有 `RenderPipelines` 一起释放。

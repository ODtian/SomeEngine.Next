# 材质贴花

`MaterialDecal` 与实体的 `WorldTransform` 描述一个投影层。变换把局部 `[-1, 1]` 盒映射到世界中的影响体积，`Material` 持有用户参数与资源，`Order` 决定由小到大的组合次序。相同次序使用实体索引排序。

`MaterialDecalRenderExtractor` 将该引用、次序与变换交付到 Rendering world 的 `RenderMaterialDecal` 和 `RenderTransform`。`MaterialDecalFeature` 从该集合构建 GPU 体积与属性数据，网格实例继续沿既有实例提取。`SceneEdit.AddMaterialDecal`、`SetMaterialDecal`、`SetEntityTransform` 与 `DeleteEntity` 提供相同的游戏世界编辑入口。

## 用户属性与受光

用户 include 通过 `[MaterialDecalScalars]` 声明贴花属性的字节布局。`ScalarLayout` 按字段名称读取 `Material` 的属性，并把纹理与 sampler 写成设备描述符索引。GPU 体积携带参数地址，属性内容由用户 shader 解释。

DefaultProject 的 `coating_material.slang` 声明 `CoatingScalars`、纹理/sampler 数组及 `EvaluateCoating`。该层复用 `StandardMaterial` 的颜色、法线图、ARM 图、粗糙度与 sampler 参数，按体积局部 XY 采样，并把法线变换到世界方向。Standard 和 Unlit 的 `evaluateSurface` 都在自己的最终命中位置读取同一参数与资源视图。Standard 将层组合到基础色、着色法线及粗糙度，Unlit 组合到颜色；表面自身的 alpha 持续表达该覆盖命中的透明度。

组合发生在表面求值中。opaque 的 Forward/Deferred 路径和每个透明命中均获得自身的层属性，随后执行原有求色与阴影采样。`SurfaceReceiver.GeometricNormal` 持续表达原表面几何，贴花混合后的法线作为 `ShadingNormal` 进入光照。

## Forward+ 候选

`SomeEngine.Render.Features.Decals.MaterialDecalBinning` 提供独立的体积分箱入口。调用方将 Rendering world 提取所得的贴花变换按 `Order`、实体索引排序后传入；条目索引就是最终应用次序。该入口持有自己的 tile 尺寸、深度切片和投影参数，通过 [Screen-Binning](Screen-Binning.md) 的五步生成 cell 列表。每个展开位置执行贴花变换盒与 cell 视空间包围盒的分离轴测试，列表内按条目索引升序。

`MaterialDecalFeature` 实际持有这个分箱器。每条 144 字节的 `MaterialDecalVolume` 保存世界与投影空间的双向变换和用户参数地址，Feature 与分箱读取同一份 volume 缓冲。独立的 240 字节视图参数同时供覆盖测试和材质查询使用。standalone 的矩阵入口将变换写入同一结构，typed upload 保持结构对齐。`material_decal_binning.shader` 在同一 ready shader 中烘焙共享 `screen_binning` 与贴花条目入口。

`Cells` 保存每个 cell 的 `(偏移, 数量)`，`Indices` 保存作者次序下的体积下标。`BeginMaterialDecals` 用当前像素和表面位置查询这个独立网格，`NextMaterialDecal` 继续执行投影范围检查并返回用户数据。材质仍自行解释 coating 等模型。视图、体积、设置或列表容量变化时刷新索引，相同输入复用已有结果；复用状态只在帧提交后采用。

`LastFrameOverflowCells`、`FeedbackFrameIdentity` 和列表容量需求来自公共读回。分箱器在构造时接收已有 `RenderPrepareSystems` 并登记自己的 prepare system；读回送达的 N+F 帧在 `BeginFrame` 前提交列表扩容。CPU 已知的体积数量增长和网格设置变化通过无状态 `ScreenBinningPasses.Rebuild` 重建内部工作与扫描，保留公开 cell、引用和诊断缓冲的图编号。固定外层 region 保持与下游的排序关系，测量使用方按 Feature region 重新取得 pass 集合。`--blend-index=full|xyz` 继续提供完整来源与独立网格查询的对照。

## DefaultGame 场景

- `decal-layers`：16 组局部布置，每组包含一面 opaque 墙、三个不同深度的透明面和 16 层涂层，共 256 个体积。
- `decal-sparse`：64 个局部弹孔体积作用于 opaque 墙；前方透明面位于体积之外的深度。

两组均通过公共场景编辑更新体积变换。涂层使用 `Blend coating normal` 和烟雾 alpha 纹理显示法线、粗糙度与空间覆盖。

公共图像检查从 `Application.RunAsync` 与 `SceneScript` 进入整条提取和绘制路径。`MaterialDecalRenderingTests` 覆盖：

- opaque 蓝面依次叠加半透明红层和绿层，中心区域得到线性颜色 `(0.25, 0.5, 0.25)`。
- `z=4` 与 `z=6` 的两个半透明蓝面分别接受同一个红色体积；体积先覆盖后面，再移动到前面，删除后整图恢复初始状态。该检查把引用预算设为 `1`，验证相同结果的容量续批。

这两条行为已在 D3D12、Vulkan 各通过一次，线性颜色容差为 `0.001`。检查使用完成后的 91 资产内容包，分别运行 8 帧与 16 帧；图像失败沿共享检查保存实际、期望及差异 PNG。执行：

```powershell
$env:SOMEENGINE_TEST_COOKED_CONTENT = '<matching cooked DefaultProject>'
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~MaterialDecalRenderingTests'
```

导入场景从同一项目模板生成材质表面代码，shader 搜索路径同时包含引擎与项目共享 include，因此刷新 Sponza、Amazon Bistro 的材质时使用同一层属性实现。

`LitDecalNormalAndRoughnessMatchTheSameStandardSurface` 对照受点光源照明的 Standard 面板：先保留原表面，再将普通材质的法线图改为 `Blend coating normal`、粗糙度改为 `.85`；随后恢复原表面，通过全覆盖用户 coating 到达相同属性状态。原表面与参考图像不同，投影层与普通材质的整图线性误差满足 `.003`。2026-10-16 使用 `review/DefaultProject` 的 91 资产匹配包与 `2eafdd25` 源码，D3D12、Vulkan 各完成 16 帧，两项全部通过。与 Offset 深度组一起执行的行为规则检查为 0 违规。

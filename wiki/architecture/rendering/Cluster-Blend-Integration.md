# Cluster 多覆盖混合

规格 [#221](https://github.com/ODtian/SomeEngine.Next/issues/221) 将普通网格的透明材质、完整材质 X-ray、材质贴花和有序颜色/stencil 纳入 Cluster 管线。研究与性能基线见 [tile visibility 研究](Transparency-Tile-Visibility.md)。本页记录正式功能的数据通路、使用入口和验收对应。

## 数据与执行

游戏实体持有网格、材质和效果组件；提取系统更新 Rendering world；Cluster 管线从该世界形成 GPU 实例数据、源图元与候选引用。`Material.State` 与材质属性共用资源修订机制。顶点解码、变形、细分曲面与表面材质由用户 shader 求值。

几何 cluster 表示网格分组。Forward+ cell 表示屏幕 XY tile 与视空间 Z 分区。多覆盖功能复用光照视图的分区约定，先计数、保留连续范围，再散布图元引用。命中按自身深度所属 cell 执行一次；容量设置决定驻留批次，后续源图元继续参与覆盖与合成。

每个透明命中重建自己的位置、UV、法线、导数与 `SurfaceReceiver`，执行自己的材质与阴影求值。OIT 依真实深度推进颜色与透射率。`Material.State.MaximumError = 0` 遍历全部贡献；正值结合材质声明的 `ColorLimit` 限定尾部误差。

`Material.State.Ordered` 将材质放入 OIT 之后的有序阶段。`DrawOrder` 指定作者次序，相同次序按实例、几何及图元身份推进。`BlendAttachmentState` 表达颜色与 alpha 的因子、运算和通道写入；`BlendConstant` 提供常量混合值。`DepthStencilState` 与 `StencilRef` 表达深度、正反面的 stencil 比较、读写掩码及 stencil-fail、depth-fail、pass 操作。覆盖先经过用户材质的可见性求值，再执行完整状态链；颜色写掩码为零的命中仍推进 stencil。

有序材质通过共用实例属性保留主视图 HiZ 后的覆盖，深度比较在状态链内选择 depth-fail 操作。像素线程保留当前颜色、深度与八位 stencil，按作者次序完成全部覆盖。最终 stencil 写入一张 R32UInt 整数图。

持久 RenderGraph 保存投影、计数、范围保留、引用散布和求色阶段。每帧上传及 GPU 引用内容沿这些阶段更新；几何、实例、视图、驻留或相关渲染设置变化时刷新索引。静态输入复用既有数据。

## 统一场景

DefaultGame 使用 `--cluster-effect=<效果>` 选择专项内容，`--blend-index=full|xyz` 选择完整遍历或 XYZ 候选。两种方式消费相同的几何、用户材质和效果规则。`--cluster-static` 固定效果物体，静态性能对照同时固定场景行为。

| 效果 | 场景内容 |
| --- | --- |
| `glass` | 32 个交错玻璃面板，四组材质与八层相交覆盖。 |
| `smoke` | 八团烟雾，每团 16 张带 alpha 纹理的卡片。 |
| `foliage` | 64 组植被，每组三张交叉叶片。 |
| `deformed-phong` | 16 个移动部件，使用 Wave 顶点变形与 Phong 曲面。 |
| `xray` | 64 个机械部件，共 10,752 个三角形，带独立遮挡板与完整 Standard 材质。 |
| `decal-layers` | 16 组表面，每组包含三层玻璃、opaque 面板与 16 层材质贴花，共 256 个体积。 |
| `decal-sparse` | 64 个局部贴花体积覆盖墙面，透明前景与墙面分别使用命中位置求值。 |

场景内容通过 `SceneTestPlatform.InitializeScene` 和 `UpdateScene` 编辑游戏实体。相机回放时专项物体使用同一相机相对布局，三个基础场景都保有对应的效果工作量。

## 自定义硬件 stencil 阶段

`ClusterPipelineSystem.GetStencil()` 按消费者请求创建共享整数图。应用的 `IApplicationColorFeature` 通过 `ApplicationRendering.Stencil` 取得同一能力；`SceneScript.CreateColorFeature` 提供脚本应用的相同入口。整数图与硬件阶段的区段长期保留，材质变化更新其生产依赖。

调用 `stencil.Export(ref edit)` 创建 D32FloatS8UInt 附件，声明整数图打包与 stencil-plane copy。硬件 pass 使用返回的 `Texture`，并排在 `CompletePass` 之后；该 pass 持有自己的深度输入、颜色附件与 RHI stencil 状态。随后调用 `attachment.Import(ref edit, hardwareComplete)`，通过反向 copy 与解包将 stencil 返回同一整数图。后续阶段继续读取 `stencil.Values`，也可以再次导出为硬件附件。

```csharp
ClusterStencil stencil = rendering.Stencil!;
ClusterStencilAttachment attachment = stencil.Export(ref edit);
// 在 stencil.Region 添加硬件 pass，附件取 attachment.Texture。
edit.OrderAfter(hardwarePass, attachment.CompletePass);
GraphPassId completed = attachment.Import(ref edit, hardwarePass);
```

有序像素状态处理与显式交接进入 `MaterialCompositeMs`。R32UInt 图、材质状态、交接缓冲和硬件附件进入显存口径；交接缓冲按 RHI 的 stencil plane footprint 组织行距和各视图偏移。

```powershell
./tools/Measure-ClusterEffects.ps1 -ApplicationPath <NativeAOT Game.App.exe>
```

该入口调用 `tools/Measure-Scenes.ps1`，每次同时测量 1024 小球、Sponza、Amazon Bistro，以完整且收敛的三场景报告交付对照。配对固定分辨率、模拟帧、场景行为、渲染配置、编译配置与采样。内容构建单列；trace 定位单列。

## X-ray

网格实体的 `Xray` 组件分别持有可见侧与遮挡侧的颜色倍率、透明度，以及遮挡距离区间。编辑入口与其他游戏数据一致：

```csharp
edit.SetXray(entity, new Xray(
    new XrayBlend(.8f),
    new XrayBlend(new Vector3(.7f, .9f, 1), .45f),
    MinimumOcclusion: 0,
    MaximumOcclusion: 8));
```

`RemoveXray` 恢复该实体的普通材质路径。独立提取器更新镜像上的 `RenderXray`，其 48 字节设置随共用实例属性提交。primary hierarchy 与 cluster 的遮挡判断保留效果几何，继续执行视锥和 LOD 选择；阴影视图沿原材质路径工作。求色以该命中自己的表面和 receiver 完成，随后按 opaque 深度分类并应用对应侧别规则。距离使用视空间的世界单位。

## 计量口径

`--measure-blending` 产生完成 GPU 提交后的逐帧 `ClusterBlendMetrics`，通过模拟帧关联统一平台的呈现间隔。测量采用图形队列的校准时间戳；统一性能工具同时关闭异步计算以固定阶段顺序。

| 字段 | 工作范围 |
| --- | --- |
| `ProjectionIndexMs` | 当前几何的用户投影、候选计数、连续范围与引用构建。复用帧记录相应阶段的实际成本。 |
| `CoverageReceiverMs` | 覆盖与各命中阴影 receiver 请求。 |
| `MaterialCompositeMs` | 命中筛选、表面与材质求色、颜色和状态合成。 |
| `Triangles` / `References` | GPU 记录的实际工作量。 |
| `UsedBytes` / `CapacityBytes` / `AllocationBytes` | 当帧驻留数据、配置容量和实际资源分配。共享网格驻留与统一参数存储沿各自已有计量维护。 |
| `ReusedIndex` | 当前帧复用已有投影及候选内容。 |

`SceneTiming.Blending` 汇总三段的平均值、中位数、P95、构建/复用帧数及显存；整帧呈现间隔保留在原 `SceneTiming` 字段。逐帧原始数据保存在统一报告的 `BlendFrames`。

`SceneTiming.Instances` 在场景完成时记录共用实例存储的实际行数、批次数、容量与属性字节数，效果增加的实例属性进入该项；共享几何仍由 `GeometryUsedBytes` 等既有字段记录。

## 行为与交付对应

所有图像检查经过 `Application.RunAsync`、`SceneScript`、游戏编辑和线性图像读回，失败图沿公共设施输出实际、期望与差异 PNG。默认 `dotnet test` 同时执行 [行为检查规则](../../harness/Behavior-Checks.md)。

| 规格故事 | 任务 | 公共行为检查与实现 |
| --- | --- | --- |
| 1–5：网格材质、用户求值、每命中属性、相交排序、光照阴影 | [#222](https://github.com/ODtian/SomeEngine.Next/issues/222) | `TransparencyRenderingTests` 的完整排序与全不透明材质对照；`Material.State`、源图元和用户表面求值。 |
| 7、15：硬软语义、容量分段 | [#224](https://github.com/ODtian/SomeEngine.Next/issues/224) | `TransparencyIndexTests.XyzCandidatesMatchFullScanWhileModelsAndCameraMoveAcrossCells` 在两种后端对比完整遍历、常规容量和单项容量。 |
| 14：游戏编辑与当前帧更新 | [#224](https://github.com/ODtian/SomeEngine.Next/issues/224) | `StaticBlendCellsAreReusedUntilInstanceMembershipChanges` 与 `SceneInitializationAndFrameEditsMatchTheSameScriptedScene`。 |
| 16：尾部误差界 | [#222](https://github.com/ODtian/SomeEngine.Next/issues/222) | 完整排序图像跨越四项局部队列继续求值；同一检查把材质误差界改为 0.04，与精确图比较整图误差。 |
| 17、18：专项与整帧、统一场景 | [#228](https://github.com/ODtian/SomeEngine.Next/issues/228) | `ClusterBlendMetrics`、DefaultGame 效果入口与 `Measure-ClusterEffects.ps1`。 |

交付分支为 `codex/tile-blending-221`；[PR #229](https://github.com/ODtian/SomeEngine.Next/pull/229) 汇集全部规格工作。

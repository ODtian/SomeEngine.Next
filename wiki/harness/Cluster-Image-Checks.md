# Cluster、VSM 与 Temporal 图像行为检查

票 #87 将渲染行为交给 `Application.RunAsync(args, SceneScript)` 的真实帧循环。测试的动作只使用 `SceneEdit`、项目材质和纹理公共 API；结果只读取 `SceneImage`。每条行为在 D3D12 和 Vulkan 上分别运行。

## 内容与图像期望

`RenderingContentFixture` 在独立临时目录构建五种内容：红绿小球、细分材质、交换红蓝通道的 shader、高细节流送球体和带遮挡物的影子接收体。内容来自 `SomeEngine.SceneContent.BehaviorScenes`，夹具结束时清理目录。

材质、shader、几何及相机变更与第 0 帧已具有最终状态的运行逐像素比较。VSM 接收区域筛选与完整接收区域比较，方向光缓存与重新生成的影子比较；灯光移动还比较启动时就位于目标位置的灯光。每个测试的动作与期望均在该测试中声明。

Temporal 取消帧的期望限定为红球、绿球内部与远离几何的背景区域，验证完成样本序列的颜色；深度边缘在独立的等价相机/实体移动用例中覆盖。背景拒绝用例比较已删除前景原来覆盖的整块区域。

绝对颜色来自场景定义。白色单纹素纹理与红/绿 tint 的线性亮度为 1，呈现 shader 使用 `pow(color / (color + 1), 1 / 2.2)`，对应 RGBA8 通道值 186；区域比较容差为 2，背景为 `(0, 0, 0, 255)`。Temporal 融合用例使用 `.9` 的历史权重和宽松邻域范围；连续 `.8` 输入得到 `.98`、`.962`，颜色经同一呈现公式转换。默认夹紧用例单独声明最大 28 的呈现红色残留。

## 旧测试与新行为对照

| 旧测试 / 旧场景动作 | 新独立测试 |
| --- | --- |
| `MaterialAdditionsAndRemovalPreserveVisibleOutput`：增加材质 | `AddedMaterialMatchesStartingWithTheFinalMaterial` |
| 同上：移除最后一个材质引用 | `RemovedMaterialMatchesStartingWithTheRemainingMaterial` |
| 材质替换及 #86 的 `MaterialReplacementAtFrameThreeMatchesStartingWithThatMaterial` | `ReplacedMaterialMatchesStartingWithTheReplacement` |
| 细分材质加入 / 移除 | `AddedTessellatedMaterialMatchesStartingWithThatMaterial`、`RemovedTessellatedMaterialMatchesStartingWithLinearMaterials` |
| `ManyShaderMaterialsPreserveVisibleOutputWithSharedCandidateCapacity` | `ManyMaterialsSharingOneTextureMatchStartingWithAllMaterials`，64 个材质、两种 shader 内容及不同采样器 |
| 共享纹理内容更新 | `UpdatingASharedTextureMatchesStartingWithTheNewPixels` |
| 材质颜色更新 | `MaterialColorChangesMatchStartingWithThatColor` |
| 采样器分组变化 | `SamplerReplacementMatchesStartingWithThatSampler` |
| `ShaderContentReplacementPreservesMaterialAndTextureOutput` | `ShaderContentReplacementMatchesStartingWithTheReplacementShader`，替换内容实际交换红蓝通道 |
| `GeometryChangesAndRemovalPreserveOutputAfterCancellation`：几何增加 / 删除 | `AddedGeometryMatchesStartingWithThatGeometry`、`DeletedGeometryMatchesStartingWithTheRemainingGeometry` |
| 同上：空场景 | `DeletingAllGeometryShowsTheDeclaredBackground` |
| 同上：旋转、拉伸并穿越侧视锥 | `StretchedGeometryCrossingTheViewEdgeMatchesStartingAtThatTransform` |
| 同上：取消帧 | `CanceledGeometryFrameLeavesTheFollowingImageEqualToTheCompletedSequence` |
| 同上：移动遮挡物后同帧恢复 | `MovingAnOccluderRecoversTheRearGeometryInTheSameFrame` |
| `StreamedGeometryKeepsVisibleCoverageWhenTheDetailDoesNotFit` | `StreamedGeometryKeepsTheSphereInteriorCoveredWhileDetailDoesNotFit`，两种光栅阈值、受限页内存、三帧整块区域 |
| `CameraMovementRecoversHiddenGeometryWithoutChangingTheScene` | `CameraMovementRecoversOccludedGeometryLikeRenderingWithFreshOcclusion`，保留历史与每帧新历史比较 |
| `MovingLightUsesCurrentReceiverPagesInItsFirstFrame` | `MovingLightShadowsMatchStartingWithTheFinalLight` |
| `DirectionalShadowsAfterCameraScrollMatchRefreshedShadows` | 同名的新场景脚本测试 |
| `NormalOffsetsPreserveCurrentShadowsWithReceiverMasks` | `ReceiverMasksPreserveNormalOffsetShadows`，两种偏移、顶点/网格/软件光栅 |
| `HistoryBlendsOnlyCompletedFramesFromTheSameView`：连续帧融合 | `TemporalHistoryBlendsConsecutiveCompletedColors` |
| 同上：取消帧及在途帧顺序 | `CanceledTemporalFrameLeavesTheSphereColorsEqualToTheCompletedSequence` |
| 同上：禁用 Temporal | `DisabledTemporalResolveShowsTheCurrentMaterialColor` |
| 同上：相机切换、历史清理 | `ResetCameraHistoryShowsTheNewColorAsAFreshTemporalSample`、`ChangingCameraWithAHistoryResetMatchesStartingAtThatCamera` |
| 同上：前景与背景深度变化 | `RemovedForegroundLeavesTheCurrentBackgroundColor`、`MovingForegroundEdgesMatchMovingTheCameraByTheOppositeOffset`（近景与远景） |
| 同上：运动重投影、抖动 | `MovingForegroundReprojectsHistoryAndExposesTheCurrentBackground` |
| 同上：默认邻域夹紧 | `DefaultTemporalClampRejectsAnObsoleteMaterialColor` |
| 同上：极小值、负值、HDR 值重复帧 | `TemporalPreservesPresentedColorsAcrossRepeatedFrames` |
| VSM / Temporal 代表开关组合 | `VsmAndTemporalCombinationsPresentTheDeclaredSphereColors`、`VsmAndTemporalCombinationsMatchTheFinalLightWithFreshCameraHistory`，全部四种组合与真实灯光变化 |

测试中的内部剔除计数、实例计数和图结构版本检查已经随旧场景函数一起替换为外部图像行为。#86 的材质与几何动作测试随迁移合入上述双后端用例；相机、尺寸及错误分类的入口检查继续由场景脚本测试维护。

## 分类与后端能力

以上图像行为属于 `Category=Hardware`。基线票 #86 在 WARP 的网格及顶点光栅组合都观测到完整帧完成超时，后续票 [#128](https://github.com/ODtian/SomeEngine.Next/issues/128) 负责软件适配器回归。已有两个 Software 回归仍以具体票号说明跳过原因。

`SceneTheoryDiscoverer` 使用图形后端公开适配器、设备能力查询，在发现阶段为 D3D12 与 Vulkan 分别生成用例。Windows 窗口、硬件适配器、动态库、间接命令和 64 位原子能力不足时生成带具体原因的 xUnit 跳过用例；帧图像仍通过公共应用入口取得。发现阶段以不可变字典保存本进程的后端能力结果，网格着色器用例单独核对对应功能。

尺寸变化继续沿用 [#129](https://github.com/ODtian/SomeEngine.Next/issues/129) 的独立回归。

## 验收

交付分支：`codex/spec82-ticket-87`；来源基线：`600da074a767bc42c890c244baabd11b94671080`。新增 33 条独立行为、102 个后端与参数用例，图像场景均通过公共应用入口。

| 验收命令 | 本次结果 |
| --- | --- |
| `dotnet build tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj --no-restore --verbosity quiet -m:1` | 0 警告、0 错误 |
| `dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj --no-build --no-restore --filter "Category!=Hardware" --logger "console;verbosity=minimal" --verbosity quiet -m:1` | 21 通过，2 按 #128 跳过，0 失败；自动行为规则检查通过 |
| `dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj --artifacts-path .artifacts/ticket-87-cluster --no-restore --logger "console;verbosity=minimal" --verbosity quiet -m:1` | 4 通过，0 跳过，0 失败；已清理该独立验收目录 |
| `dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj --no-build --no-restore --filter "Category=Hardware" --logger "console;verbosity=minimal" --verbosity quiet -m:1` | 57 通过、52 明确跳过、0 失败，总计 109；其中 51 项 Vulkan 呈现能力跳过，1 项 #129 尺寸回归跳过 |
| Temporal、边缘、灯光组合与视锥覆盖定向复验（命令见下） | 9 通过、9 个 Vulkan 能力跳过、0 失败 |

场景夹具、尺寸临时副本、诊断工具与独立验收目录已在使用结束后清理；最后清理了本票工作目录内 62 个 `bin` / `obj` 构建输出目录。

定向复验：

```powershell
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj --no-restore --filter "FullyQualifiedName~CanceledTemporalFrame|FullyQualifiedName~RemovedForegroundLeaves|FullyQualifiedName~MovingForegroundEdges|FullyQualifiedName~VsmAndTemporalCombinationsMatchTheFinalLight|FullyQualifiedName~StretchedGeometry" --logger "console;verbosity=normal" --verbosity quiet -m:1
```

## Vulkan 呈现问题记录

后续票标题：**修复 Vulkan 呈现功能选择并恢复场景脚本图像验收**。来源为 #82 的产品缺陷后续范围；复现基线为 `600da074a767bc42c890c244baabd11b94671080`，发现分支为 `codex/spec82-ticket-87`。

公开 `VulkanGraphicsBackend.Create()` 枚举硬件适配器后，`CreateDevice` 对 `Presentation | IndirectCommands` 返回 `NotSupportedException`，诊断为 `required Device features: Presentation`。每条 Vulkan 场景图像用例据此生成带原因的跳过。`RuntimeApplication.ApplicationDisplay.Initialize` 使用同一功能请求。

源码核对范围为 `VulkanDevice.GetSupportedFeatures`、`VulkanDevice.Create` 及 `RuntimeApplication.ApplicationDisplay.Initialize`。功能收集从 `IndirectCommands` 开始，后续设备、交换链扩展和 `Presentation` capability 的分支均依赖呈现标记；当前功能选择直接产生上述拒绝结果。

目标是核对 `VK_KHR_swapchain`、Win32 队列能力、设备功能选择与现有呈现路径，正确启用呈现，恢复 `Application.RunAsync(args, SceneScript)` 的有限帧图像交付。验收包括公开设备创建成功、#87 Vulkan 用例完成 Cluster/VSM/Temporal 图像比较，以及具备具体原因的能力跳过。

复现：`dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj --filter "FullyQualifiedName~RenderingFeatureTests" --logger "console;verbosity=normal" --verbosity quiet -m:1`。

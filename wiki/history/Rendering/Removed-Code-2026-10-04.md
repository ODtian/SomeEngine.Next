# 渲染代码删除记录 — 2026-10-04

任务：[删除死代码及其测试 #88](https://github.com/ODtian/SomeEngine.Next/issues/88)，来源规格 #82。
引用核对基线：`1339869ff7e721ad3d7bcc176d6de96d1f1131e7`。

## 删除前引用核对

核对覆盖 `src/` 全部引擎项目、`templates/`、`tools/`、`samples/`、`benchmarks/` 和 `tests/` 中的 C# 与项目文件；先检索类型，再沿提取、准备和帧执行入口读取调用者。

| 删除项 | 删除前引用闭包 | 对应测试 |
| --- | --- | --- |
| 实例集合及句柄 | `RenderMeshInstanceCollection.cs`、`RenderMeshInstanceSet.cs`、实例化组件和 `MeshRenderExtractor` 的专用分支；调用来自集合、实例集和实例化提取测试 | `RenderMeshInstanceCollectionTests` 3 项、`RenderMeshInstanceSetTests` 2 项、`InstancedMeshExtractionTests` 2 项 |
| 过程式实例来源及专用 CPU 数据 | `RenderInstanceSource.cs` 的来源接口、过程式来源、读取数据和变化日志服务于实例集；`RenderInstanceBuffer.cs`、`RenderInstanceUpdate.cs` 的生产引用来自实例集与彼此 | `RenderInstanceBufferTests` 2 项、`RenderInstanceUpdateTests` 4 项 |
| 实例化网格提取 | `InstancedMesh`、`RenderInstancedMesh` 的调用来自 `MeshRenderExtractor` 专用分支与实例化提取测试；应用在 `RuntimeApplication` 安装普通网格及灯光提取器，`RuntimeScene` 创建 `MeshInstance` | 与实例化提取测试一起删除 |
| CPU 渲染队列 | `RenderQueueSystem` 调用 `RenderQueueBuilder`；队列视图、计数、分类接口、写入器和排序工作项集中在这两个文件，调用来自队列构建器测试 | `RenderQueueBuilderTests` 1 项 |
| 实体分类 | `RenderEntityClassificationSystem`、`IRenderEntityClassifier`、`RenderEntityMembershipWriter` 的引用集中在定义文件 | 现有测试引用数为 0 |
| Translucency | `TranslucencyFeature` 与输出类型的引用集中在定义文件 | 现有测试引用数为 0 |
| Debug | `DebugFeature`、输出类型和显示枚举的引用集中在定义文件 | 现有测试引用数为 0 |
| 几何导出 | `StreamOutputFeature`、`RayTracingGeometryFeature`、输出类型与 `VisibleGeometryExportPass` 的引用集中在 `GeometryExportFeatures.cs` | 现有测试引用数为 0 |

共删除 12 个生产文件、51 个顶层类型（含委托）、6 个测试文件及 14 个测试方法，并清理 `MeshRenderExtractor` 的专用分支。删除后逐一提取这些类型的名称，在上述 C# 与项目文件范围复核，剩余引用数为 0。

## 当前生产通路核对

- `RuntimeApplication` 安装 `MeshRenderExtractor` 与 `LightRenderExtractor`，场景网格由 `RuntimeScene` 写入 `MeshInstance`，经 GameWorld → RenderWorld 提取后进入 Cluster 实例组合与 GPU 实例存储。
- `StableGrouping` 服务于 `RenderInstanceBatchBuilder`；`RenderInstanceDataTransfer` 服务于实例存储与 `ClusterPipelineSystem`。
- `RenderQueueCompletions` 服务于 `RenderDevice`、`RenderTimeline` 和 `RenderFrameCoordinator` 的 GPU 完成跟踪。
- `FeatureUploads` 被 `TemporalFeature` 调用；`FeatureResourceLayout` 被 Temporal 和 Lighting 调用；`FeatureUniformBuffer` 被 VirtualShadow 调用。
- 模板 `DefaultContent` 写入 `ApplicationSettingsBlock` 的透明合成与调试 shader 配置，相关字段属于现有资产序列化格式。Feature 类型的引用闭包为上述各自实现文件。

## 历史设计范围

实例集合、过程式实例来源、CPU 渲染队列、实体分类以及 Translucency、Debug、几何导出 Feature 已于本次删除。
相关页面的删除状态链接到本记录；2026-09-09 的交付与闭环核查页保留当时证据，并增加本次删除说明。

## 本次验收

验收环境：Windows x64、.NET SDK 10.0.112、Debug 构建。引用基线与本页开头一致。

- `git submodule update --init external/Alimer.Bindings`：使用锁定提交 `8d15f4e182f77df0c9af3aaf7ccd5dd8aeafc2a4`。
- `dotnet run --project tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj --no-build -- --engine .`：使用已构建的内容生成器，生成 67 个默认项目资产。
- `dotnet build SomeEngine.slnx --no-restore -v quiet`：构建通过，0 警告、0 错误。
- 补充构建 `benchmarks/SomeEngine.RenderGraph.Benchmarks`、`tools/SomeEngine.Job.Benchmarks`、`tools/RhiTypeGraph`、`samples/ImportedScenes/Materials`、`tests/SomeEngine.ECS.Benchmarks.Tests`、`tests/SomeEngine.Graphics.Benchmarks.Tests` 下的项目：六项构建全部通过，均为 0 警告、0 错误。
- `dotnet test SomeEngine.slnx --no-build --no-restore -m:1 --verbosity quiet --logger trx --results-directory .tmp/ticket-88-checks`：执行解决方案中的测试项目。ECS.Systems 首次执行在完成 152 项后停滞，结束该次测试进程，继续完成其余项目；该次全量命令退出码为 1。
- `dotnet test tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj --no-build --no-restore --verbosity normal --logger trx --results-directory .tmp/ticket-88-checks/ecs-systems --blame-hang-timeout 60s --blame-hang-dump-type none`：独立重跑通过，288 项完成，退出码为 0。
- 对两个补充基准测试项目分别执行 `dotnet test <项目路径> --no-build --no-restore --verbosity quiet --logger trx --results-directory .tmp/ticket-88-checks`：均通过，退出码为 0。
- `git diff --check` 与 `git diff --cached --check`：通过。

最终统计采用每个项目完整运行的结果：共 22 个项目，2538 项通过、3 项按条件跳过、0 项失败。ECS.Systems 采用上述独立重跑结果。

| 项目 | 通过 | 条件跳过 | 失败 |
| --- | ---: | ---: | ---: |
| `SomeEngine.Assets.Consumer.Tests` | 88 | 0 | 0 |
| `SomeEngine.Assets.Tests` | 241 | 0 | 0 |
| `SomeEngine.Core.Tests` | 58 | 0 | 0 |
| `SomeEngine.Diagnostics.Tests` | 20 | 0 | 0 |
| `SomeEngine.ECS.Benchmarks.Tests` | 7 | 0 | 0 |
| `SomeEngine.ECS.Fuzz.Tests` | 17 | 2 | 0 |
| `SomeEngine.ECS.Serialization.Tests` | 169 | 0 | 0 |
| `SomeEngine.ECS.SourceGen.Tests` | 39 | 0 | 0 |
| `SomeEngine.ECS.Systems.Tests` | 288 | 0 | 0 |
| `SomeEngine.ECS.Tests` | 540 | 0 | 0 |
| `SomeEngine.Graphics.Benchmarks.Tests` | 29 | 0 | 0 |
| `SomeEngine.Graphics.Direct3D12.Tests` | 333 | 1 | 0 |
| `SomeEngine.Graphics.Tests` | 1 | 0 | 0 |
| `SomeEngine.Graphics.Vulkan.Tests` | 45 | 0 | 0 |
| `SomeEngine.Job.Dots.Tests` | 5 | 0 | 0 |
| `SomeEngine.Job.Tests` | 181 | 0 | 0 |
| `SomeEngine.Render.Cluster.Tests` | 42 | 0 | 0 |
| `SomeEngine.Render.Tests` | 72 | 0 | 0 |
| `SomeEngine.RenderGraph.Tests` | 129 | 0 | 0 |
| `SomeEngine.Resources.Tests` | 14 | 0 | 0 |
| `SomeEngine.Runtime.Tests` | 17 | 0 | 0 |
| `SomeEngine.Serialization.Tests` | 203 | 0 | 0 |

条件跳过包括两个由环境变量指定的 ECS 模糊测试 campaign，以及一个要求 Intel D3D12 硬件适配器的用例。默认种子模型对照、D3D12、Vulkan 与 Cluster 渲染测试的完整项目结果见上表。

从干净工作树复现时，先构建 `tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj`，再生成默认内容并构建解决方案。本次验收汇总完成后，已清理 67 个生成资产、1 个生成 shader 布局、内容缓存与全部 TRX 结果，并恢复 56 个内容构建产生的导入记录。

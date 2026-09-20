# 资产重建执行记录

## 2026-09-16 最终检查点：43 项完成

本次接续直接读取 App Server 的两个 Thread，不恢复执行、不委派本机 Codex。原讨论停在组装 API，实际工作树已更晚，接续前最新任务表为 40/43。保存全部已有修改后，补齐来源复制相对依赖边界、Shader 显式依赖目录，以及额外公开回归发现的 D3D12 空提交设备丢失检查。

最终完整解决方案 Release 构建为 0 警告、0 错误；20 个程序集 2625 项通过、0 失败、3 条件跳过。按原有进程隔离另执行呈现 17 项和设备移除 1 项，均通过。资产 207、外部消费者 79、通用序列化 255、Render 85、Cluster 121 与实际消费者均包含在完整结果中。A31、A32、A33 因此关闭，原任务表 43/43 勾选，原验收要求未缩小。

用户再次强调原提示词后，本轮通过 App Server 实际重读两条对话（讨论 6 个 turn，执行 23 个 turn），核对磁盘上更晚的验收记录。没有仅凭勾选停止检查：两条新增公开回归先复现了进程内 Block 引用进入文件目录，以及重复关闭 Context 提前完成的问题。保存依赖现来自实际写出的 Block 描述；所有关闭等待者共用同一次实际清理结果。修复后的 19 项定向测试及上述完整方案均通过，资产原生 AOT 新增 `process-block=ok`。页管理、文件协议和渲染通路未改动。

Assets、Serialization、ECS 三套原生 AOT 均实际发布并运行；12 个 tools 项目逐项无警告构建。引擎目录之外的零产物模板从自己的来源生成 63 项资产；用户 C# / Slang 文本修改保留，删除必要源码会失败且不补回。移除 53 份衍生记录及目录文件后重新构建，全部资产身份与直接引用保持。本轮在新安装项目上再次执行这四种构建结果，均符合预期。正式 AssetCook 构建后从存储打开实际 Shader，两个后端各 2 个入口。

最终代码的独立默认项目与全部六种场景各完成 30 帧像素验证，共 210 帧，读取失败全部为 0。默认场景 6572 候选、4148 可见、49392 着色项；稀疏移动 1024 候选、640 可见、11608 着色项，只驻留 3/6 页。WARP 独立回归不代替完整应用；Vulkan 仅记非呈现回归。

正式真实 Texture 性能工具重新完成九组各 31 样本；未取得负载的保存额外解码为 0，类型查询分配为 0，16 MiB 局部修改仅复制当前页，释放并 Trim 后为 0。通用页混合生命周期与范围取得也执行完成；原参数取舍保持，文件缓存未受控的限制明确记录。

原 Importer、对象、生成、文件、Shader、任务、清单、边界与验证 Wiki 已按最后结构核对。以前的固定组装链草案不再作为实施依据；SG 不生成流程和业务关系，现有页管理与 Game world 到 Rendering world 通路保持。具体命令、数字、首次失败和修复、测量表见 [最终验证](Asset-Verification-2026-09-16.md)。未提交、未合并、未推送。

最终交付核对：当前 20 份主题及入口文档的 117 个本地引用全部有效，原清单正好 43 项勾选，`git diff --check` 通过。本轮清理本对话确认的 10 个临时目录、5825 个文件，包含本轮独立项目与结果，以及前一阶段遗留的 8 个工作树验证目录和临时日志目录。删除前逐一检查没有已跟踪文件、链接目录或正在使用这些位置的进程；App Server 读取副本、一次性脚本、原始测试与场景日志一并删除。正式源码、模板、工具及三套标准原生 AOT 发布产物保留。其他工作树与他人的文件未清理；分支仍为 `codex/asset-rebuild`，HEAD 仍为 `aa8d22fe`，未提交或推送。

## 最终回归之前的检查点（历史）

以下保留前一阶段原话。其“本轮”、委派、受阻及未关闭状态均不是当前对话的执行情况。

### 2026-09-16 最后检查点

当前不是“43 项全部验收完成”。原清单的 A00–A30、B00–B08 共 40 项实现与定向证据已收齐；A31、A32、A33 因最新完整解决方案测试被工具安全检查拦截而保留未关闭。A32 的代码路径清理、原主题 Wiki、旧入口文档和来源格式示例已实施，仍需 A31 后最终确认。完整当前结果与各阶段区别见 [2026-09-16 验证记录](Asset-Verification-2026-09-16.md)。

最后完整编译 0 警告、0 错误；12 个正式工具均无警告构建；Assets、Serialization、ECS 三套 Native AOT 实际发布并执行通过。新的 Importer AOT 覆盖 DI、设置、依赖去重、生产者冲突、同一 Mesh 多 Block、保存重开、Unicode 目录替换与已有读者保留，释放后活动页为 0。

本轮已实际通过 App Server 读取指定讨论 Thread，并找到真正的执行 Thread。Codex 子任务额度耗尽后停止原协调执行，直接使用本机工具继续，不购买额度。最新代码修复包括截断头的错误边界、材质及外部资产旧字段调用、重复共享贴图输出断言、Slang 原生对象释放、单目录文件并发切换、实际输出核验、旧 Slang 隐式导入移除、人工来源身份与衍生记录分离，以及项目根与库路径显式传入。

引擎工作树内及引擎目录之外两种位置均安装零产物模板，并从源生成 63 项资产；独立应用从无关当前目录启动仍读取自身内容。C# / Slang 用户修改不被覆盖；删除必要源码会失败而不补回；移除 53 份衍生记录和目录后，63 项资产的 ID 与直接引用保留。正式 AssetCook 通过同一 Shader ID 打开 DXIL / SPIR-V 各 2 个入口。

硬件 D3D12 的独立默认项目及 phong、factors、materials、wpo-alpha、displacement、sparse-motion 六种场景均实际完成 30 帧并通过像素检查。读取失败均为 0。最新外部项目末帧 6572 候选、4148 可见、49392 着色项、6/6 页驻留。Vulkan 窗口呈现仍是原有未实现边界；未完成的 WARP 尝试不列为本轮成功。

最后全量测试没有执行，未重试或拆分同一受拦截命令。早先 Assets 202、外部消费者 79、Serialization 255、Render 85、Cluster 121、Vulkan 非呈现 43，以及另一个阶段记录的全量数量，均不得替代最新代码的最终整体结果。任务剩余的是回归和依赖它的最终收尾，不是重新讨论导入器架构。


## 以下为按时间保留的实施历史

历史记录中的默认根目录生成、旧接口和阶段状态仅描述当时实现。当前默认工具只调用项目模板的唯一构建实现，不再接受旧的 `--rebuild` 根目录清理流程；当前模型与命令以上面的检查点和原主题 Wiki 为准。

2026-09-12：从 main aa8d22fe 创建 codex/asset-rebuild，工作树 worktrees/asset-rebuild。

已复制所有已跟踪未提交修改及 src、tests、tools、assets、samples、benchmarks、docs、wiki 中未跟踪源文件；逐文件 SHA-256 比对一致。原工作区文件未修改。历史 .agent-runs、.codex 审阅结果及 harness 临时产物没有复制或删除。

## 开始时已有修改

```text
 M docs/AssetSystem.md
 M src/SomeEngine.Assets.Importers/Importers/ClusterBuilder.Raw.cs
 M src/SomeEngine.Assets.Importers/Importers/ClusterBuilder.cs
 M src/SomeEngine.Assets.Importers/Importers/DisplacementBoundsTexture.cs
 M src/SomeEngine.Assets.Importers/Importers/GltfSourceImporter.cs
 M src/SomeEngine.Assets.Importers/Importers/SlangDeps.cs
 M src/SomeEngine.Assets.Importers/Importers/SlangShaderImporter.cs
 M src/SomeEngine.Assets.Importers/Importers/SlangSourceImporter.cs
 M src/SomeEngine.Assets/Asset.cs
 M src/SomeEngine.Assets/AssetAttribute.cs
 D src/SomeEngine.Assets/AssetFormat.cs
 M src/SomeEngine.Assets/AssetLoader.cs
 M src/SomeEngine.Assets/AssetProject.cs
 M src/SomeEngine.Assets/AssetRecord.cs
 D src/SomeEngine.Assets/AssetWriter.cs
 M src/SomeEngine.Assets/Pipeline/MeshPayloadSource.cs
 M src/SomeEngine.Assets/Schema/ApplicationSettings.cs
 M src/SomeEngine.Assets/Schema/AssetContracts.cs
 M src/SomeEngine.Assets/Schema/ClusterShaders.Loading.cs
 M src/SomeEngine.Assets/Schema/ClusterShaders.cs
 M src/SomeEngine.Assets/Schema/Material.cs
 M src/SomeEngine.Assets/Schema/MaterialData.cs
 M src/SomeEngine.Assets/Schema/Mesh.Fields.cs
 M src/SomeEngine.Assets/Schema/Mesh.Loading.cs
 M src/SomeEngine.Assets/Schema/Mesh.Streaming.cs
 M src/SomeEngine.Assets/Schema/Mesh.cs
 D src/SomeEngine.Assets/Schema/RenderScene.Loading.cs
 D src/SomeEngine.Assets/Schema/RenderScene.cs
 D src/SomeEngine.Assets/Schema/RuntimeConfiguration.Loading.cs
 D src/SomeEngine.Assets/Schema/RuntimeConfiguration.cs
 M src/SomeEngine.Assets/Schema/Shader.Fields.cs
 M src/SomeEngine.Assets/Schema/Shader.Loading.cs
 M src/SomeEngine.Assets/Schema/Shader.Programs.cs
 M src/SomeEngine.Assets/Schema/Shader.cs
 M src/SomeEngine.Assets/Schema/ShaderRef.cs
 M src/SomeEngine.Assets/Schema/StandardMaterial.cs
 D src/SomeEngine.Assets/Schema/StandardMaterialData.cs
 M src/SomeEngine.Assets/Schema/Texture.Chunks.cs
 M src/SomeEngine.Assets/Schema/Texture.Create.cs
 M src/SomeEngine.Assets/Schema/Texture.Fields.cs
 M src/SomeEngine.Assets/Schema/Texture.Loading.cs
 D src/SomeEngine.Assets/Schema/Texture.cs
 M src/SomeEngine.Assets/Schema/UnlitMaterial.cs
 D src/SomeEngine.Assets/Schema/UnlitMaterialData.cs
 M src/SomeEngine.Assets/Schema/VirtualShadowMapShaders.Loading.cs
 M src/SomeEngine.Assets/Schema/VirtualShadowMapShaders.cs
 M src/SomeEngine.Assets/SomeEngine.Assets.csproj
 M src/SomeEngine.ECS.Serialization/DataWriter.cs
 M src/SomeEngine.ECS.Serialization/Options/SerializationOptions.cs
 M src/SomeEngine.ECS.Serialization/TopologyCodec.cs
 M src/SomeEngine.ECS.Serialization/TopologySerializationRegistry.cs
 M src/SomeEngine.ECS.Serialization/WorldSerializer.cs
 M src/SomeEngine.Generators/AssetGenerator.cs
 M src/SomeEngine.Generators/BinaryContractGenerator.cs
 M src/SomeEngine.Generators/MaterialGen.cs
 M src/SomeEngine.Runtime/RuntimeApplication.cs
 M src/SomeEngine.Runtime/RuntimeScene.cs
 M src/SomeEngine.Serialization/Containers/BinaryDocument.cs
 M src/SomeEngine.Serialization/Containers/BinaryDocumentWriter.cs
 M src/SomeEngine.Serialization/Streaming/ChunkRequestScheduler.cs
 M src/SomeEngine.Serialization/Streaming/DocumentChunkBatchLoader.cs
 M tests/SomeEngine.Assets.Consumer.Tests/DiskAssetBehaviorTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/AssetArchitectureAcceptanceTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/AssetManifestTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/AssetOpeningTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/AssetPackStorageTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/AssetProjectTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/AssetProjectValidatorTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/AssetStorageTests.cs
 D tests/SomeEngine.Assets.Tests/Assets/AssetTestFiles.cs
 M tests/SomeEngine.Assets.Tests/Assets/AssetTypeExtensibilityTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/AssetWriterAcceptanceTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/CookedClusterShaderContractTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/DisplacementBoundsTextureTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/GltfSourceImporterTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/MaterialFileTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/MaterialReloadTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/ReadingSample.cs
 M tests/SomeEngine.Assets.Tests/Assets/ShaderFileTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/SlangEntryPointResourceUsageTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/SlangInstancePropertyTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/SlangIntegrationTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/SlangShaderCookProfileTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/SlangShaderTextureShapeTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/TextureMipTileStreamingTests.cs
 M tests/SomeEngine.Assets.Tests/Assets/WaveQueueCompilationTest.cs
 M tests/SomeEngine.Assets.Tests/IcoSphereTest.cs
 M tests/SomeEngine.Render.Cluster.Tests/ClusterAssetLoaderTests.cs
 M tests/SomeEngine.Render.Cluster.Tests/ClusterPublicationTests.cs
 M tests/SomeEngine.Render.Cluster.Tests/ClusterRegistrationTests.cs
 M tests/SomeEngine.Render.Cluster.Tests/ClusterShaderAbiTests.cs
 M tests/SomeEngine.Render.Cluster.Tests/ClusterTestFiles.cs
 M tests/SomeEngine.Render.Cluster.Tests/CookedShaderTestProgram.cs
 M tests/SomeEngine.Render.Tests/AssetLoaderTests.cs
 M tests/SomeEngine.Render.Tests/CookedShaderTestProgram.cs
 M tests/SomeEngine.Render.Tests/RenderDeviceTextureTests.cs
 M tests/SomeEngine.Serialization.Tests/BinaryDocumentTests.cs
 M tools/GenerateDefaultAssets/Program.cs
 M tools/SomeEngine.AssetCook/Program.cs
 M tools/SomeEngine.Assets.AotSmoke/Program.cs
 M tools/SomeEngine.RenderScenarios/Scenarios.cs
 M tools/SomeEngine.RenderScenarios/SomeEngine.RenderScenarios.csproj
 M wiki/Home.md
 M wiki/architecture/Asset-Files.md
 M wiki/architecture/ECS-Architecture.md
 M wiki/architecture/Product-Boundary.md
 M wiki/architecture/Render-Boundaries.md
 M wiki/design/DRY.md
 M wiki/design/SRP.md
?? .agent-runs/
?? .codex/
?? harness/
?? src/SomeEngine.Assets.Importers/GlobalUsings.cs
?? src/SomeEngine.Assets/GlobalUsings.cs
?? src/SomeEngine.Assets/Schema/Material.Asset.cs
?? src/SomeEngine.Assets/WorldAsset.cs
?? src/SomeEngine.Render/Components/SceneComponents.cs
?? src/SomeEngine.Render/SceneSerialization.cs
?? src/SomeEngine.Serialization/Containers/BufferRead.cs
?? src/SomeEngine.Serialization/Containers/SharedBytes.cs
?? src/SomeEngine.Serialization/Containers/SharedData.cs
?? src/SomeEngine.Serialization/IO/RangeReadStream.cs
?? tests/SomeEngine.Assets.Tests/Assets/EditableAssetTests.cs
?? tests/SomeEngine.Runtime.Tests/WorldAssetTests.cs
?? tests/SomeEngine.Serialization.Tests/RangeReadStreamTests.cs
?? tests/SomeEngine.Serialization.Tests/SharedDataTests.cs
?? wiki/architecture/Assets/
```

## 按修订计划重新开始

2026-09-12：重新读取原工作区更新的 Asset-Rebuild-Principles.md、Asset-Rebuild-Tasks.md 和 Asset-Serialization-Review.md，并同步到本工作树。现在以 A00–A33、B00–B08 的统一清单为准；旧计划的完成状态、测试和测量不转计为新任务完成。

已按用户要求移除本轮错误实现：SomeEngine.Content、BlockFile、重复的 ContentSource / ContentReader、同义 AtomicFile、独立预算的 PagePool / PageBuffer，以及它们的 Curve 样例、专用测试、性能和 AOT 工具。7 个项目引用与新建目录的构建输出一起移除，解决方案恢复原有项目集合。原工作区带入的 SharedData、SharedBytes、BufferRead、RangeReadStream 及原有容器/测试均保留。新计划没有删除这些通用能力的授权。

已知冲突与第一批最小修改：

| 范围 | 复用能力 | 冲突 / 依据 | 最小修改与验证 |
| --- | --- | --- | --- |
| B00 / B01 文档与编码视图 | 已有文档解析、SHA-256、包认证、范围保留 | 两份打开流程导致视图遗漏外部包认证（D01） | 共用头、目录、根、来源与认证读取；之后分别复制根或转交范围建立视图。签名包两入口、篡改自洽嵌套文档和失败清理测试 |
| B00 / B02 调度与账本 | 同键去重、四类预算、独立 pin 与 LRU | 共享账本不足但局部额度未满时不淘汰（D02） | 在既有账本/调度器内修复回收与通知；不再新增调度器、账本 |
| B00 生成独立性 | 原有静态编码生成和注册 | 通用生成注入资产字段（D07） | 随 A01 / A14 的声明接入解除；不通过移动旧注入代码或添加兼容分支宣称完成 |

其余任务按新清单继续推进，本记录只记实际证据；没有完整验收的任务不勾选。

## 首批实现与验证

- B00 / B01：对象读取与编码视图共用 BinaryDocument.ReadContentAsync。共同执行头、类型目录、根摘要、来源版本与外部包认证；对象路径复制根，视图转交原范围，不增加字段根或另一套容器。新增 4 个用例覆盖合法签名包、替换为内部摘要自洽的文档、错误根类型以及两个入口的失败来源释放。
- B02：复用现有 ResidencyBudgetLedger 和 ChunkRequestScheduler。局部或共享预算不足时回收无活跃使用的内容；账本请求其他调度器回收自身可释放的缓存，调用发生在调度锁之外。取消、pin 释放、加载成功/失败和账本释放通知关联等待者；同一次变化唤醒全部等待者。两类预算申请共同判定，避免部分申请回滚造成隐含占用。新增 5 个用例覆盖同调度器/跨调度器自动回收、跨调度器 pin 释放与等待者取消隔离、失败释放及一次通知推动多个请求。
- B03：移除文档和文档调度接入中仅因来源驻留就禁止解码的规则。明确目标读取仍直接写入调用者一次提供的 Memory；压缩或不能安全借用的来源使用一份最终解码/读取缓冲。原来源、版本、完整性检查与分项预算保持。新增 10 组文件、内存、映射、不保证不可变借用、HTTP 范围来源的 None/Brotli 一致性验证，并更新原映射来源拒绝测试。

任务状态：A00 已核对完成。B00 的生成器反向依赖和资产声明接入仍未完成，B01–B03 的上述修正已落地并验证，但不把局部交付转计为完整前置或整个资产重构完成。Mesh 使用侧等剩余来源/生命周期连接仍按 A09/A25/A28 处理。

检查环境：Windows x64，.NET SDK 10.0.112。

| 命令 | 实际结果 |
| --- | --- |
| `dotnet test tests/SomeEngine.Serialization.Tests/SomeEngine.Serialization.Tests.csproj --no-restore --nologo -v minimal` | **168 通过、0 失败、0 跳过**；保留原 149 例的有效回归，新增 19 例；仅更新 B03 明确改变的拒绝语义。 |
| `dotnet test tests/SomeEngine.ECS.Serialization.Tests/SomeEngine.ECS.Serialization.Tests.csproj --no-restore --nologo -v minimal` | **168 通过、0 失败、0 跳过**。 |
| `dotnet publish tools/SomeEngine.Serialization.AotSmoke/SomeEngine.Serialization.AotSmoke.csproj -c Release -r win-x64 --nologo -v minimal` | 成功生成原有 Native AOT 工具。 |
| `tools/SomeEngine.Serialization.AotSmoke/bin/Release/net10.0/win-x64/publish/SomeEngine.Serialization.AotSmoke.exe` | 成功运行，输出 `serialization-nativeaot-smoke:ok`；包括静态编码、视图、类型注册、原生块及文档压缩块。 |
| `dotnet build SomeEngine.slnx --nologo -v minimal` | **未通过**。空的 Alimer 子模块检出已按仓库记录的版本补齐；随后在 SlangShaderSharp 还原时遭遇 `Microsoft.Build.Tasks.Git 8.0.0` 的 NU1902 审计警告被视为错误。未更改第三方版本或关闭审计，不能声称完整解决方案构建通过。 |
| `git diff --check -- src/SomeEngine.Serialization tests/SomeEngine.Serialization.Tests` | 通过。 |

本批没有修改 SEBDOC03、包格式、静态编码或磁盘类型身份。新测试使用既有公共读写/范围/签名/调度 API，临时文档由测试在结束时删除。Direct3D12/Vulkan 实际应用均未运行；所有资产迁移、B04–B08 及其他未完成任务继续保留在清单中。

## 后续实施决定

- B04：在原 BinaryDataReader / BinaryDataWriter / BinarySerializer 上接入 IBufferWriter 与 ReadOnlySequence，不新增编解码框架。每份编码值沿用 Int32 定位范围；文档/包整体仍使用 64 位偏移。请求连续 Span 跨段时仅复制被请求的范围并记入读取分配限制；普通标量、字符串及拥有型字节字段直接读取段内容。
- B05：文档与包输出改用具备写入、定位和调整长度能力的 Stream；复用现有回填和摘要。包的签名/非签名文件发布共用原有原子保存流程，在最终替换前检查取消并支持旧文件读者继续持有句柄。
- B08：现有头只保存根 schema/epoch，目录中可同时存在不同 TypeId 但相同 schema/epoch 的条目，不能据此识别根。最小协议修正是明确规定类型目录第 0 项就是根，其余类型保持排序；头仍为 128 字节、块目录项仍为 96 字节。容器版本升为 4（SEBDOC04）以避免误读旧目录顺序；没有新增容器、包系统或旧格式回退。普通打开、编码视图、描述读取和包嵌套验证统一使用此规则。受影响产物在 A29 全部重建。
- A33 构建前置：移除 SlangShaderSharp 对 SourceLink 8.0.0 的显式覆盖，使用 .NET SDK 自带实现；最低 SDK 固定到本机已使用的 10.0.112，保留 NuGet 审计。依据：[SourceLink 官方 SDK 集成说明](https://github.com/dotnet/sourcelink#using-source-link-in-net-projects) 与 [CVE-2026-62900 修复版本](https://github.com/advisories/GHSA-23fw-v26w-5fgq)。没有改变 Slang 编译器版本。
- B06 复核：当前 BuildMembers 的最终步骤已按逻辑名称排序，前面的来源文件排序不决定编码；不能据 D09 的初始摘要无依据重排所有现有字段。因此保留默认的逻辑名称顺序与 BinaryName 身份规则，增加可选 BinaryFields 完整顺序声明、缺失/重复/未知名字诊断，并使真实继承字段参与编码。用不同 partial 文件路径及公开编码结果验证稳定性，不改写未选择显式顺序的 schema。

## 页、视图与文档接入进展

2026-09-12：以下是实际完成的通用能力及验证；A01–A04、B00、A12、A14 及实际资产迁移仍未完成，不能据此宣布完整重构完成。

- B07：生成视图在首次验证时记录变长字段的必要偏移，固定部分用常量偏移。访问末尾字段不再扫描前面的集合；没有另一份业务字段值。公开自定义验证入口计数、截断、释放和零分配访问测试通过。正式基准覆盖 0 / 1024 / 262144 个前缀元素，首次建立成本与重复读取成本分别记录。
- A05–A08、A10 的通用基础：新增 PageArena、PageBuffer、PageList、PageRange、PageBufferWriter，均位于现有 Serialization 项目。采用分级托管分配、共享平衡索引、路径和页 COW；没有恢复独立预算或重复调度器。Duplicate 只保留根，范围读取只保留相交页。批量写是不可重入的同步 scoped Span 回调，标准缓冲区写入使用有界暂存使可写别名无法改变保留内容。真实资产公共边界还需 A14 接入。
- A09：现有 ChunkRequestScheduler 增加 PageArena 目标，预留的解码容量直接转交给最终页，不先释放再申请，不重复记账。现有 BinaryDocument 范围读取 / Brotli 解码直接填充这些页；页范围和分支可独立于缓存和文档存活。取消时等待实际 I/O 结束，关闭来源、缓存或 Arena 均不会提前回收活跃保留。覆盖清洁缓存隔离、跨文档版本、去重、等待者取消、失败重试、跨缓存压力和仅剩自身引用时原地写入。
- A13 通用输入适配：BinaryDocumentWriter.AddChunk 接受 ReadOnlySequence，沿用现有摘要 / 压缩 / 回填，分别验证未压缩与 Brotli 的分页输出和连续内存逐字节一致。没有新增文件容器。
- A11 初步测量：同一正式工具覆盖 4 / 16 / 64 KiB 最大页，128 B / 8 KiB / 2 MiB / 16 MiB 内容，分支分配均为 72 B；首写只复制目标页。顺序、随机跨页、批量和普通值元素访问已记录；256 KiB 竞技场下的 1 字节保留仍可能保留整数组。混合生命周期、异步周转及实际资产负载尚未完成，默认参数暂不作为最终选择。
- 当前已通过 Serialization 211 项、ECS.Serialization 168 项。Native AOT 冒烟已包含标准分段编码、页集合分支、真实页调度 / Brotli 解码、关闭缓存和文档后的保留读取及预算释放，发布并执行成功。完整方案构建及所有资产、实际图形验收尚未完成。

公共 API、分配 / 复制规则和使用期间见 [AssetPages.md](../../../docs/AssetPages.md)。

## 独立声明接入前的生成清理

- 已删除旧 AssetGenerator.cs：从 T 镜像 XXXData、嵌套 `.Asset`、SharedData 字段根、每字段 ByteEdit 及注入语法树的整个生成实现均已移除。BinaryContractGenerator 恢复只接收显式 BinarySerializable 声明，删除专用于虚拟注入文件的诊断位置重写。没有保留旧注入分支。
- 通用生成类增加 IBinaryObjectReader / ReadInto，BinarySerializer 和 BinaryDocument 提供对应目标解码入口。可写 List / IList 原实例被复用，可直接填充 PageList，避免先创建整份 List 再复制；这不是 Asset 特例，也不增加字段 DTO。读取失败可能留下部分修改，资产打开应使用尚未对外提供的新对象并负责失败清理。
- 编码生成按实际 C# 可访问性接受 setter，包含派生类型可访问的受保护 setter；不要求为 ID 等字段开放公共赋值。继承、明确顺序及原有编码仍按 B06 规则。
- 目标对象不重建、页集合实例不替换、受保护身份字段、无额外根 I/O 和读取限制等公共行为通过，Serialization 当前为 214 项。
- 删除旧生成之后，全解决方案构建当前有 58 个不同错误，集中于尚未迁移的业务 partial 属性和嵌套 `.Asset` 引用。这是本次仍必须完成的 A01–A04 / A14 / A22–A29 工作，不是已交付状态。不得恢复旧生成器来消除这些错误。
- 当前 PageList 只支持 unmanaged 值。真实资产中的引用值及嵌套集合仍需明确可共享语义；不能将任意可变 CLR class 浅复制进页后宣称完成深层 COW。独立资产、T 的依赖方向、可读前端、目录/构建和全部消费者都尚未完成。

## 用户补充的命名调整

按最新指示，属性名称改为 BinarySerializableAttribute，静态读写接口改为 IBinarySerializable，调用入口和源文件改为 BinarySerializer。源码生成识别、手写实现、全部调用方、外部示例、测试与相关文档同步更新，共涉及 40 份源码/文档；没有添加旧公共名称别名。已有 TypeId 与 schema 计算中的格式标识字符串保持其协议作用，这次命名调整不改变编码字节。

命名复核已完成：源码、测试、工具与文档中三个旧公共名称无残留；XML 注释对应“生成序列化支持”“类型自身提供静态读写”“调用静态读写进行编码和解码”。本次回归为 Serialization **221 通过**、ECS.Serialization **168 通过**，Native AOT 重新发布并执行成功，输出 `serialization-nativeaot-smoke:ok`。ImmutableArray 行为测试改用元素序列比较，避免将具有相同内容的不同底层数组误判为解码失败。上述验证不代表尚在迁移中的完整 Assets 解决方案已经通过构建。

## 独立资产与使用类型的实际接入

2026-09-12：本节更新此前的中间状态，不将局部通过转计为全清单完成。

- A02 / A22：实际 Shader 使用类型及共享资源值定义移到 SomeEngine.Resources；该项目引用 Graphics、Serialization 和编译期生成器，不引用 Assets。Shader 已移除 BinaryDocument、文件读取门及 LoadBackendAsync，使用对象只保存已取得的 ShaderProgram 和业务元数据。入口匹配、反射检查、材质布局与实例属性校验保留。Shader.Update 保留对象身份并通知所有观察者。既有值类型的 LogicalName 及枚举 BinaryName 显式保存原磁盘身份，避免仅因命名空间移动改变 schema。
- A14 / A22：独立 ShaderAsset 直接声明内容，保留原 shader 文件类型名与 bytecode chunk key 算法。CreateAsync 可以选择后端，完成页读取及 SlangShaderSharp 原有 Stream 解码后才返回 Shader；UpdateAsync 显式替换已存在的使用对象。已编译代码的描述留在资产，使用对象直接持有 ShaderProgram，不再携带资产定位字段。导入器开始通过 PageBufferStream 保存编译结果和逐段摘要；其余导入元数据集合及消费者仍需继续迁移，尚未做端到端验收。
- A27：WorldAsset 改为不绑定 T 的 Asset，根直接使用自身的 Id / Name / AssetType / 内容长度 / 外部引用，不再有 WorldFile 字段镜像。写入与读取使用原 WorldSerializer，经 PageBufferStream 接入页。Duplicate 共享根，保存沿用 BinaryDocumentWriter，World 创建完成后不持有文件。启动配置改为独立 ApplicationSettingsAsset，并迁移直接启动消费者；组合着色器引用类型等待对应资产接入。
- A08 / A13：新增 PageBufferMap，实际数据是 ulong 到字节缓冲区的共享平衡索引，页仍由原 PageArena 持有；不新增预算或调度器。祖先或值共享时先分离必要路径，后续同页写复用已独占页。Asset.Chunks 保存明确提供或取得用于编辑的大块，和标量 / 引用 / 列表共用 PageAccess；冷读取仍通过原文件和调度器。保存过程中保留已修改块的所有 PageRange，未修改块由既有文档写入器复制。WorldAsset 已接入此共同持有方式，不另存大块根。
- B00 / A14：生成器通过已生成或手写的静态编码及视图验证接口支持引用程序集中的值，并在外层 schema 中使用其实际 schema。不存在把生成源码注入另一个生成器的分支。BinaryValue 标记由定义端检查可共享源码形状后生成，资产侧不根据外部类型缺失的私有元数据猜测可共享性。任意可变 CLR 对象仍只有普通引用语义；PageList 不承诺深复制它们。
- A05 / A08：PageBuffer<T> / PageList<T> 已使用真实 T[] 支持引用值，范围统一为 PageRange<T>，统计补充 BackingBytes。此前“PageList 仅 unmanaged”的描述已由当前实现取代。

实际验证：新增 PageBufferMap 的结构操作、祖先共享与页复制、保留范围、同页后续写和兄弟字段互斥；PageBufferStream 的定位、截断、扩张清零与文档回填；引用程序集的对象 / 视图 / Nullable 值读取及 schema 依赖；非法 BinaryValue 源声明。ECS 的分页输出与连续输出逐字节相同，恢复后不依赖原页或来源。

当前通用验证结果：Serialization 229 项通过（包含枚举移动身份回归），ECS.Serialization 169 项通过。扩展的 Native AOT 包含类型化引用页、ImmutableArray、共享大块索引、跨字段写互斥与分页流，重新发布并运行成功。SomeEngine.Resources 独立构建通过。Assets 项目最近构建仍有 75 个不同错误，集中于组合着色器、Texture / Mesh / Material 的旧声明与消费者；该失败状态仍属于本次必须完成的迁移，不能通过排除文件或恢复旧生成器规避。

A11 重新运行正式性能工具后，128 B / 8 KiB / 2 MiB / 16 MiB 的字节分支分配均为 104 B，首写复制量仍是所选页容量。加入 PageAccess 后，历史的 72 B 已不代表当前实现。4 / 16 / 64 KiB 页的 2 MiB 批量 COW 中位数本次约为 1935 / 522 / 1240 μs；参数仍需混合生命周期和真实资产负载测量。重复视图字段读取零分配，原生块写入与直接复制带宽中位比为 98.68%。性能结果只支持已测能力，不构成完整资产架构性能验收。

尚未完成：外部 CurveAsset / Curve 公共闭环、所有实际资产和消费者、可读前端、目录 / 构建流程、默认资产重建、完整解决方案与实际 Direct3D12 / Vulkan 验收。A00 以外清单项继续按实际前置与完整验收决定勾选。

## 组合着色器接入

2026-09-12：新增独立 ClusterShadersAsset、VirtualShadowMapShadersAsset，保留原文件类型名、扩展名以及所有角色 / 阶段 / 入口验证。旧的 ClusterShadersData、VirtualShadowMapShadersData 和嵌套资产入口已移除。两类资产在显式 CreateAsync 中取得依赖，局部字典仅用于本次构建去重和失败清理。

实际使用类型位于 SomeEngine.Resources，使用 ShaderEntry 直接引用已取得的 Shader。ClusterShaders 保存 ShaderOperation 列表，VirtualShadowMapShaders 保存各个用途的入口；它们不维护资产 ID 查询表，也不调用 AssetLoader。渲染管线消费这些直接引用，保留原有阶段组合、单一 Shader 检查、管线选择和构建逻辑，没有改变顶点布局或 Game world → Rendering world 数据通路。

资产引用生成补充显式标记的 GUID 文本、Nullable 引用以及嵌套值中的公共字段和继承成员；普通字符串 / GUID 不自动变为依赖。Asset 的公共身份和类型访问也加入共同的 PageAccess 互斥，避免目标解码在批量写回调期间先修改公共头再在后续字段被拒绝。

正式 SomeEngine.Resources.Tests 项目只引用实际 Resources 项目，两个公共行为测试通过，验证组合入口中的直接对象引用及依赖释放。该验证不代替资产打开 / 保存 / 加载的完整端到端回归。Assets 项目当前仍有 56 个不同编译错误，主要来自 Texture、Mesh、Material 的旧声明和消费者；不通过排除这些文件或恢复旧生成分支消除失败。

后续仍需核对外部 ShaderProgram 自有字节数组在 T 有效期间的 CPU 预算、所有资产的创建与更新期间，以及清洁内容与编辑页的保留范围。当前页测试不作为这些完整使用对象预算和生命周期要求的验收证据。

## Texture 接入

2026-09-12：独立 TextureAsset 已直接声明形状和 tile 元数据。Texture 使用对象移至 SomeEngine.Resources，只持有已取得的 PageBufferMap 像素页、形状和设备资源；移除 T 中的 BinaryDocument、ChunkRequestScheduler 和后续文件读取。显式 TextureAsset.LoadMipAsync 取得内容后调用 T.AddPixels，普通上传只读取已有页。公共流送计数从 T 移到资产 / 文件，仍使用原调度器计数器。

TextureMipTile 保留原二进制身份、坐标和存储布局，移除被 BinaryIgnore 忽略的可变 Payload 别名。过程式创建使用 TexturePixels（描述和输入字节），实际像素复制到共同页；从资产创建则共享已取得的页。页仍计入现有 ResidencyBudgetLedger，T 和独立 PageRange 的使用期间决定最后一次归还。

原 tile 排列、语义键、尺寸 / 格式 / pitch、cube / array / depth 和 bootstrap 覆盖验证已转移至共同 TextureTiles。保存时在隔离分支上规范排序及语义键，保留坐标编辑后的原块映射、4096 字节对齐和 ordinal；未修改来源块继续用 BinaryDocumentWriter.AddChunkAsync 复制，修改页的保留持续到写入结束。打开已有文件时仍严格验证已保存的 tile 元数据，不把异常文件当作普通待规范化编辑。

设备更新继续使用既有完成 / 取消边界。RenderDevice 读取 T.TryRetainTile 返回的页段并按原 RHI footprint 逐行拷贝；SequenceReader 从当前位置前进，不为每行重新扫描全部前缀，也不先拼接整块像素。min/max displacement 与 glTF 纹理创建已改为写入共同页，原采样 / 分区 / 图像解码算法保持。默认资产工具和部分外部调用样例已同步；其余消费者和原测试仍需完整编译后逐项接入。

SomeEngine.Resources.Tests 当前 9 项通过，包括此前 2 项组合着色器测试及 7 项 Texture 用例：输入 / Arena 关闭后的像素保留及预算归还、显式 mip 追加、旧读取跨内容替换、取消清理、cube / cube array / volume 内容和非法采样 / 层数。Assets 项目当前仍有 39 个不同错误，集中于 Mesh / Material，尚不能宣称 Texture 资产端到端或实际 GPU 上传回归已经通过。

待完整验收继续覆盖：多块取得时的预算与自身保留交互（当前容量预检不能代替批量取得审查）、所有旧 Texture 调用 / 测试、默认内容重建以及两种图形后端。没有恢复旧 TextureData、嵌套资产或 T 内部文件读取入口。

## Material 接入

2026-09-12：Material、StandardMaterial、UnlitMaterial 已移至 SomeEngine.Resources。使用类型不再引用 Asset / AssetLoader 或文件编码类型。MaterialGen 只生成实际属性、静态属性访问和复制代码；从 T 镜像嵌套资产及自动登记资产构造器的路径已删除。MaterialType 显式保留两类内置材质原身份，TypeId 缓存计算结果，避免每次属性读取都重新做 SHA-256。

新增独立 MaterialAsset / StandardMaterialAsset / UnlitMaterialAsset。当前分支的类型、Shader、Parent 共用一张小页，参数使用 PageList<MaterialParameter>，不再有 Fields / MaterialData 根或整个 List 的首写复制。具体资产属性在源码中独立声明，中央 SetParameter 负责页 COW。父级循环、类型不一致、同名覆盖、Reset、纹理依赖去重和失败释放保留；实例创建完成后只向 T 交付实际 Shader、Texture 和标量值。单一材质类型登记移到资产侧，普通 T 的复制使用生成的直接构造代码，不再维护第二份构造器登记。

原参数 discriminant、Sampler、向量和矩阵编解码保留在 MaterialParameter 中。文件根直接编码 MaterialAsset，不再经 MaterialData；为公共 Id / Name / AssetType 前缀更新材质根 schema，epoch 为 3。原根类型 ID 与材质类型 ID 保持，参数标签不重编号，也不增加旧格式回退。默认资产仍需按 A29 重建。

共同 Asset 新增 CreateCopy，明确创建新身份并共享内容；Duplicate 继续保留身份。glTF 从模板创建新资产已使用 CreateCopy。保存现有路径时，在原文档解析 / 完整性验证后检查目标 ID，拒绝覆盖另一个资产身份。旧格式生成产物不借此绕过格式检查，重建流程仍需明确处理。

Resources 的 12 项测试通过，新增材质类型身份、默认值、实际属性变更计数、更新提交后的全部观察者通知，以及错误类型 / 参数不改变当前值。当前 Assets 构建仍有 15 个声明错误，集中于 Mesh；材质文件、继承、外部自定义类型与完整导入回归尚未验收。

额外修复了通用解码结束检查的释放路径：T.Read 已创建对象但输入仍有多余字节时，Span 和 Sequence 入口会释放无法返回的 IDisposable 结果。两个公开行为用例通过。该修复不替代后续对生成构造 / setter 抛出异常期间清理的完整审查。

## Mesh 使用侧与几何基础接入

2026-09-12：将原有 MeshPageHeader、PackedCluster、ClusterBVHNode、MeshPayloadLayout 和 VertexEncodingCompatibility 移入 SomeEngine.Resources.Geometry，保持字段、布局、位宽、页尺寸和算法，不增加任何默认顶点属性。引用方同步使用新的命名空间。

新增仅访问已驻留数据的 SomeEngine.Resources.Mesh：持有共同页索引中的 BVH 和已取得几何页，缺页同步访问明确失败，AddPages 显式补充并验证页头、长度及根认证摘要。更新保留对象身份和版本检查，已有 PageRange 保留不因输入、Mesh 或 Arena 关闭而失效。MeshIntegrity 复用原页布局检查，保留不透明顶点编码匹配、BVH 分界与节点整除约束，并以页段计算摘要。页摘要的存储改为 ImmutableArray，避免资产共享值中的可变 Memory 别名。

Resources 项目和测试通过，目前共 15 项（新增 3 项 Mesh 行为测试）。测试使用公开几何布局和公开 Mesh / 页 API，验证输入关闭后的读取与预算释放、缺页显式供给、错误摘要拒绝、更新版本以及错误编码和 BVH 分界。

尚未接入：MeshAsset 独立声明、文件侧范围读取、原 MeshPayloadSource 与 Render.Cluster 注册 / 流送消费者、导入器和默认内容。旧 Assets/Schema/Mesh 的剩余实现还在迁移中；不通过恢复旧生成器或全量预加载来让其编译。下一步必须保持原有按页取得负载，并将其变为资产侧的显式操作，清除 T 内部来源。当前 Resources 的通过不等于 Mesh 资产或完整解决方案验收通过。

## Mesh 资产、外部消费者与导入器接入

2026-09-12：本节更新上一节尚未接入项的实际状态，不代表 A25 或整个任务验收完成。

- A25：独立 MeshAsset 已直接声明 Bounds、顶点编码、负载位置、页摘要、BVH、Regions 和材质引用，移除旧 MeshData 及 Assets/Schema/Mesh 的五份冲突实现。SetPayload 从共同 PageBuffer 推导几何完整性信息；Duplicate / CreateCopy 沿用共同字段与块的页分支。原负载 chunk key、原生布局、不透明顶点编码、无压缩和 4096 对齐保持。
- A02 / A25：CreateAsync 只取得必需 BVH，LoadPagesAsync 显式补充指定几何页，UpdateAsync 更新已有 Mesh。Mesh 保留真实 Regions 数据；重新打开同一文件后以摘要内容比较页描述，不依赖 ImmutableArray 的对象身份。
- A09 / A25：MeshPayloadSource 变为明确返回给调用方的资产侧访问，支持原文件范围和已供给的驻留页。文件范围继续使用 BinaryDocument 的 OpenChunkRangeSourceAsync，并保留同一 AssetFile，未新建文档解析器或缓存。来源关闭、重复访问及异步读取的引用保留继续覆盖完整操作。Resources.Mesh 不保存此对象。
- B03 / B04：PageBuffer.ReadAsync 将指定 IRangeSource 范围直接填充到新页，沿用内部受控填充和原预算。取消或失败必须等当前来源停止写入后才能释放页；没有开放可逃逸的公共 Memory 写回调。
- B00：修复跨程序集可空引用值生成的静态调用，类型符号不带可空修饰；字段本身的 null 编码保留。回归实际编译两个程序集，验证非空与 null 的对象、视图、目标读取及 schema 依赖。
- A29 / A30：外部消费者测试中的 DiskProbe、OtherProbe 和 CountedAsset 改为独立资产声明；DiskProbe 使用对象只保留创建时取得的页。保留所有既有版本变化、并发与取消、缓存淘汰、包读取、编辑隔离、保存及失败清理用例。新体系的静态编解码、派生属性与生成构造已通过实际外部程序集使用。
- A12 / A21：消费者回归暴露并修复 AssetLoader 对包版本的错误比较。AssetEntry.Version 是存储选定版本，包存储使用包的 generation，嵌套文档具有自己的 generation。OpenAsync 继续验证所选存储版本，文档继续验证根身份、schema 及包认证；不在加载器把两种 generation 当作同一个值。
- A23 / A25：ClusterBuilder 通过既有 PageBufferStream 直接输出几何页和 BVH，移除临时文件后整块分配并读回的路径；几何处理算法不变。glTF 改为 MeshAsset / CreateCopy / 资产类型引用。Slang 元数据通过不可变值替换保留标签、绑定、标量布局及实例属性；删除导入状态中重复保存的 Metadata，资产是主要持有者。反射输出通过已有 Create 入口一次构造不可变集合。

本次实际验证：Serialization 234 项通过；Resources 15 项通过；Assets.Consumer 25 项通过（原 22 项加 3 项实际 Mesh 文件行为）。Mesh 测试覆盖 64 KiB 以上几何只取得 BVH、显式页读取、重新打开后的摘要匹配、来源与资产关闭后的内容保留、驻留分支编辑隔离、损坏页拒绝且不改变 Mesh。Assets、Assets.Importers、Render 项目均构建通过；Assets 仍有既有 MaterialAssetTypes 的 CA2255 提示。

未完成的实际构建证据：Render.Cluster 仍在注册时调用已删除的 Mesh.TryRetainPayloadSource，必须把显式来源沿现有 Game world → Rendering world 数据通路接通，并保留已有按页流送与预算；不能靠恢复 T 中的来源或拒绝驻留数据解决。Assets.Tests 当前构建有 249 个错误，主要为旧 ReadingSample.Asset、旧 Payload / LoadAsync / UpdateAsync 和资产泛型引用等消费者，须逐项迁移并保留原行为覆盖。Slang 与几何导入器本轮只完成构建，业务算法回归尚待迁移后的测试验证。默认内容重建、全解决方案、完整 AOT、D3D12 / Vulkan、目录与源格式、其余 A00–A33 / B00–B08 交付和最终清理仍未完成，任务保持未勾选。

## 显式 Mesh 来源进入现有渲染通路

2026-09-12：MeshInstance 与 RenderMesh 增加可选 PageSource，MeshRenderExtractor 的完整提取和增量更新均沿现有 Game world → Rendering world 路径传递该引用。没有新增旁路或在 Resources.Mesh 中恢复文件访问。RuntimeScene 取得 Mesh 与独立 MeshPayloadSource，并将二者作为同一场景条目持有及清理。

ClusterMeshPrepareSystem 从 RenderMesh 收集明确来源，并在异步注册期间保留访问；结束及失败清理释放全部保留。ClusterMeshes 注册在排队前保留来源，核对来源与 Mesh 的页摘要 / BVH 内容，之后沿原 MeshPages、PageStream 和预算接入。必需 BVH 已在创建 Mesh 时取得，注册直接读取这些已驻留字节，不再从文件重复取得 BVH。

程序生成且几何页齐全的 Mesh 使用 MeshPayloadSource.FromMesh，保留共同页索引的分支；缺页时要求明确提供资产来源，不拒绝驻留输入，也不预加载整个文件。Mesh.Duplicate 共享现有页，后续添加和更新独立。外部行为测试验证来源在原 Mesh 更新、关闭后的内容保留，以及来源内容匹配和缺页拒绝。Assets.Consumer 现在 26 项通过，Resources 15 项通过。Runtime 项目及其传递依赖（包括 Render.Cluster 和组合管线）构建成功，源目录不再调用 TryRetainPayloadSource / TryBorrowPayloadSource。

组件提取的完整与增量 PageSource 传递用例已放入现有 RenderWorldExtractorTests，与该项目的提取/提交测试使用相同测试边界。该渲染测试项目尚未完成旧 API 迁移，因此此用例还没有执行通过，不能以构建替代其行为验收。Cluster 注册测试原先通过阻塞文件 BVH 读取验证排队和取消；迁移时需保持其并发、失败清理与目标存储验收，将文件读取前置到显式创建边界后选择相应的实际触发点，不恢复 T 内部 I/O。

Assets.Tests 的 ReadingSample 改为独立 ReadingSampleAsset 和普通使用类型；引用、构造身份与显式创建/更新调用同步迁移。ClusterBuilder / IcoSphere / LOD 测试读取公开 Chunks 与 PageRange，继续保留原顶点、几何布局、层级和持久化断言。部分 Shader 与 Material 测试按新数据归属和显式更新入口接入。实际构建错误从上一节 249 个降为 72 个；主要剩余 Texture 旧 Payload / ResidentChunkLease / 隐式加载接口、Shader ImportTrace 和旧文件测试夹具。该测试项目尚未通过，不减少既有业务覆盖来完成验收。

仍须完成：上述测试迁移并实际运行；渲染注册与提取行为、更新后的来源版本、场景关闭与在途工作生命周期；默认内容重建；全部工具、完整解决方案、AOT 与 D3D12 / Vulkan 验收；源格式 / 目录 / 构建及完整 A00–A33 / B00–B08 清单。目标保持进行中。
## 资产完整测试与默认内容重建

2026-09-12：Assets.Tests 已完成本轮旧 API 消费迁移并实际运行，151 项全部通过。保留 Texture 引导 mip、显式取得、输入排序确定性、输入分支不变、错误边界/重复/缺失负载/行距拒绝，以及材质继承、重置、观察者失败后的已提交依赖保留、模型几何和 Slang 编译/反射/默认着色器行为。测试夹具改用独立资产、Chunks、PageRange 和已取得的 Texture 数据；导入记录只从 ShaderAsset 读取。原有冲突身份测试按当前规则改为验证不同 ID 不能覆盖已有文件，且失败不改变文件或目录。

实际回归暴露并修复了通用 ReadInto 的复制型 setter 问题：目标列表为 null 或只读时，旧生成代码先赋值空列表再向临时列表写入，资产 setter 复制后会得到空内容。现在先完成新列表再交给 setter；已有可写列表仍直接复用。公共测试分别覆盖 null 和只读目标、复制型 setter、Span 和 Sequence 输入、后续读取保留既有列表。修复后 Shader 文件的反射列表恢复，实际纹理形状/标量/image 信息测试通过。当前 Serialization 全套 236 项通过，Assets.Consumer 26 项通过。此修复证明正确性；首次空列表经复制型 setter 的分配路径仍需纳入 A08/A11 的真实资产内存与性能审查。

ShaderAsset 的保存验证与创建 Shader 的可用性要求进一步分开：未编译的资产内容允许保存和检查，CreateAsync 明确拒绝缺失字节码。对应的项目打开测试同时验证不触发创建规则和显式创建失败，未放松已声明块的 key、长度和内容一致性验证。

GenerateDefaultAssets 已适配 MeshAsset，并增加明确的 --rebuild 选项。该选项只清理此命令负责的固定默认产物、列出的 Slang 输出及 IcoSphere 来源登记的输出；先统一验证所有路径为项目内的 .asset，再逐文件清理。源内容、源身份和目录保持，以原 ID 重新生成当前格式，不引入旧格式解析或绕过普通保存的身份检查。

实际执行：dotnet run --project tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj -c Release --no-restore -- --rebuild 成功，选择 63 个默认输出重建，生成纹理、着色器、组合管线、材质、模型、World 场景和启动配置，最终项目资产验证通过。随后重新运行 Assets.Tests，151 项全部通过，包含实际默认编译产物消费者。生成后的 .asset 与伴随内容是正式产物，保留在重构工作树；原工作区未被生成命令修改。

当前剩余证据：Render.Tests 构建仍有 53 个旧 API 错误，集中于 TestAssets 的不完整 Mesh/Texture 构造、CookedShaderTestProgram、材质默认字节与集合类型、Texture 读取及文件测试。其注册/提取/GPU 回归、Render.Cluster.Tests、Runtime.Tests、其余工具、完整解决方案与完整 AOT 尚未验收。默认生成成功与 Assets.Tests 通过不替代源格式/目录职责、外部 CurveAsset、多对多创建、共享预算与最终性能、清理和完整 A00–A33 / B00–B08 验收；目标继续进行中。
## Render 回归、纹理块身份与资产 Native AOT

2026-09-12：Render.Tests 的 53 个旧 API 编译错误已处理，完整 84 项测试通过。测试夹具创建具有真实几何/BVH 页的 Mesh、已供给像素的 Texture 和 SlangShaderSharp.ShaderProgram；材质默认字节按不可变值保存。加载测试在关闭文件前明确取得所需页，使用同一 PageArena/预算验证依赖和页的生命周期。纹理设备测试通过 TextureAsset.LoadMipAsync 取得额外 mip，验证随后帧上传、视图更新、多设备、取消和失败时原内容保留。第一帧不再期待隐藏的资产缓存访问，缓存与读取计数保持不变。

RenderWorldExtractorTests 中的显式 PageSource 完整/增量提取用例本次实际通过。Render.Tests 的 Windows 纹理用例实际创建了 D3D12 WARP 设备并完成帧操作；该结果只证明此测试覆盖，不代表完整 D3D12/Vulkan 场景验收。

本轮核对旧源码 aa8d22fe，发现此前命名空间移动误把纹理 chunk key 的格式前缀改为 SomeEngine.Resources.Texture.Mips；现恢复原 SomeEngine.Assets.Schema.Texture.Mips 前缀。新增公共 API 协议回归，验证原持久化名字的 key 和三参数 tile 坐标与六参数 array/face/depth 坐标的差异，同时修复测试夹具混用重载的问题。没有更换 key 算法。修复后再次用 GenerateDefaultAssets --rebuild 成功重建选定默认产物并通过项目验证，Assets.Tests 151 项、Resources 16 项通过。

SomeEngine.Assets.AotSmoke 已改为独立 SmokePaletteAsset 与使用对象，以及显式 Texture/Mesh 创建、页取得、材质继承和 World 内容分支。实际执行 dotnet publish tools/SomeEngine.Assets.AotSmoke/SomeEngine.Assets.AotSmoke.csproj -c Release -r win-x64，并运行其 publish 目录中的原生 exe 成功。扩展后再次发布与运行，输出包含 ready-after-close=ok、update=ok、extension=ok、mesh-file=ok、material-inheritance=ok、world=ok。Mesh 检查覆盖真实文件保存、创建只取得 BVH、显式补页、来源关闭后的读取和 COW 编辑。烟测临时目录由 finally 清理；正式发布产物保留在标准 bin 目录。此烟测仍不等于全资产类型/图形路径的完整 AOT 验收。

Runtime.Tests 完整 21 项通过。当前完整解决方案构建仍失败，报告 95 个错误，与 Render.Cluster.Tests 单独构建的旧 API 错误一致，涉及 ClusterTestFiles、ClusterTestAssets、CookedShaderTestProgram、MeshStreamingTests 和注册/取消测试等。下一步必须保留它们的按页范围读取、故障去重、完整性拒绝、并发注册和失败清理行为，将文件 I/O 的控制点迁到显式创建/流送边界；不能用全量预加载或恢复 Mesh 内部文件来源让测试通过。

整体 A00–A33 / B00–B08 清单、源格式/目录/构建职责、Curve 外部消费、多对多创建、共享预算和最终性能审查、剩余 Cluster 测试、完整解决方案、全 AOT 与 D3D12/Vulkan 场景验收仍继续，未标记目标完成。
## Cluster 全量回归与解决方案验证

2026-09-12：Render.Cluster.Tests 已完成旧 Mesh/Shader API 迁移，完整 121 项通过。MeshFileContent 测试夹具分开持有 Mesh 和显式 PageSource；注册接收来源，已取得 BVH 不再于注册阶段重读。几何故障去重、只读请求页、头/页/BVH 损坏拒绝、分页预算、发布与原生布局断言保持。增加必需 BVH 读取失败/取消的创建边界回归，验证来源关闭及重试成功。

注册并发测试将控制点从隐式文件读取改为最终 BVH 目标 Memory 的首次写入，仍验证同对象并发只复制一次、不同对象排队、错误来源/目标失败后的重试、取消/关闭等待在途写入结束、同一注册流程重入拒绝、更新后已发布旧来源保留，以及不同顶点编码共用驻留设施。没有新增产品专用测试钩子或把文件来源放回 T。字段错误测试继续断言错误 GUID 被拒绝且没有输出文件，不绑定发生验证的内部方法顺序。

实际完整解决方案构建成功。随后 dotnet test SomeEngine.slnx -c Release --no-build --no-restore --nologo -v:q 完成：20 个测试项目，2496 项通过、3 项跳过、0 项失败。各项目通过数：Core 58、Diagnostics 20、Graphics 22、ECS.Fuzz 17（另跳过 2）、ECS 534、ECS.Systems 287、Graphics.Direct3D12 405（另跳过 1）、Job.Dots 5、ECS.Serialization 169、Job 177、ECS.SourceGen 39、Graphics.Vulkan 43、RenderGraph 65、Render.Cluster 121、Render 84、Resources 16、Assets 151、Runtime 21、Assets.Consumer 26、Serialization 236。跳过项仍保留，不计为覆盖；可选 fuzz campaign 与硬件条件由既有测试属性决定，最终验收需要按实际范围报告。

后续失败生命周期检查又修复生成读取的一条路径：可释放可变类在 setter 拒绝解码值时，已完成构造的实例现在会被释放；清理也失败时保留原始与清理异常。该分支不改变编码字节，required/init-only 类仍保持原构造路径，不能将此局部修复当作所有自定义构造器和嵌套可释放对象的完整清理审查。新增 Span/Sequence 公开行为用例，Serialization 全套增至 238 项并通过；Assets 151 项复跑通过；完整解决方案重新构建成功（重新编译时仍有既有分析器/测试警告，增量构建的零警告不能代表仓库无警告）。

重新阅读任务与源码后的明确缺口：A12 可读源前端未实现；A22/A23 独立 .shader 的多源码/入口/特化选择未实现，工具仍以 .slang 导入一份产物；A18 源与伴随成组移动/复制和目录重建未完整接入；A20/A21 的设置、输出身份与可重建构建记录边界仍需整理；A30 使用边界分析器、CurveAsset/Curve 与多对多外部消费未完成；A11 最终页参数/混合生命周期性能以及其余 A31–A33 审查未完成。当前 build/test 通过只是已有实现的回归证据，不是任务清单完成证明。接下来优先补齐可读源与独立定义，再完成目录/构建与外部扩展验证。

## 可读源前端与独立 Curve 消费

2026-09-12：按 A01 / A12 / A14 / A30 接入实际源文件和外部使用库。现有静态生成器已经持有字段定义、顺序与类型形状，因此直接扩展同一模型生成 ITextSerializable / ITextObjectReader，未建立字段 DTO、运行时反射表、平行二进制容器或新的资产类型目录。TextSerializer 的 JSON 解析使用 BCL JsonDocument，读取器在其有效期内构建最终字段；文本生成不依赖 Asset 特例。

- 文本支持普通标量、枚举、组合值、可空值、Memory、数组及受支持集合、字典、显式联合。字典沿用二进制键排序，联合沿用 BinaryUnionCase 标记；默认字段保留默认值，null、required、重复字段、未知字段和错误标签有公开行为验证。只读 record struct 的 init 属性沿用对象初始化能力，移除此前不必要的整体 readonly 限制，仍检查各字段是否可填充。
- AssetText 定义可读源公共描述，身份只位于 Fields 一处；未知业务类型可描述 ID / 类型 / 名称 / schema。OpenSourceAsync 直接填入调用方 PageArena，外部块用相对路径引用并通过既有 IRangeSource → PageBuffer 读取。失败会释放已填充页，未加载的二进制块在另存源时也必须提供引用。SaveSourceAsync 与二进制保存共用原临时文件及最终替换流程，错误身份不能覆盖现有源。
- 新增 samples/SomeEngine.Curves 与 samples/SomeEngine.Curves.Assets。普通 Point / Curve / CurveSamples 所在库不引用 Assets；CurveAsset 和 ConstantCurveAsset 均可创建 Curve，CurveAsset 还能创建采样结果。使用对象直接保留已有页；源或竞技场关闭后继续计算；资产编辑不会隐式更新 Curve，显式 Update 保留对象身份。
- PageList 增加范围保留，PageCollections 为 IList 声明提供通用批量写与范围保留，生成资产也提供 ReadPoints 等类型化页读取。外部测试覆盖元素赋值、Duplicate、CreateCopy、跨 await 范围、批量写、释放和零每元素分配。没有加入逐类型 Edit 或使用对象字段镜像。

本轮 Serialization 253 项、Assets.Consumer 38 项、Assets 151 项已通过。完整解决方案已构建成功，重新编译仍报告既有 nullable / CA2255 / xUnit 警告。Assets Native AOT 重新发布并执行成功，输出同时包含 curve-source=ok 和 text-shapes=ok，覆盖跨程序集 Point、源文件、Curve 生命周期及字典 / 枚举 / 联合的静态文本编码。烟测临时目录在 finally 中删除，正式发布文件留在标准 bin 目录。

最终再次执行 dotnet test SomeEngine.slnx -c Release --no-restore --nologo -v:q，实际完成 20 个项目、2525 项通过、3 项跳过、0 项失败。包含 Direct3D12 405 项（另跳过 1，耗时 3 分 28 秒）、Vulkan 43 项、Render 84 项、Cluster 121 项、Runtime 21 项。此处是现有自动化覆盖，不能据此声称独立定义、全部源构建或完整图形应用场景已经验收。

完整目标继续：该前端尚未接入全部内置资产、工程发现、目录重建、构建工具与默认内容；独立 .shader 定义、源与伴随成组操作、持久输出身份与可重建记录分离、多输出切换、明确使用边界分析器、最终性能参数和 A31–A33 全覆盖审查仍未完成。文本与引用负载的保存不是多文件事务；不得将以上局部验证转写为整个 A12 或 A30 已完成。

## 独立 Shader 定义、构建与消费接入

2026-09-12：继续 A12 / A22 / A23 / A29。ShaderAsset 原先只保存编译结果，项目级 SlangSourceImporter 从单份 .slang 及伴随文件推导输出；这与独立定义、多源码选择及“人工设置不混入构建记录”冲突。本轮在同一 ShaderAsset 声明加入 SourceFiles、SelectedEntries、Specializations、CookProfile、Targets，字段版本变为 9。新增通用 TextIgnore，明确将字节码、反射和构建追踪排除于人工源，二进制仍保留这些派生字段；没有新增 Shader 字段镜像。

SlangShaderImporter 的 CompileDefinition / BuildAsync 使用原有 Slang session、组合、链接、反射、材质元数据和字节码捕获流程。SourceFiles 明确选择多份源码，SelectedEntries 选择原生入口，特化直接调用 Slang 原生表达式 API，目标沿用原有配置。相同名称的无关 .slang 不参与构建。includeDirectories 支持项目外只读库；SlangDeps 不再把项目外依赖截成文件名。输入摘要包括实际依赖、定义内容、配置、include 路径顺序与 Slang 构建版本，GetDefinitionInputs 可以复核此前依赖；增量跳过和统一可重建记录尚未接入。

IAssetSourceFormat 补充源格式处理能力，AssetProject.BuildAsync 在产物成功后登记到既有 AssetManifest。DefinitionPath 由目录条目持有，目录保存 / 重载后可按定义路径解析身份；重复定义和已被其他 ID 占用的输出路径在构建前拒绝。原项目级 SlangSourceImporter 已删除，AssetProject 测试、AssetCook、GenerateDefaultAssets 和 RenderScenarios 改为显式 .shader 构建。剩余静态 Import / ImportTransient 测试辅助入口及其 SourceMeta 依赖仍需在最终清理中审查，不能将项目级入口删除描述为全部旧构建路径已清除。

52 份现有默认 Shader 已转为独立 .shader，保留先前输出 GUID 和编译配置；对应 52 份重复 .slang.meta 已移除。另外两份仅用于 include、无输出记录的 fullscreen_triangle / feature_visible_clusters 伴随文件也已移除，源码仍保留。一次性身份转写代码执行后立即删除。GenerateDefaultAssets --rebuild 实际重建了 63 份选定默认产物并完成目录验证，加入完整编译键后又按新定义重新构建成功。AssetCook 对 hello_triangle.shader 实际构建与存储打开成功，GUID 仍为 40101228-9501-58ff-b81a-08767d408801，配置仍为 d3d12-sm6.2。

新增 ShaderDefinitionTests 覆盖多文件 / 入口、无同名绑定、原身份、目录重新加载、重复定义拒绝、取消、外部 include 变化、编译失败保留旧产物及特化生成 DXIL / SPIR-V；原配置切换、反射和身份测试迁到新入口。Assets 154 项通过，Serialization 254 项通过。全量命令 dotnet test SomeEngine.slnx -c Release --no-build --no-restore --nologo -v:q 完成 20 个项目，2528 项通过、1 项失败、3 项跳过；唯一失败是 Cluster 测试把新 .shader 路径作为 Slang 读取。该消费者改为根据目录的 DefinitionPath 加载实际定义，经同一编译器验证每个配置的管线入口，随后 Cluster 全套 121 项复跑通过。最终各项目覆盖合计为 2529 项通过、3 项跳过，以全量输出和这次 Cluster 复跑共同为证。

Assets Native AOT 重新发布并执行成功，输出新增 shader-file=ok：发布包内携带实际构建的 triangle.shader.asset，读取并创建 Shader 后关闭文件，已取得的两后端代码仍可使用。既有 curve-source、text-shapes、Mesh、Material、World 与 Texture 项同时通过。该烟测不执行 Shader GPU 帧；完整应用验收仍单独跟踪。

本轮还接收并记录用户同步的最终材质分发结论，已加入原则补充及 A26 / A29：Standard / Unlit 等具体材质属于项目模板，顶层项目 Shader 组合材质算法与管线入口，引擎保留通用设施与只读 Shader 库，不引入替换表或自动回退。当前具体材质仍在 SomeEngine.Resources / Assets 中，standard.slang 的 Cluster 耦合尚未拆清，模板项目、首次无缓存构建、更新不覆盖用户代码等验收均未完成。其余资产文本接入、发现与成组操作、构建记录及多输出切换、边界分析器、最终性能和 A31–A33 完整审查继续，目标保持进行中。

## Block 迁移与验证中间记录（2026-09-16）

本次恢复执行限定在 `worktrees/asset-rebuild`、`codex/asset-rebuild`。逐文件基线为 541 项未提交变动（97 删除、252 修改、192 未跟踪），保留原修改和 Slang 子模块。恢复后的 `dotnet build SomeEngine.slnx -c Release --no-restore --nologo -v quiet` 实际通过，0 错误、6 条已有 xUnit 分析器警告。这是迁移开始前基线，不是最终重构验收。

用户最终约束以同一 Asset 中的开放强类型 Block 为准：Asset 持身份、引用和生命周期；Block 自行选择编码与取得方式，小元数据可驻留，大数据通过既有 BinaryDocument、页与调度机制按需读取。多个 Importer 可对同一原生来源对象和同一目标 Asset 生产不同 Block，来源选择确定实际生产者；SG 只生成 Microsoft DI 注册和编辑器能力描述，不生成业务流程。先前直接把全部业务字段挂在 Asset、静态固定组装链以及临时材质回调的方向须按此纠正。原43项尚未据新架构完成逐项验收，不在此勾选。

已落地的中间实现包括 AssetBlock/typed acquisition/文档目录、Block 字段生成器、MeshBlock/ClusterMeshBlock 的真实数据迁移，以及 ClusterBuilder、glTF 区域映射和应用 Mesh 消费端显式取得这些 Block。Texture 也迁移到唯一持数据的 TextureBlock。新增 Context 暂存结果、并发去重及依赖图，DI 能力生成器扫描引用程序集声明；统一来源事务与设置/构建记录拆分正在实现。以上新代码仍在行为回归和修复过程中，不能据文件存在认定完成。

中间审查发现并正在处理：不同 Block 的局部 chunk key 必须隔离；自定义编码可直接读写 metadata；一个 Asset 的页字段和 Block 必须共用 PageAccess；关闭取消在途读取；读取与释放同时失败仍须结束等待者；组替换提交与退休数据释放错误须分开；未知 Block 重存保留原编码描述。事务暂存名称必须保留真实复合后缀，单文件替换/回滚与跨文件目录可见性分别验收。

已实际执行 `dotnet run --project tools/SomeEngine.Serialization.Benchmarks/SomeEngine.Serialization.Benchmarks.csproj -c Release --no-restore`，包含外层补充并由主任务接入的 PageWorkloads。4/16/64 KiB 三档页的混合生命周期 round p50 为 307.0/53.1/47.1 us，复制比为 87.25/341.10/1356.52；保留 2816 字节实际范围时活跃容量为 201728/705536/2720768 字节，实际数组空间为 2621440/3407872/5242880 字节。8 并发、128 次、每次 256 KiB 范围读取观测吞吐为 538.01/2261.60/5787.04 MiB/s，所有档位释放后为 0。OS 文件缓存不受控；轮次包含校验和簿记；异步分配为进程级，不能解释为纯 API 延迟或冷盘吞吐。A11 仍待真实 Asset 负载、最终参数选择和完整证据。

此前模板应用的两帧验证曾因 Cluster 无候选/无可见几何而失败；这里只记录事实，不把首帧呈现或进程存活算多帧通过。最新 Block schema 下的默认内容、Native AOT、完整测试与 D3D12/Vulkan 应用帧尚需重新执行。


## 实际 Texture Block 测量（2026-09-16，31 样本）

`dotnet run --project tools/SomeEngine.Assets.Benchmarks -c Release --no-restore` 已实际通过全部9组，每组完整预热后测31次。下表为 p50；OS文件缓存不受控，分配计数为本进程总量；无GPU，当前构建活动也会影响I/O尾部延迟，不能解释为设备冷盘指标。

| 最大页 KiB | 像素字节 | 打开 | 元数据分支复制 | 取得像素 | 首次写 | typed Block 查询 |
|---:|---:|---:|---:|---:|---:|---:|
| 4 | 4096 | 954.20us | 13.00us | 479.90us | 17.10us | 116.29ns |
| 4 | 2097152 | 845.40us | 12.40us | 30926.40us | 33.30us | 140.20ns |
| 4 | 16777216 | 642.60us | 5.00us | 236725.90us | 33.80us | 68.66ns |
| 16 | 4096 | 537.30us | 4.30us | 272.50us | 2.60us | 68.39ns |
| 16 | 2097152 | 917.10us | 7.10us | 28211.90us | 39.90us | 100.00ns |
| 16 | 16777216 | 641.20us | 4.80us | 115852.80us | 42.80us | 69.12ns |
| 64 | 4096 | 545.70us | 4.40us | 272.60us | 2.60us | 68.95ns |
| 64 | 2097152 | 597.00us | 4.40us | 9945.00us | 29.40us | 68.65ns |
| 64 | 16777216 | 654.20us | 4.90us | 84555.50us | 101.90us | 68.77ns |

全部大小/页档打开只解码96字节元数据、活跃页容量768字节；未加载像素重存的额外解码量为0。每10000次Block查询分配为0；16MiB像素资产首次局部写实际复制分别4096/16384/65536字节，未复制整个像素块。各组均验证原分支不变，关闭Asset和文件后Texture仍持编辑后的像素，释放并Trim后为0。元数据分支复制分配约1296–1336字节，不随4KiB到16MiB像素数据量线性增长。此测量补足实际Texture路径，最终参数及其他真实资产/完整回归仍须结合后续证据，未据此提前勾选43项。

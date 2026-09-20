# 资产重构：当前家族与原始范围清单

2026-09-16。以下当前矩阵替代旧文件名枚举中的迁移状态；原始清单随后保留，明确作为 2026-09-12 的范围基线，不能据其旧路径删除业务代码或恢复旧架构。最终完整回归、三套原生 AOT、独立项目与七组多帧均已通过，43 项任务完成；详见 [任务](Asset-Rebuild-Tasks.md) 与 [验证记录](Asset-Verification-2026-09-16.md)。

| 家族或连接 | 当前主要位置与数据 | 已执行的公开证据 |
| --- | --- | --- |
| 共同资产 | Assets 的 Asset、Asset.Blocks、AssetBlock、AssetText；根只保存身份和 Block 描述 | 分支、打开、失败、未知 Block、文本与二进制、AOT |
| Texture | Schema 的 TextureAsset / TextureBlock，原 tile 编码与页流送；Resources 的使用对象 | 各形状、mip、外部负载、冷保存、保留读者、更新和实际上传 |
| Mesh | MeshAsset 内 MeshBlock、MeshMaterialsBlock、ClusterMeshBlock | 原生 glTF 两个几何生产者、同一语义身份、多材质引用、分页几何和实际渲染 |
| Material | MaterialBlock；项目模板 Game.Assets 中的 Standard / Unlit 声明及参数生成 | 继承、零/空/重置、父链、纹理依赖、更新、多材质场景 |
| Shader | ShaderAsset 内 ShaderSourceBlock / ShaderBlock；项目顶层 Slang 与显式引擎库 | 原生编译、实际输入与输出核验、目标、特化、取消失败、AssetCook、AOT 消费代码 |
| 管线资源组 | Cluster、虚拟阴影等配置使用各自强类型 Block | 必需入口/阶段检查、旧资源释放、完整 Cluster 121 项和六种场景 |
| World 与启动配置 | 原有 World / ECS 编码及配置 Block；Game world 到 Rendering world 保持 | 资产引用、原生 ECS、1024 实例、独立应用自有目录 |
| 外部扩展 | samples/SomeEngine.Curves.Assets 与消费者程序集 | 同一 Block 创建多个使用形态、不同 Block 创建同种对象、外部类型编码、实际 AOT |
| 导入 | Importing 下 Context / batch；模板 Game.Importing 使用原生 glTF 对象 | 生产者选择冲突、依赖去重、设置作用域、失败取消、唯一共享贴图输出 |
| 工程存储 | AssetProject、SourceMeta、单 catalog、原 Loader / loose / pack | 发现重建、移动复制删除、悬空引用、成组回退、53 个衍生记录删除后的身份保持 |
| 通用编码 | 原 Serialization、Generators、页与调度、范围来源、包认证和差分 | Serialization 255、独立消费者、协议/边界/预算及原生 AOT |
| 工具和模板 | 12 个 tools 项目，DefaultProject 的 Game.Build / Game.App | 0 警告构建、两种位置的零产物安装、用户源码保留和非空多帧 |

旧嵌套资产、Asset 级业务字段、单 Slang 隐式导入、默认根目录生成流程、三索引回退和通用来源设置回退均已从当前主流程移除。原编码算法、几何/材质/Shader 业务数据及独立算法不是删除目标。

## 2026-09-12 原始范围基线

### 当时的引用枚举

2026-09-12；依据当前工作树的项目文件、源码引用和构建入口。主工作区开始时已有修改见 Asset-Rebuild-Execution.md。以下是原始引用枚举，不作为删除清单。保持、局部调整和替换范围以修订后的原则、任务及序列化审查为准。


## src/SomeEngine.Assets

保持有效身份值、查询、来源和保存能力；A01–A04、A14–A19 替换 T 绑定 Asset、嵌套声明及冲突生命周期；A23–A27 调整业务使用类型与资产连接。

- src/SomeEngine.Assets/AssemblyInfo.cs
- src/SomeEngine.Assets/Asset.cs
- src/SomeEngine.Assets/AssetAttribute.cs
- src/SomeEngine.Assets/AssetId.cs
- src/SomeEngine.Assets/AssetIoHelpers.cs
- src/SomeEngine.Assets/AssetLoader.cs
- src/SomeEngine.Assets/AssetManifest.cs
- src/SomeEngine.Assets/AssetPipelineContracts.cs
- src/SomeEngine.Assets/AssetProject.cs
- src/SomeEngine.Assets/AssetRecord.cs
- src/SomeEngine.Assets/AssetStorage.cs
- src/SomeEngine.Assets/Data/ClusterBVHNode.cs
- src/SomeEngine.Assets/Data/MeshPageLayout.cs
- src/SomeEngine.Assets/Data/MeshPayloadLayout.cs
- src/SomeEngine.Assets/Data/PackedCluster.cs
- src/SomeEngine.Assets/Data/VertexAttribute.cs
- src/SomeEngine.Assets/Data/VertexEncodingCompatibility.cs
- src/SomeEngine.Assets/GlobalUsings.cs
- src/SomeEngine.Assets/ImportHash.cs
- src/SomeEngine.Assets/MetaFiles.cs
- src/SomeEngine.Assets/Pipeline/MeshPayloadSource.cs
- src/SomeEngine.Assets/Schema/ApplicationSettings.cs
- src/SomeEngine.Assets/Schema/AssetContracts.cs
- src/SomeEngine.Assets/Schema/ClusterShaders.cs
- src/SomeEngine.Assets/Schema/ClusterShaders.Loading.cs
- src/SomeEngine.Assets/Schema/Material.Asset.cs
- src/SomeEngine.Assets/Schema/Material.cs
- src/SomeEngine.Assets/Schema/MaterialData.cs
- src/SomeEngine.Assets/Schema/Mesh.cs
- src/SomeEngine.Assets/Schema/Mesh.Fields.cs
- src/SomeEngine.Assets/Schema/Mesh.Loading.cs
- src/SomeEngine.Assets/Schema/Mesh.Pages.cs
- src/SomeEngine.Assets/Schema/Mesh.Streaming.cs
- src/SomeEngine.Assets/Schema/Shader.cs
- src/SomeEngine.Assets/Schema/Shader.Fields.cs
- src/SomeEngine.Assets/Schema/Shader.Loading.cs
- src/SomeEngine.Assets/Schema/Shader.Programs.cs
- src/SomeEngine.Assets/Schema/ShaderRef.cs
- src/SomeEngine.Assets/Schema/StandardMaterial.cs
- src/SomeEngine.Assets/Schema/Texture.Chunks.cs
- src/SomeEngine.Assets/Schema/Texture.Create.cs
- src/SomeEngine.Assets/Schema/Texture.Devices.cs
- src/SomeEngine.Assets/Schema/Texture.Fields.cs
- src/SomeEngine.Assets/Schema/Texture.Loading.cs
- src/SomeEngine.Assets/Schema/UnlitMaterial.cs
- src/SomeEngine.Assets/Schema/VirtualShadowMapShaders.cs
- src/SomeEngine.Assets/Schema/VirtualShadowMapShaders.Loading.cs
- src/SomeEngine.Assets/SomeEngine.Assets.csproj
- src/SomeEngine.Assets/SourceGuid.cs
- src/SomeEngine.Assets/WorldAsset.cs

## src/SomeEngine.Assets.Importers

A20–A25：调整源身份、伴随描述、人工设置、构建记录与输出登记；现有模型算法、Slang 编译能力和其他无关业务保持。

- src/SomeEngine.Assets.Importers/AssemblyInfo.cs
- src/SomeEngine.Assets.Importers/GlobalUsings.cs
- src/SomeEngine.Assets.Importers/ImportedAssetIds.cs
- src/SomeEngine.Assets.Importers/Importers/ClusterBuilder.cs
- src/SomeEngine.Assets.Importers/Importers/ClusterBuilder.Raw.cs
- src/SomeEngine.Assets.Importers/Importers/ClusterVertexEncoder.cs
- src/SomeEngine.Assets.Importers/Importers/DisplacementBoundsTexture.cs
- src/SomeEngine.Assets.Importers/Importers/GltfDeps.cs
- src/SomeEngine.Assets.Importers/Importers/GltfImporterSettings.cs
- src/SomeEngine.Assets.Importers/Importers/GltfSourceImporter.cs
- src/SomeEngine.Assets.Importers/Importers/MeshMaterialSlot.cs
- src/SomeEngine.Assets.Importers/Importers/PrimitiveMeshGenerator.cs
- src/SomeEngine.Assets.Importers/Importers/RawAttribute.cs
- src/SomeEngine.Assets.Importers/Importers/SlangBindingMap.cs
- src/SomeEngine.Assets.Importers/Importers/SlangDeps.cs
- src/SomeEngine.Assets.Importers/Importers/SlangEntryMeta.cs
- src/SomeEngine.Assets.Importers/Importers/SlangImportLifetime.cs
- src/SomeEngine.Assets.Importers/Importers/SlangMaterialMeta.cs
- src/SomeEngine.Assets.Importers/Importers/SlangShaderCookProfile.cs
- src/SomeEngine.Assets.Importers/Importers/SlangShaderImporter.cs
- src/SomeEngine.Assets.Importers/Importers/SlangSourceImporter.cs
- src/SomeEngine.Assets.Importers/Importers/StandardClusterVertexEncoder.cs
- src/SomeEngine.Assets.Importers/SomeEngine.Assets.Importers.csproj

## src/SomeEngine.Serialization

保持容器、编码、来源、调度、预算、包、视图与原生块的有效能力；B00–B08 局部修正 D01–D10，A05–A10 将编辑页接入既有读取和预算。不得据此列表整库删除。

- src/SomeEngine.Serialization/BinaryContractCatalog.cs
- src/SomeEngine.Serialization/BinarySerializer.cs
- src/SomeEngine.Serialization/BinaryContractViews.cs
- src/SomeEngine.Serialization/BinaryDataReader.cs
- src/SomeEngine.Serialization/BinaryDataWriter.cs
- src/SomeEngine.Serialization/BinaryPrimitiveEncoding.cs
- src/SomeEngine.Serialization/Containers/BinaryChunkRef.cs
- src/SomeEngine.Serialization/Containers/BinaryDocument.cs
- src/SomeEngine.Serialization/Containers/BinaryDocumentFormat.cs
- src/SomeEngine.Serialization/Containers/BinaryDocumentView.cs
- src/SomeEngine.Serialization/Containers/BinaryDocumentWriter.cs
- src/SomeEngine.Serialization/Containers/BinaryDocumentWriting.cs
- src/SomeEngine.Serialization/Containers/BufferRead.cs
- src/SomeEngine.Serialization/Containers/SharedBytes.cs
- src/SomeEngine.Serialization/Containers/SharedData.cs
- src/SomeEngine.Serialization/Contracts.cs
- src/SomeEngine.Serialization/Digest256.cs
- src/SomeEngine.Serialization/IO/BoundedCountingStreams.cs
- src/SomeEngine.Serialization/IO/HashingReadStream.cs
- src/SomeEngine.Serialization/IO/HashingWriteStream.cs
- src/SomeEngine.Serialization/IO/HttpRangeSource.cs
- src/SomeEngine.Serialization/IO/MemoryMappedRangeSource.cs
- src/SomeEngine.Serialization/IO/RangeReadStream.cs
- src/SomeEngine.Serialization/IO/RangeSources.cs
- src/SomeEngine.Serialization/NativeBlocks.cs
- src/SomeEngine.Serialization/Packs/AssetPack.cs
- src/SomeEngine.Serialization/SomeEngine.Serialization.csproj
- src/SomeEngine.Serialization/Streaming/ChunkRequestScheduler.cs
- src/SomeEngine.Serialization/Streaming/ChunkStreamingMetrics.cs
- src/SomeEngine.Serialization/Streaming/DocumentChunkBatchLoader.cs
- src/SomeEngine.Serialization/Streaming/ResidencyBudget.cs

## src/SomeEngine.Generators

B00、B06、B07 与 A14/A26/A30：保持静态编码、注册、联合类型和原生布局；解除资产字段注入，修正字段排列与视图定位，替换资产字段镜像。

- src/SomeEngine.Generators/AnalyzerReleases.Unshipped.md
- src/SomeEngine.Generators/AssetGenerator.cs
- src/SomeEngine.Generators/BinaryContractGenerator.cs
- src/SomeEngine.Generators/MaterialGen.cs
- src/SomeEngine.Generators/SomeEngine.Generators.csproj

## tests/SomeEngine.Assets.Tests

A31：保留有效业务回归，更新明确变动的 Asset API，增加新语义覆盖。

- tests/SomeEngine.Assets.Tests/AssemblyInfo.cs
- tests/SomeEngine.Assets.Tests/Assets/AssetArchitectureAcceptanceTests.cs
- tests/SomeEngine.Assets.Tests/Assets/AssetManifestTests.cs
- tests/SomeEngine.Assets.Tests/Assets/AssetOpeningTests.cs
- tests/SomeEngine.Assets.Tests/Assets/AssetPackStorageTests.cs
- tests/SomeEngine.Assets.Tests/Assets/AssetProjectTests.cs
- tests/SomeEngine.Assets.Tests/Assets/AssetProjectValidatorTests.cs
- tests/SomeEngine.Assets.Tests/Assets/AssetStorageTests.cs
- tests/SomeEngine.Assets.Tests/Assets/AssetTypeExtensibilityTests.cs
- tests/SomeEngine.Assets.Tests/Assets/AssetWriterAcceptanceTests.cs
- tests/SomeEngine.Assets.Tests/Assets/ClusterBuilderTestMaterials.cs
- tests/SomeEngine.Assets.Tests/Assets/ClusterBuilderTests.cs
- tests/SomeEngine.Assets.Tests/Assets/ClusterBVHNodeTests.cs
- tests/SomeEngine.Assets.Tests/Assets/ClusterLodLevelTests.cs
- tests/SomeEngine.Assets.Tests/Assets/CookedClusterShaderContractTests.cs
- tests/SomeEngine.Assets.Tests/Assets/DisplacementBoundsTextureTests.cs
- tests/SomeEngine.Assets.Tests/Assets/EditableAssetTests.cs
- tests/SomeEngine.Assets.Tests/Assets/GltfSourceImporterTests.cs
- tests/SomeEngine.Assets.Tests/Assets/MaterialFileTests.cs
- tests/SomeEngine.Assets.Tests/Assets/MaterialReloadTests.cs
- tests/SomeEngine.Assets.Tests/Assets/PackedClusterTests.cs
- tests/SomeEngine.Assets.Tests/Assets/ReadingSample.cs
- tests/SomeEngine.Assets.Tests/Assets/ShaderFileTests.cs
- tests/SomeEngine.Assets.Tests/Assets/ShaderSourcePolicyTests.cs
- tests/SomeEngine.Assets.Tests/Assets/SlangComLifetimeTests.cs
- tests/SomeEngine.Assets.Tests/Assets/SlangEntryPointResourceUsageTests.cs
- tests/SomeEngine.Assets.Tests/Assets/SlangInstancePropertyTests.cs
- tests/SomeEngine.Assets.Tests/Assets/SlangIntegrationTests.cs
- tests/SomeEngine.Assets.Tests/Assets/SlangShaderCookProfileTests.cs
- tests/SomeEngine.Assets.Tests/Assets/SlangShaderTextureShapeTests.cs
- tests/SomeEngine.Assets.Tests/Assets/SourceMetaManagerTests.cs
- tests/SomeEngine.Assets.Tests/Assets/TextureMipTileStreamingTests.cs
- tests/SomeEngine.Assets.Tests/Assets/WaveQueueCompilationTest.cs
- tests/SomeEngine.Assets.Tests/IcoSphereTest.cs
- tests/SomeEngine.Assets.Tests/SomeEngine.Assets.Tests.csproj
- tests/SomeEngine.Assets.Tests/TestProjectPaths.cs

## tests/SomeEngine.Assets.Consumer.Tests

A01、A30、A31：外部消费及扩展验证

- tests/SomeEngine.Assets.Consumer.Tests/DiskAssetBehaviorTests.cs
- tests/SomeEngine.Assets.Consumer.Tests/SomeEngine.Assets.Consumer.Tests.csproj

## tests/SomeEngine.Serialization.Tests

B01–B08、A05–A14、A31：保持现有静态编码、公开文件协议、范围、签名、布局和 AOT 验证；按已确认缺陷增加回归，仅调整明确变化的公开语义。

- tests/SomeEngine.Serialization.Tests/AssetPackTests.cs
- tests/SomeEngine.Serialization.Tests/BinaryContractRuntimeTests.cs
- tests/SomeEngine.Serialization.Tests/BinaryContractShapeGeneratorTests.cs
- tests/SomeEngine.Serialization.Tests/BinaryDocumentOwnershipTests.cs
- tests/SomeEngine.Serialization.Tests/BinaryDocumentTests.cs
- tests/SomeEngine.Serialization.Tests/BinaryNativeLayoutGeneratorTests.cs
- tests/SomeEngine.Serialization.Tests/ChunkRequestSchedulerTests.cs
- tests/SomeEngine.Serialization.Tests/GeneratedContractTests.cs
- tests/SomeEngine.Serialization.Tests/HashingStreamTests.cs
- tests/SomeEngine.Serialization.Tests/HttpRangeSourceTests.cs
- tests/SomeEngine.Serialization.Tests/MappedTestDocument.cs
- tests/SomeEngine.Serialization.Tests/OwnedTestEncoding.cs
- tests/SomeEngine.Serialization.Tests/RangeReadStreamTests.cs
- tests/SomeEngine.Serialization.Tests/RangeSourceAndNativeBlockTests.cs
- tests/SomeEngine.Serialization.Tests/RangeSourceConsistencyTests.cs
- tests/SomeEngine.Serialization.Tests/RegressionAcceptanceTests.cs
- tests/SomeEngine.Serialization.Tests/SharedDataTests.cs
- tests/SomeEngine.Serialization.Tests/SomeEngine.Serialization.Tests.csproj
- tests/SomeEngine.Serialization.Tests/TestSupport.cs

## 其他源码消费者

ECS 持久化接入属于 A27；Render、Cluster、Features、启动应用属于 A28；测试属于 A31；工具与样例属于 A29。设备后端、ECS World、调度算法和 RenderGraph 本身不在删除范围。

- src/SomeEngine.Render.Cluster.Pipeline/ClusterGeometryContracts.cs
- src/SomeEngine.Render.Cluster.Pipeline/ClusterMaterialAssignments.cs
- src/SomeEngine.Render.Cluster.Pipeline/ClusterMaterialStages.cs
- src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelines.cs
- src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSet.cs
- src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.cs
- src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Materials.cs
- src/SomeEngine.Render.Cluster.Pipeline/SomeEngine.Render.Cluster.Pipeline.csproj
- src/SomeEngine.Render.Cluster/ClusterBvh.cs
- src/SomeEngine.Render.Cluster/ClusterMeshes.cs
- src/SomeEngine.Render.Cluster/ClusterMeshPreparation.cs
- src/SomeEngine.Render.Cluster/ClusterMeshRegistration.cs
- src/SomeEngine.Render.Cluster/ClusterRenderResources.cs
- src/SomeEngine.Render.Cluster/ClusterResidency.cs
- src/SomeEngine.Render.Cluster/MeshPages.cs
- src/SomeEngine.Render.Cluster/PageStream.cs
- src/SomeEngine.Render.Cluster/SomeEngine.Render.Cluster.csproj
- src/SomeEngine.Render.Features/Debugging/DebugFeature.cs
- src/SomeEngine.Render.Features/GeometryExport/GeometryExportFeatures.cs
- src/SomeEngine.Render.Features/Lighting/LightingFeature.cs
- src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs
- src/SomeEngine.Render.Features/SomeEngine.Render.Features.csproj
- src/SomeEngine.Render.Features/Temporal/TemporalFeature.cs
- src/SomeEngine.Render.Features/Translucency/TranslucencyFeature.cs
- src/SomeEngine.Render.MaterialBindless/MaterialBindless.cs
- src/SomeEngine.Render.MaterialBindless/SomeEngine.Render.MaterialBindless.csproj
- src/SomeEngine.Render/Assets/LinkedComputePipeline.cs
- src/SomeEngine.Render/Assets/LinkedRasterPipeline.cs
- src/SomeEngine.Render/Components/LightComponents.cs
- src/SomeEngine.Render/Components/MaterialBindingComponents.cs
- src/SomeEngine.Render/Components/MeshInstanceComponent.cs
- src/SomeEngine.Render/Components/RenderWorldComponents.cs
- src/SomeEngine.Render/Components/SceneComponents.cs
- src/SomeEngine.Render/Instances/RenderMeshInstanceSet.cs
- src/SomeEngine.Render/Materials/MaterialPass.cs
- src/SomeEngine.Render/Materials/ScalarLayout.cs
- src/SomeEngine.Render/RenderDevice.Textures.cs
- src/SomeEngine.Render/Shadows/VirtualShadowMap.cs
- src/SomeEngine.Render/SomeEngine.Render.csproj
- src/SomeEngine.Render/Systems/MeshRenderExtractor.cs
- src/SomeEngine.Runtime/RuntimeApplication.cs
- src/SomeEngine.Runtime/RuntimePresentationPipeline.cs
- src/SomeEngine.Runtime/RuntimeScene.cs
- src/SomeEngine.Runtime/RuntimeUiRenderer.cs
- src/SomeEngine.Runtime/SomeEngine.Runtime.csproj
- tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj
- tests/SomeEngine.Render.Cluster.Tests/ClusterAssetLoaderTests.cs
- tests/SomeEngine.Render.Cluster.Tests/ClusterMaterialStageTests.cs
- tests/SomeEngine.Render.Cluster.Tests/ClusterPublicationTests.cs
- tests/SomeEngine.Render.Cluster.Tests/ClusterRegistrationTests.cs
- tests/SomeEngine.Render.Cluster.Tests/ClusterResidencyPublicationTests.cs
- tests/SomeEngine.Render.Cluster.Tests/ClusterShaderAbiTests.cs
- tests/SomeEngine.Render.Cluster.Tests/ClusterTestAssets.cs
- tests/SomeEngine.Render.Cluster.Tests/ClusterTestFiles.cs
- tests/SomeEngine.Render.Cluster.Tests/ControlledRangeSource.cs
- tests/SomeEngine.Render.Cluster.Tests/CookedShaderTestProgram.cs
- tests/SomeEngine.Render.Cluster.Tests/CrossCuttingFeatureArchitectureTests.cs
- tests/SomeEngine.Render.Cluster.Tests/MeshStreamingTests.cs
- tests/SomeEngine.Render.Cluster.Tests/PageStreamTests.cs
- tests/SomeEngine.Render.Cluster.Tests/ResidencyBudgetTests.cs
- tests/SomeEngine.Render.Tests/AssetLoaderTests.cs
- tests/SomeEngine.Render.Tests/CookedShaderTestProgram.cs
- tests/SomeEngine.Render.Tests/InstancedMeshExtractionTests.cs
- tests/SomeEngine.Render.Tests/RenderDeviceTextureTests.cs
- tests/SomeEngine.Render.Tests/RenderMeshInstanceCollectionTests.cs
- tests/SomeEngine.Render.Tests/RenderMeshInstanceSetTests.cs
- tests/SomeEngine.Render.Tests/RenderWorldExtractorTests.cs
- tests/SomeEngine.Render.Tests/ShaderMaterialPropertyTests.cs
- tests/SomeEngine.Render.Tests/ShaderScalarLayoutTests.cs
- tests/SomeEngine.Render.Tests/TestAssets.cs
- tests/SomeEngine.Runtime.Tests/RuntimeShaderBoundaryTests.cs
- tests/SomeEngine.Runtime.Tests/WorldAssetTests.cs
- tools/GenerateDefaultAssets/Program.cs
- tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj
- tools/SomeEngine.AssetCook/Program.cs
- tools/SomeEngine.AssetCook/SomeEngine.AssetCook.csproj
- tools/SomeEngine.Assets.AotSmoke/Program.cs
- tools/SomeEngine.Assets.AotSmoke/SomeEngine.Assets.AotSmoke.csproj
- tools/SomeEngine.RenderScenarios/Scenarios.cs
- tools/SomeEngine.RenderScenarios/SomeEngine.RenderScenarios.csproj
- tools/SomeEngine.Serialization.AotSmoke/Program.cs

## 构建、默认内容与文档

A29、A32：SomeEngine.slnx、Directory.Build.props、Directory.Build.targets、justfile、tools 下各 csproj/ps1；assets 中 Slang 和定义是输入，旧 .asset 由新工具重建；Library 为可重建内容，不作为源。wiki/architecture/Assets、wiki/architecture/Asset-Files.md 和 docs/AssetSystem.md 按实际完成状态更新。

- docs/AssetSystem.md
- docs/BinarySerialization.md
- docs/dotnet10_modern_graphics_design_zh_v1.1.md
- docs/EnvironmentSetup.md
- docs/rhi-render-graph-concept-audit.md
- tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj
- tools/SomeEngine.AssetCook/SomeEngine.AssetCook.csproj
- tools/SomeEngine.Assets.AotSmoke/SomeEngine.Assets.AotSmoke.csproj
- tools/SomeEngine.RenderScenarios/SomeEngine.RenderScenarios.csproj
- wiki/architecture/Asset-Files.md
- wiki/architecture/Assets/Asset-Application-and-Rendering.md
- wiki/architecture/Assets/Asset-Code-Generation.md
- wiki/architecture/Assets/Asset-File-IO.md
- wiki/architecture/Assets/Asset-Identity-and-Dependencies.md
- wiki/architecture/Assets/Asset-Import-and-Storage.md
- wiki/architecture/Assets/Asset-Loading-and-Lifetime.md
- wiki/architecture/Assets/Asset-Materials.md
- wiki/architecture/Assets/Asset-Meshes.md
- wiki/architecture/Assets/Asset-Objects.md
- wiki/architecture/Assets/Asset-Responsibilities.md
- wiki/architecture/Assets/Asset-Shaders.md
- wiki/architecture/Assets/Asset-Sharing-and-Writes.md
- wiki/architecture/Assets/Asset-Textures.md
- wiki/architecture/Assets/Asset-Validation.md
- wiki/architecture/Assets/Asset-Verification-2026-09-10.md
- wiki/architecture/Assets/Asset-Worlds.md
- wiki/architecture/Cluster-Deform-Cache-Allocator-Benchmark-2026-09-01.md
- wiki/architecture/Render-Boundaries.md
- wiki/architecture/Render-Command-Optimization-2026-09-06.json
- wiki/architecture/Render-Enhancement-Measurements-2026-09-05.json
- wiki/architecture/Render-Enhancement-Optimization-2026-09-05.md
- wiki/architecture/Render-Enhancement-Performance-Review-2026-09-05.md
- wiki/architecture/Render-Enhancement-UE5-Source-Audit-2026-09-05.md
- wiki/architecture/Render-Execution-Cost-2026-09-06.json
- wiki/architecture/Render-Graph.md
- wiki/architecture/RenderGraph-Retained-Delivery-2026-09-06.json
- wiki/architecture/RenderGraph-Retained-Delivery-2026-09-06.md
- wiki/architecture/RHI/Core-Contract.md
- wiki/architecture/RHI/D3D12-Backend.md
- wiki/architecture/RHI/Descriptors-and-Bindings.md
- wiki/architecture/RHI/Implementation.md
- wiki/architecture/RHI/Index.md
- wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md
- wiki/architecture/RHI/Pipeline-Cache.md
- wiki/architecture/RHI/Refactor-Checkpoint-2026-08-09.md
- wiki/architecture/RHI/Validation-and-Evidence.md
- wiki/architecture/RHI/Vulkan-Backend.md
- wiki/Home.md

## 修订计划的完整资产接入核对

已核对 AssetAttribute 声明、手写 Asset 基类继承、材质派生与生成入口。以下全部未完成资产迁移；每项均需按实际职责覆盖声明、引用/目录、取得/编辑、保存/构建、创建/更新 T 及消费者。

| 家族 / 现有类型 | 当前入口 | 后续任务 |
| --- | --- | --- |
| Shader | Schema/Shader.Fields.cs、Shader.Loading.cs、Slang 导入与构建 | A22–A23、A28–A29 |
| ClusterShaders、VirtualShadowMapShaders | 两个 Schema/*Shaders.Loading.cs 与消费资源组 | A23、A28–A29 |
| Texture | Schema/Texture.Fields.cs、Chunks、Loading、Devices 与图像导入 | A24、A28–A29 |
| Mesh | Schema/Mesh.Fields.cs、Loading、Streaming、Pipeline 与模型导入 | A25、A28–A29 |
| Material、StandardMaterial、UnlitMaterial | Schema/Material.Asset.cs、MaterialGen 与两个派生声明 | A26、A28–A29 |
| WorldAsset、ApplicationSettings | WorldAsset.cs、Schema/ApplicationSettings.cs、ECS 与应用启动连接 | A27–A29 |
| ReadingSample | Assets.Tests/Assets/ReadingSample.cs | A01、A30–A31 |
| DiskProbe、OtherProbe、CountedAssetValue | Assets.Consumer.Tests/DiskAssetBehaviorTests.cs | A01、A30–A31 |
| SmokePalette 及 World/组件消费 | tools/SomeEngine.Assets.AotSmoke | A29–A31 |
| 默认、导入输出与渲染场景 | GenerateDefaultAssets、AssetCook、RenderScenarios、assets 与 samples | A20–A21、A28–A29、A33 |

保持项：BinaryDataReader/Writer、静态编码和注册、原有容器/块/压缩/校验、范围来源、四类预算与调度、包/差分/签名、可选编码视图、原生布局证明、ECS/Slang/图形业务。各项已知缺陷只按 B00–B08 和 A06/A08 修正，不能由文件枚举推导整库删除。
# 资产验证记录：2026-09-10

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

本页保存 2026-09-10 已确认范围收尾时的实际结果。它是有日期和范围的记录，不是整个资产迁移已经完成的声明，也不表示后续代码变化自动通过这些验证。验证原则见 [[Asset-Validation]]，未决设计见 [[Asset-Open-Questions]]。

## 本次完成的独立工作

旧读取、保存、登记与测试包装调用迁移为具体 `.Asset` 入口和公开来源访问，旧的 `AssetTestFiles` 包装移除。资产依赖从当前内容描述，保存条目供工程明确登记。

World 使用 ECS 序列化保存实际组件、缓冲区和实体身份；应用在同一个 Game world 上接入实际资源。新增 World 往返测试覆盖变换、Mesh / Material 引用、运动、相机、三种灯光、分支隔离和取消加载，结构说明见 [[Asset-Worlds]]。

范围读取失败后重试不再使用未完成的缓冲内容。新增读取测试覆盖跨范围读取、定位、来源借用、失败重试以及取消与关闭行为，接口说明见 [[Asset-File-IO]]。

## 实际测试结果

| 范围 | 结果 | 范围限制 |
| --- | --- | --- |
| Serialization 测试项目 | 149 项通过 | 该测试项目 |
| ECS.Serialization 测试项目 | 168 项通过 | 该测试项目 |
| Assets.Consumer 独立消费程序集 | 22 项通过 | 包含外部声明与生成入口 |
| Runtime 测试项目 | 21 项通过 | 包含新增的两个 World 行为测试 |
| Assets.Tests 公共行为子集 | 39 项通过 | 临时独立程序集，无引擎内部访问权限 |
| Render.Tests 子集 | 69 项通过 | 排除以下两个关联文件 |
| Render.Cluster.Tests 子集 | 116 项通过 | 排除以下一个关联文件 |

上述明确范围合计 **584 项通过**。Assets、Render 与 Render.Cluster 的子集结果不能写成对应整个测试项目通过。

## 子集的边界

Assets 公共行为子集包含 `AssetOpeningTests`、`AssetTypeExtensibilityTests`、`AssetWriterAcceptanceTests`、`EditableAssetTests`、`MaterialFileTests`、`AssetPackStorageTests`、`ShaderFileTests`、`AssetManifestTests` 和 `AssetProjectValidatorTests`。它覆盖打开、取消、保存、索引、包存储、材质、Shader 生命周期及扩展类型。

Render.Tests 暂未包含 `RenderDeviceTextureTests.cs`、`ShaderScalarLayoutTests.cs`。Render.Cluster.Tests 暂未包含 `ClusterAssetLoaderTests.cs`。这些文件与未确认的嵌套元数据类型相关。

排除只用于临时验证，没有写入测试项目配置。临时验证项目、筛选文件和测试生成目录已清理；工作区 `git diff --check` 通过。

## Native AOT

`SomeEngine.Assets.AotSmoke` 已完成 Release / win-x64 Native AOT 发布及产物运行。检查覆盖独立打开、来源关闭后的流式读取、对象更新、外部声明生成、Mesh 字节分支、材质继承与 World 保存加载。

发布使用以下命令，之后直接运行发布目录中的可执行文件并得到 `assets-nativeaot-smoke:ok`：

```powershell
dotnet publish tools/SomeEngine.Assets.AotSmoke/SomeEngine.Assets.AotSmoke.csproj -c Release -r win-x64 --no-restore -p:WarningsNotAsErrors=NU1902 -v quiet
```

该次发布遇到外部 SlangShaderSharp 依赖的 `Microsoft.Build.Tasks.Git 8.0.0` 审计告警 NU1902。命令参数只保留告警并允许继续编译，没有修改依赖版本或项目告警配置。原生产物实际运行成功，不是仅凭无反射推断 AOT 可用。

## 尚未完成的整体验证

全量解决方案构建仍失败，当时直接报告 18 个错误，全部涉及先前未确认的元数据 `init`、集合类型及相关调用。Importers 不能重新编译，继续阻塞依赖它的资产导入测试和场景工具完整验证。

默认资产生成工具的调用方编译检查已通过，但未运行资产重生成。单独检查依赖旧 Importers 编译产物的调用方，也不能当作完整工具构建通过。

后续需要在 [[Asset-Open-Questions]] 的设计确认并实现后，重新完成全量构建、被阻塞的相关行为测试和必要 AOT 验证。当前架构说明见 [[Asset-Files]]，加载、导入和应用边界分别见 [[Asset-Loading-and-Lifetime]]、[[Asset-Import-and-Storage]]、[[Asset-Application-and-Rendering]]。

测试入口：[资产消费行为](../../../tests/SomeEngine.Assets.Consumer.Tests/DiskAssetBehaviorTests.cs)、[World 行为](../../../tests/SomeEngine.Runtime.Tests/WorldAssetTests.cs)、[范围读取行为](../../../tests/SomeEngine.Serialization.Tests/RangeReadStreamTests.cs)、[原生检查工具](../../../tools/SomeEngine.Assets.AotSmoke/Program.cs)。

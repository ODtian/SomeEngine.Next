# Importer、Context 与工程存储

2026-09-29：本页规定开放导入架构及其当前 API。Asset 与 Block 的含义见 [对象模型](Asset-Objects.md)；SG 的限定职责见 [代码生成](Asset-Code-Generation.md)。实现未满足这些规则的具体位置见 [本轮审查](../../../reviews/AssetSpecReview-2026-09-29.md)。

原讨论 Thread 中的预生成固定组装链不再作为实现依据。当前流程由来源集成组织，Importer 只生产自己负责的 Block；项目引用的顺序、注册的顺序和遍历的顺序都不决定同一 Block 最终由谁写入。具体生产者由本次选择确定，竞争必须明确失败。

## 开放生产者，而不是固定导入链

扩展包可以携带任意数量的 Importer。Importer 直接接收自己认识的来源类型，例如 `SharpGLTF.Schema2.Mesh`、Material 或 Texture，不把第三方库的对象先转换为一套引擎通用来源描述。

`IAssetImporter<TSource, TSettings>` 负责局部转换。它声明自己生产的强类型 Block，通过 `ImportOutput.SetBlock` 提供结果，也可以通过 Context 读取其他生产者的结果。完整来源读取、元素遍历、语义资产 ID、输出路径、保存登记和重新导入由来源集成及 `AssetProject` 负责，不重复塞进每个局部 Importer。

同一来源对象、同一目标 Asset 可以由多个 Importer 协作。一个 Block 在同次结果中只有一个生产者，读者数量不受限制。配置阶段声明生产者后，冲突在执行前明确失败；不能通过执行先后覆盖对方。项目可以同时安装两个候选生产者，只有一次导入实际同时启用两者时才形成冲突。

## 三个配置作用域

| 作用域 | 保存的事实 | 生效位置 |
| --- | --- | --- |
| 项目引用与能力 | 当前项目有哪些 Block、Importer、依赖服务和编解码能力 | 编译时发现，生成 Microsoft DI 注册及能力描述 |
| 来源文件配置 | 实际启用的 Importer、各自参数、来源身份及输出身份对应关系 | 完整源文件名加 `.meta`，参与重新导入 |
| 单次调用覆盖 | 本次调用选择或参数覆盖 | 只作用于本次执行，不自动改写持久设置 |

SG 不生成完整导入流程，不决定遍历方式、材质策略、跨扩展调用或执行顺序。编辑器读取生成的能力描述，向用户呈现可选能力；这些描述不是业务载荷的弱类型解释器。设置的 JSON 只是持久表达，经对应静态设置读取器成为强类型参数，再进入 Importer。

新增来源、Importer 或 Block 不需要修改引擎中央类型分支。依赖服务通过普通 Microsoft DI 构造注入。每次导入使用明确的服务作用域，同一作用域解析到同一 Importer；作用域与来源内容的释放跟随该次导入。

该服务作用域保持到输出保存完成。`AssetProject.ImportAsync` 在保存和提交期间保留 `ImportContext`；Context 关闭时先取消并等待真实执行结束，再释放资产草稿和通过 `Hold` 保留的本次服务。来源对象在 Context 关闭后释放。Block 可以在保存时才取得来源数据，失败及取消也沿用这个清理顺序。

## Context 的执行与依赖规则

一次导入拥有一个 `ImportContext`。调用记录区分 Importer 实例、来源对象、目标资产及设置。同一调用的遍历请求和依赖请求共享执行结果，不重复构建。

需要依赖时，Importer 通过 Context 调用对应的已配置 Importer，并读取它的强类型结果。Context 检查依赖环和设置不一致；不要求事先规定一个固定顺序。取消一个等待者不会把其他等待者共享的任务取消；关闭 Context 会取消并等待真实执行结束，然后清理结果。

同一个 Context 的并发或重复 `DisposeAsync` 等待同一次关闭完成，不能仅因为已经拒绝新调用就提前返回。仍在执行的转换可以继续结束自己的工作；其输出在实际执行停止前不会被清理。关闭期间的清理错误也由同一次关闭结果报告。

几何生产者提供 `MeshBlock`，Cluster 生产者提供 `ClusterMeshBlock`。需要依赖的生产者可以通过 `context.GetBlockAsync<TBlock>(output.Target)` 请求对应数据；Context 按需执行生产者，执行顺序不依赖来源遍历顺序。当前 Cluster 转换直接读取原生 glTF 几何。每个生产者通过 `output.SetBlock` 交付自己的 Block，完整来源流程通过 `GetAssetAsync` 取得完成的语义资产。

`IClusterVertexEncoder` 由项目通过 Microsoft DI 明确提供，管线不据此引入默认顶点属性布局。替换只针对本次隔离的资产草稿，失败不登记输出。

## 完整流程与失败处理

`IAssetSourceImporter<TSource, TSettings>` 连接格式读取及遍历。`AssetProject` 读取来源配置、创建 Context 并组织保存提交；`ImportContext` 通过 `ConfigureAssets`、`EnableImporter` 和 `GetAssetAsync` 维护子元素到语义 Asset 的对应、输出草稿和本次执行期限。局部 Importer 不自行保存文件。

`AssetProject.ImportAsync` 选择来源及配置，读取来源，执行遍历和递归依赖，检查实际输入仍与本次读取一致，然后保存全部草稿。实现位于该 partial 类型的 `Importing/AssetImportProject.cs`。来源、设置、工具或扩展版本变化均参与重新导入判断。实际读取产生的依赖与构建记录分开保存，失败任务的依赖不冒充成功构建结果。

`.meta` 仅保存人工来源身份、Importer 选择、参数和元素 ID 映射；`.import.json` 保存可丢弃的输入及输出验证记录。删除或损坏后者不得改变来源或已保存元素身份，也不得从构建输出补回人工身份。目录重建从 .meta 的 ElementIds 恢复产物归属；来源保存直接维护主要身份映射。每个被选择的 Importer 都由本项目已注册能力解析。设置必须不可变，并以完整导入语义实现值相等。

输出、来源描述、构建记录和目录先在临时位置生成，检查身份与冲突，再沿现有文件替换流程提交。失败或取消会回退本次已经替换的文件，旧的有效目录保持可用。该保证覆盖进程内可观察的失败；不声称多个独立文件在断电或并发外部写入下具有文件系统整体原子性。

损坏的已登记生成产物需要重建，不能误当成另一个资产而永久阻止修复；有效的其他资产身份仍禁止被覆盖。消失的来源元素移除对应输出，但持久子元素身份映射保留，后续同一元素恢复不重新生成身份。来源没有自身持久元素标识时，项目必须明确选择并记录其身份策略，不能将临时遍历下标假装成可靠来源身份。

## 当前 glTF 项目集成

通用 `GltfMeshImporter` 只生产 `MeshBlock`，`ClusterGltfMeshImporter` 只生产 `ClusterMeshBlock`，`GltfTextureImporter` 生产 `TextureBlock`。选中的项目 `GltfSceneImporter` 遍历 primitive，直接请求纹理、生成或复用材质，并按 primitive 顺序组成 `MeshMaterialsBlock`。三个 Mesh 分支在 `MeshAsset` 汇合；节点结构、变换和 Mesh 引用在 `WorldAsset` 汇合。

`GltfSourceImporter` 只读取原生 glTF `ModelRoot`、建立来源元素身份并选择一个 `IAssetImporter<Scene, GltfSceneSettings>`。同一来源 Mesh、材质和纹理由 `ImportContext` 按各自的来源元素身份复用。`GltfSceneImporter` 给出节点遍历、层级、变换、Mesh 请求以及默认相机和灯光的公共组装；项目的具体场景 importer 只实现项目材质资产及必要的场景内容替换。不存在独立的材质选择器、材质转换接口或 importer 工厂。Scene 最终引用 `MeshAsset`；增加其他几何表示不改写 Scene 引用。图中的分支表达数据依赖，不承诺同时执行。

## 保存、目录和读取

来源移动和复制不能把格式内的相对路径当成无关字节。通用 `CopySourceAsync` 只允许同目录复制，创建新的来源及元素身份并保留人工选择；跨目录请求在写入任何文件前拒绝。原生资产的跨目录移动通过认识相应 Block 的强类型路径编辑函数完成。其他来源格式需要自己的引用改写，不以目标目录恰好有同名依赖作为正确性依据。

`AssetManifest` 是已登记条目的主数据；路径、来源输出和反向依赖是派生索引。目录查询不触发导入或编译。直接保存资产返回 `AssetEntry`，需要目录可见时由调用方明确登记；项目构建流程则统一完成这些步骤。

来源、资产与依赖通过一个 `catalog.json` 保存和切换，不再写入或回读 `source_index.json`、`asset_index.json`、`dependency_graph.json`。持续并发读取和替换目录的公开回归要求每次读到完整的一组信息。Windows 上的小型元数据替换使用静态生成的系统调用，保留已有读句柄，不以重试等待代替一致性；大型负载仍沿用原保存机制，不为了目录切换复制整份资产。

`LooseAssetStorage`、`AssetPackStorage` 与外部 `IAssetStorage` 负责按已选条目打开字节范围，不决定 Block 业务或对象创建。Loader 合并字节读取，不缓存唯一 Asset 或业务对象。包版本与内部文档版本分别核对，不能混用。

源码：[ImportContext](../../../src/SomeEngine.Assets/Importing/ImportContext.cs)、[导入项目](../../../src/SomeEngine.Assets/Importing/AssetImportProject.cs)、[提交与回退](../../../src/SomeEngine.Assets/Importing/AssetImportFiles.cs)、[glTF 库入口](../../../src/SomeEngine.Assets.Importers/Importers/GltfSourceImporter.cs)、[局部生产者](../../../src/SomeEngine.Assets.Importers/Importers/GltfContentImporters.cs)。

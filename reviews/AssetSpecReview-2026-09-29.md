# 当前 Asset 规范矛盾审查

2026-09-29。初次检查对象是 `F:/SomeEngine.Next` 当时工作区，包含开始审查前已经存在的未提交修改，不是仅检查 HEAD。初次审查和后续文档修订均没有修改产品实现。

以 [Asset 重构原则](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)、现行 Asset 主题页和 [2026-09-29 Shader 总规范](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md) 为依据。Shader 总规范第 12 节明确其为当前总规范，因此与它相反的旧主题页不作为实现的豁免依据。

审查覆盖 Asset/Block、加载和关闭、保存/目录、来源导入、Shader 构建及取得、Texture/Mesh/Material 转换、应用资源缓存、代码生成，以及相关文档。底层序列化和图形后端只沿这些调用路径核对，没有重新执行全部序列化、后端、AOT 或渲染性能验收。这份清单列出本轮已找到的矛盾，不构成剩余代码绝无缺陷的保证。

初次审查共记录 **21 项实现与规范的偏差、8 项现行文档矛盾、1 项当次重现的验收问题**。其中 9 项实现问题通过只使用公共 API 的临时行为检查复现；其余实现问题标为静态确认。D01–D08 的文档修订和 2026-09-30 的部分实现修复见文末，未逐项处理的问题继续保留。以下问题描述和行号是初次审查时的证据，不表示本次代码仍具有所有列出的缺陷。P1 表示会破坏内容一致性、正常使用路径或测试进程；P2 表示明确的能力、生命周期或架构偏差；P3 表示文档和迁移整理问题。

| 编号 | 级别 | 问题 | 证据 |
|---|---|---|---|
| I01 | P1 | 旧 Texture Block 能向更新后的 Texture 加入旧 mip | 公共行为复现 |
| I02 | P1 | Mesh 显式加载的新页不进入后续渲染提取 | 公共行为复现 |
| I03 | P1 | 删除构建记录再重建目录会阻断重新导入 | 公共行为复现 |
| I04 | P1 | Shader 的 `../` 来源可以构建，却不能创建使用对象 | 公共行为复现 |
| I05 | P1 | 带 Shader 引用的材质无法使用现有 `UpdateAsync` | 公共行为复现 |
| I06 | P2 | 源码编译仍在 Importer 中，Block 只接受已有 IR | 公共行为复现、调用链 |
| I07 | P2 | 发现流程遗漏尚无 `.meta` 的受支持来源 | 公共行为复现 |
| I08 | P2 | 第二次 Asset 关闭不等待实际读取结束 | 公共行为复现 |
| I09 | P2 | 目录登记丢弃文件实际保存的自引用 | 公共行为复现 |
| I10 | P2 | 应用 Shader 加载要求代码 Block 必须存在 | 静态确认 |
| I11 | P2 | Shader 的长期 Block 存储仍依赖页/COW | 静态确认 |
| I12 | P2 | 生成与文本入口把页存储作为强制前提 | 静态确认 |
| I13 | P2 | 应用缓存永久保留所有旧 Shader 版本 | 静态确认 |
| I14 | P2 | Mesh 永久保留所有历史提取内容 | 静态确认 |
| I15 | P2 | Shader 在异步等待之后才固定源数据分支 | 静态确认 |
| I16 | P2 | Shader 构建摘要可能对应未实际编译的新字节 | 静态确认 |
| I17 | P2 | 显式关闭 Texture Importer 仍会执行默认转换 | 静态确认 |
| I18 | P2 | glTF 默认以顺序下标决定元素对应关系 | 静态确认 |
| I19 | P2 | 默认持久类型身份随 CLR 名称变化 | 静态确认 |
| I20 | P2 | 保存 `.meta` 仍从构建输出补回元素身份 | 静态确认 |
| I21 | P2 | 旧 Importer 选择仍通过兼容分支静默改写 | 静态确认 |
| D01–D08 | P3 | 生命周期、来源格式、职责、API、验收和历史规则互相冲突 | 文档逐项对照 |
| V01 | P1 | 现有测试子集两次发生 Slang COM finalizer 原生崩溃 | 实际执行，原因未定位 |

**I01 — 旧 Texture Block 能污染新版本。**

规范要求补 mip 时核对内容修订，阻止旧资产像素接入已经更新的对象：[Asset-Textures.md:11](../wiki/architecture/Assets/Asset-Textures.md)。[TextureBlock.cs:87](../src/SomeEngine.Assets/Schema/TextureBlock.cs) 只在调用开始时读取目标 Texture 的当前 `Revision`，再比较尺寸、格式和 tile 描述；这些描述没有像素内容身份。[Texture.cs:107](../src/SomeEngine.Resources/Texture.cs) 只确认目标在此次读取期间没有再次改变。

复现：旧、新资产保持相同 ID、尺寸、tile 元数据，仅改变像素；用新 Block 更新 Texture 后，再用仍打开的旧 Block 加载细 mip。调用成功，旧 mip 被接入新版本。缺少的是源内容与目标版本的对应关系，不能仅增加一次目标 revision 比较。

**I02 — Mesh 已取得的数据没有发布给下一次提取。**

规范要求显式流送完成后更新可用状态，再沿 Game world → Rendering world 更新渲染：[Asset-Application-and-Rendering.md:17](../wiki/architecture/Assets/Asset-Application-and-Rendering.md)。[Mesh.cs:171](../src/SomeEngine.Resources/Mesh.cs) 的 `AddPages` 替换缓冲后既不使提取缓存失效，也不发出 `Changed`；[Mesh.cs:26](../src/SomeEngine.Resources/Mesh.cs) 仅按 `Revision` 复用上一次 `CaptureData`。[MeshRenderExtractor.cs:77](../src/SomeEngine.Render/Systems/MeshRenderExtractor.cs) 同时依赖这两项。

复现：创建只含 BVH 的 Mesh，先 `CaptureData()`，再 `LoadPagesAsync(mesh, [0])`。原 Mesh 已能取得第 0 页，下一次 `CaptureData()` 仍取不到。需要使“就绪页变化”进入现有提取边界；不能用资产到渲染的另一个通路补偿。

**I03 — 可丢弃构建记录成为重新导入所需的身份依据。**

规范规定 `.meta` 保存元素身份，`.import.json` 可丢弃，原生来源及身份描述足以重建目录：[Asset-Import-and-Storage.md:47](../wiki/architecture/Assets/Asset-Import-and-Storage.md)、[Asset-Rebuild-Principles.md:212](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)。[AssetProject.Files.cs:90](../src/SomeEngine.Assets/AssetProject.Files.cs) 重建产物归属时只遍历 `metadata.Outputs`，没有使用仍然完整的 `ElementIds`；记录缺失时产物的 `SourceGuid` 被登记为空。[AssetImportProject.cs:124](../src/SomeEngine.Assets/Importing/AssetImportProject.cs) 随后把这些相同 ID 的产物视为另一来源的资产。

复现：导入成功 → 删除 `.import.json` → `RebuildAsync()` → 再导入。最后抛出 `Asset ... belongs to another source`。应从主要身份数据恢复关系，而不是重新依赖构建记录或豁免所有身份冲突。

**I04 — 构建和加载采用两套来源路径规则。**

规范保留 Slang 原生相对路径及项目外引用的相对关系：[Asset-Rebuild-Principles.md:233](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)、[Asset-Application-and-Rendering.md:9](../wiki/architecture/Assets/Asset-Application-and-Rendering.md)。[SlangShaderDefinition.cs:80](../src/SomeEngine.Assets.Importers/Importers/SlangShaderDefinition.cs) 接受并解析 `../test.slang`，编译及 IR 保存成功；[ShaderSourceBlock.Slang.cs:211](../src/SomeEngine.Assets/Schema/ShaderSourceBlock.Slang.cs) 在恢复已经保存的 IR 时拒绝 `..`、`.` 和空路径段。

公共检查确认：同一构建成功的资产调用 `ShaderBlock.CreateAsync` 失败，错误为 `Shader source names must be normalized relative paths`。构建输入路径与持久化模块名称需要有一致的转换规则。

**I05 — 材质更新入口没有接入显式 Shader 取得参数。**

规范保留材质对象更新行为：[Asset-Materials.md:21](../wiki/architecture/Assets/Asset-Materials.md)。[MaterialAsset.cs:292](../src/SomeEngine.Assets/Schema/MaterialAsset.cs) 的 `UpdateAsync` 仍调用不带 Shader 加载行为的 `CreateAsync(loader, token)`，而 [同文件:225](../src/SomeEngine.Assets/Schema/MaterialAsset.cs) 对任何非空 Shader 引用都要求显式加载行为。

复现使用实际可加载的 `hello_triangle` Shader、已登记目录和已经持有该 Shader 的 StandardMaterial，更新仍抛出 `Creating a material with a shader requires an explicit shader-loading behavior`。需要让更新采用与创建一致的调用边界；不能让 Loader 猜 target。现有 `MaterialReloadTests` 的更新测试未设置 Shader，因此没有覆盖该场景。

**I06 — 源码编译不是统一的异步 Block 行为。**

总规范要求源码、IR、已有代码和缓存都通过 Block 取得，Importer 复用同一编译行为：[Shader 总规范:73、235、504](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)。实际 [ShaderSourceBlock.Slang.cs:117](../src/SomeEngine.Assets/Schema/ShaderSourceBlock.Slang.cs) 在没有序列化 IR 时直接失败；真正的 `LoadModuleFromSource`、入口收集、compose/link/codegen 另写在 [SlangShaderDefinition.cs:106](../src/SomeEngine.Assets.Importers/Importers/SlangShaderDefinition.cs)。

公共检查给 Block 提供存在的源码名称，并在 `SessionDesc` 配置来源目录，调用 `CreateAsync` 得到 `has no serialized Slang data`。当前确实支持用户机器上的 IR 编译，但这不等于原生源码编译也已进入 Block 边界。两套实现已经导致 I04 的行为分叉。

**I07 — Discover 只能发现已经有伴随信息的外部来源。**

规范把发现定义为检测来源的新增、删除和变化：[Asset-Rebuild-Principles.md:55](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)。[AssetProject.Files.cs:62](../src/SomeEngine.Assets/AssetProject.Files.cs) 识别二进制资产、原生定义以及已有 `.meta` 的文件，没有使用已注册来源 Importer 的格式能力识别新增文件。

公共检查注册支持 `.input` 的来源 Importer，创建 `source.input`，`DiscoverAsync()` 返回空集合。发现新来源不应要求它先被导入，也不必为了发现就执行转换。

**I08 — Asset 的“关闭完成”取决于调用者是否是第一个。**

规范要求关闭等待实际取得结束：[Asset-Objects.md:36](../wiki/architecture/Assets/Asset-Objects.md)、[Asset-Loading-and-Lifetime.md:28](../wiki/architecture/Assets/Asset-Loading-and-Lifetime.md)。[Asset.cs:196](../src/SomeEngine.Assets/Asset.cs) 在第一次关闭开始即设 `_disposed`，后续调用直接返回；第一次调用仍在等待第 213 行的实际读取。

公共检查使用暂时不能响应取消的扩展读取器，第一次 `DisposeAsync` 未完成，第二次已经完成。调用者据第二次结果结束外部服务或资源的使用期限会过早。Context 和 Loader 已保存共同关闭任务，Asset 未采用相同的完成语义。

**I09 — 保存依赖与登记依赖不一致。**

规范只对直接引用过滤空值、去重，并允许引用图存在环，不能因循环拒绝发现和保存：[Asset-Identity-and-Dependencies.md:29](../wiki/architecture/Assets/Asset-Identity-and-Dependencies.md)、[Asset-Rebuild-Principles.md:224](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)。[AssetManifest.cs:386](../src/SomeEngine.Assets/AssetManifest.cs) 额外删除 `dependency == owner`。

公共检查保存 Parent 指向自身的 MaterialAsset：保存结果正确包含自身 ID，登记后 `GetDependencies` 变成空集合。父链不能创建使用对象应由转换边界报告；目录不应修改文件中的引用事实。

**I10 — 正式应用加载路径不支持只有 ShaderSourceBlock 的合法资产。**

规范允许源 Block 与代码 Block 分别取得同一种 Shader，目标代码可以缺失：[Shader 总规范:14、75、265](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)。[ShaderRef.cs:45](../src/SomeEngine.Assets/Schema/ShaderRef.cs) 已按 Block 是否存在选择路径，但应用材质加载使用的 [RuntimeShaderResources.cs:159](../src/SomeEngine.Runtime/RuntimeShaderResources.cs) 无条件请求 `ShaderBlock`。

同一 source-only Shader 在配置加载路径可被编译，在应用材质加载路径却先因缺少代码 Block 失败。应复用共同的取得选择行为。

**I11 — Shader 页存储仍是长期内容模型。**

规范禁止 Shader 以通用页/COW 作为驻留模型，普通代码应直接物化到最终 GC 存储：[Shader 总规范:94、117、328](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)。[ShaderBlock.cs:16、159](../src/SomeEngine.Assets/Schema/ShaderBlock.cs) 和 [ShaderSourceBlock.cs:8](../src/SomeEngine.Assets/Schema/ShaderSourceBlock.cs) 仍继承 `AssetPageBlock`，编译代码/IR 被写入 Block 的长期 `Chunks`，分支继续采用页 COW。

[ShaderBlock.cs:239](../src/SomeEngine.Assets/Schema/ShaderBlock.cs) 的取得路径先经 [AssetFile.cs:75](../src/SomeEngine.Assets/AssetFile.cs) 解码并缓存成页，再分配最终 `byte[]` 复制。最终 Shader 本身已经没有 Dispose，这一点符合规范；但不能据此把仍长期驻留的 Block 页称作一次操作内的临时页。当前还存在缓存页与最终数组同时保留的成本。

**I12 — 普通 GC Block 没有与页 Block 对等的生成和文本入口。**

规范让 Block 自己选择 backing，不把页系统作为默认条件：[Asset-Objects.md:32](../wiki/architecture/Assets/Asset-Objects.md)、[Asset-Rebuild-Principles.md:119](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)。[AssetBlockGenerator.cs:59](../src/SomeEngine.Generators/AssetBlockGenerator.cs) 在声明 `[BinarySerializable]` 和 `[BinaryFields]` 时强制继承 `AssetPageBlock`，并无条件生成标量/引用页和 PageList。[AssetText.cs:101、237](../src/SomeEngine.Assets/AssetText.cs) 的文本读写同样把 `AssetPageBlock` 写入泛型约束。

手写非页 Block 可以存在，但无法使用与页 Block 对等的生成和文本流程。这使“按业务语义选择 GC 数据”需要退出主要便利入口，与当前默认存储规则不一致。

**I13 — Shader 缓存只增加版本，不结束旧版本的保留。**

规范要求旧消费者结束后，旧 Shader 不被缓存意外保活：[Shader 总规范:84、629](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)。[RuntimeShaderResources.cs:27、169](../src/SomeEngine.Runtime/RuntimeShaderResources.cs) 以 ID 和版本为键保存 Shader，只有增加，没有移除旧版本、容量限制或其他结束保留的操作。

同一 Shader 重建多次，旧代码数组、Slang session/component/blob 会一直保留到整个应用缓存不可达。交换材质引用不能解除这份持有。缓存应明确当前版本与仍在使用版本的保留范围。

**I14 — Mesh 提取历史也只增加，不按读者期限结束。**

规范让旧读者和业务分页按各自期限保留内容：[Asset-Objects.md:36](../wiki/architecture/Assets/Asset-Objects.md)、[Asset-Loading-and-Lifetime.md:40](../wiki/architecture/Assets/Asset-Loading-and-Lifetime.md)。[Mesh.cs:16、28](../src/SomeEngine.Resources/Mesh.cs) 把每个新 `CaptureData` 永久加入 `_capturedContents`，唯一清理在 [同文件:233](../src/SomeEngine.Resources/Mesh.cs) 的 Mesh 关闭。

长期 Mesh 反复更新，即使没有任何旧帧使用此前版本，旧页仍持续计费，可能耗尽业务分页预算。这里需要让实际读者保留所需内容，而不是让当前 Mesh 保存全部历史。

**I15 — 等待 Slang 编译锁时没有固定本次源版本。**

规范要求异步操作前取得元数据和来源，编辑形成新分支：[Shader 总规范:330](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)。[ShaderSourceBlock.Slang.cs:96](../src/SomeEngine.Assets/Schema/ShaderSourceBlock.Slang.cs) 先等待共享 semaphore，第 99 行才 Duplicate。与之配合的 [ShaderBlock.cs:77](../src/SomeEngine.Assets/Schema/ShaderBlock.cs) 已经先固定代码 Block。

同一 Slang global session 有排队操作时，调用方在等待期间编辑或关闭源 Block，读取就会采用较晚的源内容或因关闭失败；保存代码与反射还可能来自不同分支。应在首次异步等待前固定完整输入，而不是延长资产总体生命周期。

**I16 — 构建记录摘要不是实际编译字节的摘要。**

规范要求检查实际读取输入没有变化，旧成功内容在失败时继续有效：[Asset-Import-and-Storage.md:45](../wiki/architecture/Assets/Asset-Import-and-Storage.md)、[ShaderSources.md:82](../docs/ShaderSources.md)。[SlangShaderDefinition.cs:110](../src/SomeEngine.Assets.Importers/Importers/SlangShaderDefinition.cs) 先读源码字节给 Slang，后在第 129 行调用 [SlangDeps.cs:16](../src/SomeEngine.Assets.Importers/Importers/SlangDeps.cs) 重新打开文件计算摘要。

若源码在第一次读取后、Collect 前被改写，代码来自旧字节，记录却得到新摘要；[AssetProject.cs:110](../src/SomeEngine.Assets/AssetProject.cs) 再次核验新文件会成功。需要记录实际输入的摘要或保证输入读取版本一致。此项是静态确认的时序窗口，本轮没有用竞争写入探针复现。

**I17 — 来源配置不能真正关闭 Texture Importer。**

规范要求本次启用项来自来源选择/单次覆盖：[Asset-Import-and-Storage.md:13、20](../wiki/architecture/Assets/Asset-Import-and-Storage.md)。[GltfSourceImporter.cs:126](../src/SomeEngine.Assets.Importers/Importers/GltfSourceImporter.cs) 在没有启用的 Texture Importer 时直接 new 一个默认转换并启用，不检查它是否被明确禁用，也不受 `UseDefaultImporters` 约束。

于是选择“禁用”仍产生转换，且绕过项目 DI 提供的依赖及对应启用版本描述。必需生产者缺失应由配置边界明确处理，不能把明确禁用解释为默认启用。

**I18 — glTF 下标身份没有经过明确的来源策略选择。**

规范要求来源无持久元素 ID 时，由项目明确选择并记录策略：[Asset-Import-and-Storage.md:51](../wiki/architecture/Assets/Asset-Import-and-Storage.md)。[GltfImporterSettings.cs:13](../src/SomeEngine.Assets.Importers/Importers/GltfImporterSettings.cs) 默认不要求元素 ID；[GltfSourceImporter.cs:195](../src/SomeEngine.Assets.Importers/Importers/GltfSourceImporter.cs) 自动使用 `kind:index:N`，只发一条 Trace 警告。

当前模板 [ico_sphere.glb.meta](../templates/DefaultProject/assets/Models/ico_sphere.glb.meta) 已保存这类 key，但没有来源 Importer 的明确身份策略设置。持久化“下标 → GUID”不能让元素重排后仍对应原语义元素。应把这种有损策略变成明确选择，或要求来源提供 ID。

**I19 — 默认类型身份仍由 C# 名称决定。**

规范规定文件类型标识不直接等同于 C# 完整名，代码重命名不改变持久类型身份：[Asset-Rebuild-Principles.md:214](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)。[AssetGenerator.cs:58](../src/SomeEngine.Generators/AssetGenerator.cs) 在省略 `SchemaName` 时使用 `type.ToDisplayString()`；[AssetBlockGenerator.cs:56](../src/SomeEngine.Generators/AssetBlockGenerator.cs) 在省略语义名称时用完整类型名计算 Block ID。

已有显式名称的内置 Block 不受这条默认路径影响，但公开生成入口允许外部扩展仅重命名类/namespace 就改变磁盘身份，且没有诊断要求固定名称。这与规范的无条件重命名保证不一致。

**I20 — `.meta` 写入仍把构建输出当作身份补全来源。**

规范明确删除从构建输出回填身份的兼容行为：[Asset-Import-and-Storage.md:47](../wiki/architecture/Assets/Asset-Import-and-Storage.md)。[MetaFiles.cs:125](../src/SomeEngine.Assets/MetaFiles.cs) 每次 `AddFiles` 仍遍历 `meta.Outputs`，通过 `ElementIds.TryAdd(output.SubAssetKey, output.AssetGuid)` 补写人工身份映射。

`SourceMeta.Load` 中的 Outputs 来自 `.import.json`；因此通用保存/移动旧来源时，衍生数据仍可能决定缺失的主要身份。正常新导入已经由 Context 建立 `ElementIds`，不需要通过构建输出再维护这份事实。

**I21 — 旧 Importer 选择被保留为静默迁移路径。**

规范要求替代完成后不保留旧资产/导入表示的兼容回退：[Asset-Rebuild-Principles.md:37](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)、[Asset-Import-and-Storage.md:47](../wiki/architecture/Assets/Asset-Import-and-Storage.md)。[GltfSourceImporter.cs:19、168](../src/SomeEngine.Assets.Importers/Importers/GltfSourceImporter.cs) 仍识别两个 `Legacy...SelectionId`，静默删除旧选择，再补充新 Scene Importer。

旧配置不是被明确拒绝或由独立迁移工具转换，而是在正常输入检查期间改变。该兼容路径也使输入检查承担修改人工选择的职责。

**D01 — Shader 的公开生命周期说明仍互相否定。**

[Asset-Shaders.md:36](../wiki/architecture/Assets/Asset-Shaders.md)、[Shader-Build.md:11](../wiki/architecture/shader/Shader-Build.md)、[ShaderSources.md:72](../docs/ShaderSources.md) 要求 Shader 保留已有代码页，其中最后一份明确承诺不复制。与 [Shader 总规范:18、94、117](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)、[Asset-Sharing-and-Writes.md:17](../wiki/architecture/Assets/Asset-Sharing-and-Writes.md) 的 GC 数组/禁止页生命周期结论相反。

[ShaderSources.md:49、63](../docs/ShaderSources.md) 的两段示例仍写 `using Shader shader`，而当前 Shader 不实现 IDisposable，这两段示例无法编译。这里应修正文档，而不是给 Shader 恢复 Dispose。

**D02 — 通用热重载规则仍要求所有对象保持原引用。**

[Hot-Reload.md:4、8](../wiki/architecture/Assets/Hot-Reload.md) 无条件要求写入原长期对象并增加 revision；[Asset-Validation.md:17](../wiki/architecture/Assets/Asset-Validation.md) 也笼统要求更新保留身份。与 [Asset-Object-Identity.md:11](../wiki/architecture/Assets/Asset-Object-Identity.md)、[Asset-Loading-and-Lifetime.md:40](../wiki/architecture/Assets/Asset-Loading-and-Lifetime.md) 规定 Shader 创建完整新实例、交换外层引用相反。通用页必须明确区分不可变替换与确有可变业务身份的类型。

**D03 — `.shader` 的人工选择范围没有统一。**

[Asset-Rebuild-Principles.md:235、238、329](../wiki/architecture/Assets/Asset-Rebuild-Principles.md) 要求定义保存源码/入口组合和资产特有编译选择。现行 [Asset-Shaders.md:49](../wiki/architecture/Assets/Asset-Shaders.md)、[ShaderSources.md:9、23](../docs/ShaderSources.md)、[Shader-Composition.md:10](../wiki/architecture/shader/Shader-Composition.md) 则规定只有 SourceNames、所有声明入口一起组成、不保存入口选择。

这影响实际可表达的资产，不只是术语差异。应依当前总规范明确哪些构建选择允许持久化；不能同时承诺“可选入口”和“总是全部入口”。

**D04 — Texture 的设备数据归属和业务类型所属层仍有旧结论。**

[Texture.md:4、17](../wiki/architecture/Assets/Texture.md) 称 Texture 为资产对象且不拥有设备 backing；[Asset-Object-Identity.md:13](../wiki/architecture/Assets/Asset-Object-Identity.md) 仍把 Texture/Material/Mesh 对外身份归到 Assets 业务 API。与 [Asset-Responsibilities.md:25](../wiki/architecture/Assets/Asset-Responsibilities.md)、[Asset-Textures.md:19](../wiki/architecture/Assets/Asset-Textures.md) 的 Resources 业务类型、Texture 设备关联职责不一致。

实现也明确由 Texture 保存设备数据字典并在关闭时调用其 Release：[Texture.Devices.cs:17、166](../src/SomeEngine.Resources/Texture.Devices.cs)。应说明业务对象与设备对象的区别及各自持有关系，删除互相排斥的归属陈述。

**D05 — 现行使用文档仍指向不存在的入口。**

- [Asset-Import-and-Storage.md:27、43](../wiki/architecture/Assets/Asset-Import-and-Storage.md) 把生命周期和草稿职责交给 `AssetImportBatch`；当前源码没有该类型，实际由 ImportContext 和 AssetProject 完成。
- [AssetSources.md:23、25](../docs/AssetSources.md) 描述生成的 `OpenSourceAsync` 和 `AssetText.ReadIntoAsync`；当前 AssetGenerator 没有生成前者，后者不存在，实际为 `AssetText.OpenAsync` / `SourceReader.ReadBlockIntoAsync` 及具体资产的显式接入。
- [AssetPages.md:58、60](../docs/AssetPages.md) 仍描述 Slang 字节码经 PageBufferStream 读写，以及 `Asset.Chunks`；当前 Chunks 属于 AssetPageBlock，Shader 当前取得实现见 I11。

这些陈述会让扩展实现者寻找或依赖已不存在的职责边界。

**D06 — 通用 Bootstrap 完成条件与 Mesh 创建条件相反。**

[Bootstrap-Streaming.md:4、7](../wiki/architecture/Assets/Bootstrap-Streaming.md) 规定任何渐进内容必须具备足以产生有效结果的常驻 Bootstrap 才视为加载成功；[Asset-Meshes.md:19](../wiki/architecture/Assets/Asset-Meshes.md) 则在 BVH 取得后即创建 Mesh，几何页以后读取。实现 [ClusterMeshBlock.cs:114](../src/SomeEngine.Assets/Schema/ClusterMeshBlock.cs) 确实只交付 BVH，即使最低细节页尚无一页可用。

应区分“Mesh 描述可用”和“Mesh 已具备最低绘制表示”，或明确要求哪一个公开加载边界负责完成最低细节页取得。不能让两个页面对同一个“加载成功”给出不同保证。

**D07 — WARP 完整应用验收被错误地写成当前通过。**

[Asset-Application-and-Rendering.md:27](../wiki/architecture/Assets/Asset-Application-and-Rendering.md) 说 WARP 与 RTX 3080 都完成 30 帧输出校验；其引用的 [最终验证记录:135](../wiki/architecture/Assets/Asset-Verification-2026-09-16.md) 明确说明本次没有完成 WARP 完整资产应用场景。[Asset-Open-Questions.md:19](../wiki/architecture/Assets/Asset-Open-Questions.md) 同样限定 WARP 是独立设备测试。

旧阶段确实留有 WARP 通过记录，但不能把它重写成最新完整验收结果。这里应按验收日期和运行范围修正主题页。

**D08 — 是否允许在现行架构目录保留历史陈述，规则自相矛盾。**

[Shader 总规范:559、566](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md) 要求冲突历史只留在 Git；[Asset-Rebuild-Execution.md:3](../wiki/architecture/Assets/Asset-Rebuild-Execution.md) 和 Inventory 页也采用这一规则。另一方面 [Asset-Files.md:3](../wiki/architecture/Asset-Files.md)、[Asset-Rebuild-Tasks.md:9](../wiki/architecture/Assets/Asset-Rebuild-Tasks.md)、[验证记录:143](../wiki/architecture/Assets/Asset-Verification-2026-09-16.md) 明确保留此前阶段历史。

历史日期本身不是实现缺陷；矛盾在于一组现行维护规则要求删除，另一组又要求保留。应统一归档规则及权威入口，避免修一页后又从另一页恢复旧结论。

**V01 — 现有测试组合仍能触发原生崩溃，不能维持“相关验收全部完成”的当前判断。**

[Shader 总规范:383、388、704、705](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md) 记录官方 Slang 包的强制 GC 和相关资产测试已通过，状态为实现与验证完成。本轮在删除临时测试并重建测试程序集之后，两次执行下面同一个现有测试子集，均中止于原生 finalizer：

```powershell
dotnet test tests/SomeEngine.Assets.Tests/SomeEngine.Assets.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~AssetFileOperationsTests|FullyQualifiedName~AssetImportTests|FullyQualifiedName~MaterialReloadTests|FullyQualifiedName~TextureMipTileStreamingTests|FullyQualifiedName~DirectSlangShaderTests|FullyQualifiedName~AssetBlockBehaviorTests' -v minimal
```

第二次增加 `--logger 'console;verbosity=normal'` 以记录执行进度，选择范围相同。两次结果分别在 **29 项通过**、**44 项通过**后中止，退出码都为 1。错误栈一致：

```text
Fatal error. 0xC0000005
System.Runtime.InteropServices.Marshal.Release(IntPtr)
System.Runtime.InteropServices.Marshalling.ComObject.Finalize()
System.GC.RunFinalizers()
```

独立执行 `--filter FullyQualifiedName~DirectSlangShaderTests` 为 **5/5 通过**。因此本轮证据只能确认组合运行仍能触发原生崩溃，不能认定某一个测试、某一种对象持有方式或官方包就是根因，也不能据此恢复手工 FinalRelease。两个中止结果不能以已通过的部分项目数量计为验收通过。

**本轮行为核对与清理记录**

临时检查只调用公共资产、Block、资源及工程 API，没有读取私有字段、反射内部结构或断言生成源码。第一组 6 项、第二组 3 项均按规范期望编写断言，并在对应问题处失败；它们是缺陷复现，不是产品回归通过。

| 公共行为检查 | 实际结果 | 对应问题 |
|---|---|---|
| 更新 Texture 后从旧 Block 补 mip 应拒绝 | 没有抛出异常 | I01 |
| 显式加载 Mesh 页后下一次提取应可见 | 原 Mesh 可见，提取内容不可见 | I02 |
| 删除构建记录并重建目录后应能重新导入 | 来源归属冲突异常 | I03 |
| `../` 来源构建成功后应能创建 Shader | 相对路径规范化异常 | I04 |
| 已登记有效 Shader 的材质应能走更新入口 | 缺少显式 Shader 加载行为异常 | I05 |
| Block 应支持从可取得源码进入编译 | 缺少序列化 IR 异常 | I06 |
| 新增受支持来源应被发现 | 发现结果为空 | I07 |
| 每次 Asset 关闭应等待实际读取 | 第二次关闭提前完成 | I08 |
| 登记依赖应等于保存文件的直接依赖 | 自引用被删除 | I09 |

临时 `SpecReviewTests.cs` 在每组执行后立即删除；最后重建 `SomeEngine.Assets.Tests` 成功，0 警告、0 错误，测试程序集已不含临时检查。临时来源、资产和目录由检查中的 finally/Dispose 清理，并再次检查未留下本次使用的临时目录。未保存原始测试输出或转储。初次审查保留的正式交付只有这份审查记录。

优先处理 I01–I05 的内容一致性和公共使用路径，并定位 V01；随后收敛 I06、I10–I16 的共同数据取得及版本保留边界。文档应跟随明确的最终行为同步，不能通过放宽规范把已经复现的错误解释成通过。

**2026-09-29 后续文档修订**

修订采用同一职责划分：总规范规定应有行为，API 文档说明当前入口和实现差距，带日期的验证记录保存当时执行事实。Shader 总规范继续作为该领域的权威；本轮没有通过改写行为要求关闭实现问题。

| 编号 | 文档修订结果 | 主要位置 |
|---|---|---|
| D01 | Shader 持有最终 GC 数组或直接 Slang blob；示例删除 `using Shader`；当前页存储和复制差距仍列为 I11 | [Shader 主题](../wiki/architecture/Assets/Asset-Shaders.md)、[使用文档](../docs/ShaderSources.md)、[构建规则](../wiki/architecture/shader/Shader-Build.md) |
| D02 | 不可变 Shader 交换外层引用；有可变业务身份的类型使用自身更新 API | [热重载](../wiki/architecture/Assets/Hot-Reload.md)、[更新边界](../wiki/architecture/Assets/Asset-Update-Boundary.md)、[验证](../wiki/architecture/Assets/Asset-Validation.md) |
| D03 | 当前 `.shader` 保存身份和 SourceNames；入口由 Slang 发现，目标、编译选项与特化由调用边界提供；未来扩展先修订 schema | [重构原则 F15](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)、[Shader 总规范第 5 节](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)、[任务 A22](../wiki/architecture/Assets/Asset-Rebuild-Tasks.md) |
| D04 | Resources.Texture 持有像素与设备关联，设备数据管理原生 backing；Assets 负责持久身份和转换 | [Texture](../wiki/architecture/Assets/Texture.md)、[对象身份](../wiki/architecture/Assets/Asset-Object-Identity.md) |
| D05 | 删除不存在的入口，按 AssetProject / ImportContext、AssetText.SourceReader、AssetPageBlock.Chunks 说明职责 | [导入](../wiki/architecture/Assets/Asset-Import-and-Storage.md)、[可读来源](../docs/AssetSources.md)、[页接口](../docs/AssetPages.md) |
| D06 | 区分描述/BVH 创建完成与最低可绘制表示就绪；后者仍要求最低细节几何页，I02 继续未关闭 | [Bootstrap](../wiki/architecture/Assets/Bootstrap-Streaming.md)、[Mesh](../wiki/architecture/Assets/Asset-Meshes.md) |
| D07 | WARP 独立设备测试与完整应用验收分开；通过数字绑定日期和范围 | [应用接入](../wiki/architecture/Assets/Asset-Application-and-Rendering.md)、[验收边界](../wiki/architecture/Assets/Asset-Open-Questions.md) |
| D08 | 被替代规则留在 Git 历史；带日期的验证/审查证据可保留，但不定义当前行为，也不覆盖较晚失败 | [架构导航](../wiki/architecture/Asset-Files.md)、[Shader 总规范第 12 节](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)、[验证记录](../wiki/architecture/Assets/Asset-Verification-2026-09-16.md) |

同时撤销当前“实现与验证全部完成”的状态表述，保留较早通过记录的日期与范围，链接本轮 V01 的两次组合测试中止。文档修订不代表源码、生命周期、身份或回归问题已经修复。

本轮文档检查覆盖 25 份文件、273 个本地链接，未发现缺失目标、未闭合代码块或行尾空白；24 份已跟踪文档的 `git diff --check` 返回 0。对已修订范围再次搜索，D01–D08 对应的过时 API 和冲突表述未残留。此次只修改 Markdown，没有重新执行产品测试或渲染性能验收。

## 2026-09-30 数据存储重构后的状态

此后按新设计修改了代码与文档，具体执行证据见 [本次验证](../wiki/architecture/Assets/Asset-Verification-2026-09-30.md)。以下仅更新此次实际处理的具名问题，不将初次清单整体关闭。

| 编号 | 本次处理与证据 |
| --- | --- |
| I01 | Texture 内容版本随对象更新，旧 Block 加载同尺寸新版本被拒绝；AssetDataSharingTests 的公共行为回归通过 |
| I02 | Mesh 补页增加变更状态、发出 Changed 并使提取缓存失效；MeshTests 验证旧提取保留原值、下一次提取可见新页，渲染接入子集通过 |
| I11 | Shader Block 改为普通字段和托管字节，保存代码直接读最终数组；通用 CPU 页和 COW API 已移除，相关资产与 NativeAOT 行为通过 |
| I12 | Block 生成器、AssetText 和外部消费者不再要求页基类；普通字段、集合、文本保存和外部扩展回归通过 |
| I14 | Mesh 删除永久历史提取列表，只保留当前提取结果；旧版本由实际读者的普通引用保留，不再由当前 Mesh 累积所有历史 |
| I15 | ShaderSourceBlock 的 Duplicate 已移到等待 Slang 编译锁之前；本次静态复核确认顺序，未单独执行锁排队期间编辑来源的竞争检查 |
| V01 | 原组合再次在 26 项已执行项目通过后发生同一 COM finalizer 原生崩溃；退出码 1，根因未定位，继续未关闭 |

其余实现问题没有在本次数据存储任务中逐项验收，保留初次证据。本次 381 项定向测试、AOT 和三场景反馈的成功不覆盖 V01，也不表示所有导入、身份和应用缓存问题都已解决。

## 2026-10-01 规范对齐后的状态

当前缓存采用 [统一整数 LRU 字节池](../wiki/architecture/Assets/Asset-Byte-Cache.md)。本次实现、公开 API 基准、582 项回归、Native AOT 和 43.32 秒三场景完整反馈见 [本次验证](../wiki/architecture/Assets/Asset-Verification-2026-10-01.md)。以下表格更新具名实现问题；前面的日期证据保留其当时范围。

| 编号 | 当前处理 | 本轮证据 |
|---|---|---|
| I01、I02 | 既有版本匹配、页就绪变更和提取缓存修复继续有效 | Asset 全量、Resources 全量及 Render 提取回归 |
| I03 | 目录重建以 ElementIds 恢复源归属 | 删除构建记录、重建、再次导入保持身份的公共测试 |
| I04 | 来源路径统一规范化，保留相对外部路径 | ../ 源码编译、保存 Slang 数据、删除源码后恢复 |
| I05 | UpdateAsync 接收显式 Slang 参数或加载行为 | 实际 Shader 的材质更新公共测试 |
| I06 | ShaderSourceBlock 统一编译；Importer 复用 CompileAsync | 源码、序列化数据、代码、摘要公共行为回归与内容构建 |
| I07 | Discover 使用注册的来源格式 | 缺少 .meta 的受支持来源被发现，发现保持读取行为 |
| I08 | 全部关闭调用等待同一实际完成边界 | 两次关闭在未完成 codec 读取期间等待 |
| I09 | 保留自引用，只过滤空值和重复 | 目录自依赖公共查询测试 |
| I10 | 应用依据可用 Block 选择 Shader 取得入口 | RuntimeShaderResources 调用链检查、源 Block 回归及实际应用运行 |
| I11、I12 | 托管字节和普通 Block 生成/文本路径继续有效 | 全量 Asset、外部扩展和 Native AOT |
| I13 | 每个资产保留当前成功 Shader；读取序号处理完成次序 | 缓存写入路径检查、失败保留行为及实际应用运行 |
| I14、I15 | 提取历史由读者保留；异步前固定源分支 | 既有 Mesh、Shader 和提取公共回归 |
| I16 | Slang 文件系统记录实际提供的字节摘要 | 编译读取后修改文件，结果摘要仍对应旧字节的公共测试 |
| I17 | 转换器来自显式选择和项目服务 | 禁用纹理生产者后旧产物保持有效的公共测试 |
| I18 | 默认持久 ID；下标身份明确写入来源设置 | 缺少持久 ID 的来源先失败，显式选择后导入；球体、Sponza、Bistro 重建 |
| I19 | Asset/Block 持久名称明确声明；编码默认沿用持久名称 | 外部 CLR 重命名保持 TypeId 的公共生成入口测试 |
| I20 | 来源保存维护主要身份映射，移除由 Outputs 补全身份的路径 | 保存路径检查、来源与删除构建记录回归 |
| I21 | 所有选项使用当前已注册能力解析 | 选择解析检查、外部能力回归及完整内容构建 |
| V01 | Importer 使用进程级编译服务持有全局会话，入口按会话串行访问 | 固定官方 2026.17.1 上原组合加 GC 回归 64 项通过，完整 Asset 进程 240 项通过 |

V01 的证据支持编译服务持有关系影响回收顺序的推断，观察范围与验证命令见本次验证。官方依赖保持 2026.17.1，应用和发布的 Shader 通过普通托管引用保留直接 Slang 对象。

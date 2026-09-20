# 资产文件与运行对象

> **重构范围说明（2026-09-12）。** [冻结原则](../wiki/architecture/Assets/Asset-Rebuild-Principles.md)与[执行任务](../wiki/architecture/Assets/Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中冲突段落待更新，未涉及的架构继续适用；现有序列化与存储能力的保持依据见[源码审查](../wiki/architecture/Assets/Asset-Serialization-Review.md)。

资产文件和运行对象是两种不同的数据。`Asset<T>` 表示已经打开或正在创建的文件内容；`Texture`、`Mesh`、`Shader`、`Material` 表示应用实际使用的对象。运行对象不实现文件根的序列化接口，不保存资产 GUID，也不由加载器建立唯一实例。

## 类型职责

| 类型 | 保存的数据和职责 | 关闭或更新的范围 |
| --- | --- | --- |
| `AssetId<T>` | 一个 `Guid`，以及类型参数提供的调用方类型约束；默认值和 `Guid.Empty` 都表示空编号 | 没有生命周期、引用计数、设备或运行对象 |
| `Asset<T>` | 当前文件的编号、内容版本、名称、磁盘依赖及文件字段、分块目录访问 | 只关闭这一次资产访问 |
| 内部 `AssetFormat<T>` | 编译期确定的运行类型与文件类型关联、文件类型名、后缀、二进制格式、字段编解码和磁盘依赖枚举 | 不创建领域对象，不执行重载 |
| `TextureData`、`MeshData`、`ShaderData`、`MaterialData` 等 | 二进制文件字段；导入器和生成代码使用的纯数据，没有独立的加载、设备或销毁状态 | 字段副本不拥有运行对象 |
| `AssetLoader` | 在一个存储来源内按 GUID 与文件版本共享文件读取，限制并发、队列和字段缓存 | 取消自身读取、清空自身缓存；不关闭借入的存储，也不销毁运行对象 |
| `IAssetStorage` | 查询 `AssetEntry`，打开该条目指定版本的 `IRangeSource` | 存储返回的具体字节范围由调用方关闭 |
| `AssetEntry` | 目录、存储和注册共用的编号、类型、路径、版本、依赖及导入来源信息 | `AssetManifest` 保存条目，并派生路径、源输出和反向依赖索引 |
| `AssetProject` | 工程的导入、创建、保存、注册、索引和诊断 | 不提供运行对象服务；读取统一通过 Loader 和内部格式 |
| `AssetWriter` | 将 `Asset<T>` 的文件内容写入同目录临时文件，完成后原子替换目标 | 不接受运行 `T` |
| `SourceMeta` | 源编号、导入器、设置、版本、输入摘要、`FileDependency` 和各个 `SourceOutput`（仅记录相对公共输入的差异） | 每个源只有一个 `.meta`；输出文件不再有 `.asset.meta` |
| `ImportedAsset` | 一次导入返回的输出记录，用于工程注册 | 不承载运行对象 |

`AssetRef<T>`、资产 GUID 包装、加载上下文、驻留对象表以及旧的类型描述器已移除。没有替代的通用句柄、读取会话或事务体系。

## 打开、创建和释放

```csharp
await using var loader = new AssetLoader(storage);
await using Asset<Texture> file = await loader.OpenAsync(new AssetId<Texture>(guid), ct);
await using Texture first = await Texture.CreateAsync(file, ct);
await using Texture second = await Texture.CreateAsync(file, ct);
await file.DisposeAsync();
await loader.DisposeAsync();

// 两个 Texture 是不同对象；它们各自取得的具体文件读取范围仍然有效。
using var tile = await first.AcquireMipTileAsync(0, 0, 0, cancellationToken: ct);
```

`OpenAsync` 只读取并验证文件字段及按需使用的目录信息。它不会创建 GPU 资源，不会实例化依赖，也不会调用任何领域对象构造函数。对同一编号连续或并发打开会得到不同的 `Asset<T>`。一个调用方取消、关闭或写出自己的资产访问，不会关闭另一个调用方的访问。

普通外部程序集可以用 `Texture.ReadDescription(file)` 和 `Texture.ReadMipTiles(file)` 查看纹理文件的尺寸、格式与分块目录，不需要引用内部文件类型，也不需要先创建 Texture。目录返回独立字段，修改它不会改变文件内容。描述中的立方体层数采用 Graphics 资源的六面层数约定。

缓存属于具体 Loader 和它借入的存储，因而不会混淆不同存储来源。缓存键包含 GUID 和存储选定的文件版本。松散文件从经过校验的二进制文件头读取内容版本，并在打开实际文件句柄后再次核对；不以长度或修改时间代替版本。替换路径不会使旧文件句柄转向新内容。新版本可以立即独立打开，旧对象无需先被销毁。

底层继续使用 `IRangeSource`、`BinaryDocument<TFields>`、分块调度器、压缩和摘要实现。`BinaryDocument<TFields>.Duplicate()` 建立独立的关闭边界，共享具体文件字节。关闭某一个文档只结束该文档的读取范围，不会结束其他文档的范围。生成基础设施的字段读取返回独立字段副本；取得的文档根应作为只读文件字段使用。

Loader 的关闭会等待它发起的读取结束，并尝试关闭全部缓存文件。某一个字节来源关闭失败不会跳过后续来源；错误在关闭结果中汇总。Loader 始终不调用外部存储的关闭方法。

默认限制为 8 个并发文件读取、1,024 个排队或正在执行的读取、128 个缓存文件，以及 16 MiB 缓存根字段编码字节。没有等待者的旧版本优先退出，随后按最近使用顺序执行文件数和字节预算淘汰。正在交给调用方的文件不会在交接期间被淘汰。第三方存储未提供版本时，可以合并一次正在执行的读取，但不把完成结果永久当作最新文件。显式保留的 Asset 或运行对象仍保留自己的实际字节访问，不受 Loader 缓存淘汰影响。

尚未写入并重新打开的 `Asset<T>` 具有空 `Version`；已打开资产的版本取自二进制文档的内容 generation。存储条目的版本用于选择正确来源，包的版本不会冒充包内资产的内容版本。

`ToAsset` 捕获文件输入，随后修改输入字段或像素数组不会修改已创建的资产。字段读取返回深拷贝；文档的 `Duplicate()` 从原始已验证根字节解码，不能把调用方改过的 Root 传给另一个访问者。忽略字段中的字节数据也需要复制，并保持可空内存“缺失”和“空但存在”的区别。外部格式提供自定义 `CopyFields` 时，同样负责其可变嵌套数据和忽略字段的深拷贝。

写出已打开的资产时，使用有界缓冲区复制原文档并验证分块，保留原类型目录、压缩编码和内容版本，不累积一份所有解压块的列表。新建资产则使用其生成的文件写入器，并在提交前检查实际文件根格式。写入中的取消、损坏内容和格式错误不会替换已有目标文件。一次复制字段与一次编码最终文件是两件事，复制不再反向修改调用方的分块键或长度。

## 运行对象

### Texture

`Texture.CreateAsync` 取得自己的 `BinaryDocument<TextureData>`，验证描述和基础 mip 覆盖，并异步读取最粗 mip。创建完成后可以交给渲染世界。数组层、立方体面、三维纹理深度切片、块压缩及文件声明的行距、切片距都保留。

无文件内容使用 `Texture.Create(TextureDesc, tiles, sampledDimension, sampledFormat)`。它不分配 GPU 资源。使用现有 `Graphics.Texture` 和 `RenderDevice` 为实际设备建立分配，不再引入公开的纹理包装家族。

Texture 保存自己的设备关联；每个设备有独立的分配、可用 mip 和视图缓存。`TryGetTexture` 和 `GetFirstAvailableMip` 查询这些关联。`RenderDevice.PrepareTexture` 只使用已经可用的 CPU 数据，并将上传与消费者连接到现有 RenderGraph；它不做文件读取。原生上传布局由 backend 的 copy footprint 决定。

`Texture.Dispose` 是运行纹理的释放入口。`RenderDevice.ReleaseTexture` 可以单独解除某一设备的关联。已记录帧仍然保护它用到的 GPU 资源，资源直到相应的真实提交完成才释放。设备整体退出也会解除它自己的关联。

### Texture 更新

`Texture.UpdateAsync(newFile, ct)` 先完成文件读取、字段检查和基础 mip 准备。没有设备关联时，内容可以直接在调用方的世界更新边界替换。有设备关联时，新内容等待各设备下一次 `PrepareTexture`，使用新的 GPU 分配上传；旧内容和旧分配在此期间保留。`IsUpdating` 表示内容已经等待渲染更新。

更新任务会等待各设备上传的帧成功提交后完成，因此不要在阻止下一帧执行的位置同步等待它。继续正常的世界/渲染帧执行，在允许的更新边界观察任务结果。读取失败、上传准备失败、帧取消或调用方取消都保留旧内容。已提交帧的资源释放由 RenderGraph 的帧完成与 RenderDevice 的提交跟踪共同保护。

CPU 分块缓存预算与 GPU 分配不是同一个预算。`TextureLoadOptions.DecodedBudgetBytes` 约束解码后的 CPU 数据；文件字段缓存由 Loader 另行限制。GPU 分配和上传使用期属于图形设备与真实提交。

程序生成纹理通过 `Texture.UpdateAsync(TextureDesc, tiles, sampledDimension)` 使用相同更新边界，不需要制造临时磁盘资产或编号。更新中的设备被显式移除时，随之退出等待集合；不能因为等待一个已经退出的设备而使更新任务无法完成。提交后的旧内容清理错误会作为清理错误报告，不能伪装成未发生内容切换。

### Mesh

`Mesh.CreateAsync` 验证文件中的分页摘要、BVH 摘要和布局，然后取得自己的具体负载范围。关闭 Asset、Loader 或包存储不会使已有 Mesh 的读取失效。`ReadPageAsync`、`ReadBvhAsync` 将数据直接写入调用方提供的最终目标，并校验摘要。Cluster 继续使用已有分页、BVH、页缺失合并和最终目标读取路径。

`Mesh.Create(name, vertexEncoding, payload, bvhOffset, bounds)` 支持无文件网格。顶点编码由调用方明确给出；资产和渲染管线均不引入默认顶点属性布局或每帧读取元数据的解释器。`Mesh.UpdateAsync` 先打开并验证新数据，再在调用方的世界更新边界切换内容；既有 Cluster 使用保留其所需的具体旧范围。

### Shader

Shader 保留多后端、入口、阶段、字节码和反射数据。无文件代码可以通过 `Shader.Create(name, programs, reflections, metadata)` 创建。`Shader.CreateAsync(file, target, ct)` 只实例化所选后端的代码；不指定 target 的重载用于明确需要所有已存后端的调用方。`LoadBackendAsync` 可以在 Asset 和 Loader 已关闭后取得另一后端，过程中不会同步回读文件。渲染链接只使用已经读取的代码。

`Shader.UpdateAsync` 在新字节码和反射检查通过后替换内容并增加版本；失败保持旧代码。运行 Material 订阅其实际 Shader 的变更。Shader 关闭自身保留的文件读取范围，已经建立的图形管线仍按自己的提交完成边界释放。

### Material

运行 Material 只保存实际 Shader、Texture、具体属性和属性变更编号。部分属性的生成代码直接读取和写入具体字段；标量编码与纹理、采样器读取不经过通用对象数组、装箱值或反射。不同材质可以共享同一个实际 Shader 或 Texture。

磁盘 `MaterialData` 保存材质类型、`AssetId<Shader>`、`AssetId<Material>` 父级、纹理编号及具名参数覆盖。`Material.CreateAsync(file, loader, ct)` 显式解析父级文件并合并覆盖，然后创建实际引用对象；打开文件本身不解析运行依赖。父级循环、未知材质类型和不匹配的参数在实例化时拒绝。纹理参数可以显式覆盖为空编号。不存在独立生命周期的磁盘 MaterialInstance，也不存在从运行 Texture 的字符串 GUID 反查并存盘的路径。

材质只关闭它在实例化磁盘内容时自行创建的依赖；应用直接赋值的纹理、着色器由应用管理。材质更新先准备替换内容，在变更通知之前转移新依赖，避免订阅者抛出异常后替换对象的释放误杀新纹理。所有订阅者和旧资源关闭都会尝试执行，失败统一汇总。通知或提交后清理失败表示内容已经切换，不表示旧内容被恢复。Shader 也在释放读取锁后通知全部订阅者。已关闭的 Mesh 和 Shader 不能通过再次更新复活。

## 场景和启动配置

场景文件的字段类型为 `SceneData`，与现有 ECS `World` 关联。应用取得 `Asset<World>` 后，由现有场景实例化代码向传入的 Game world 创建实体。Render world 仍只接收既有提取流程的数据，没有第三条资产到渲染数据通路。

`ApplicationData` 的文件字段转换成 `ApplicationSettings`，直接用于窗口、世界和渲染器启动。配置解析不会顺便创建所有纹理、网格或着色器。实际 Shader 组合和场景依赖由各自的创建操作取得，并由创建它们的领域组合明确释放。

## 创建文件与扩展格式

生成器处理文件字段上的关联：

```csharp
public sealed class Terrain
{
    public Terrain(string name) => Name = name;
    public string Name { get; }
    public static Terrain Create(Asset<Terrain> asset)
        => new(asset.GetFieldsGenerated<TerrainFields>().Name);
}

[Asset(typeof(Terrain), ".terrain.asset")]
[BinaryContract]
public sealed partial class TerrainFields
{
    public string? AssetGuid { get; set; }
    public string Name { get; set; } = "";
}

using Asset<Terrain> file = new TerrainFields { Name = "island" }.ToAsset();
Guid id = project.CreateAsset("island.terrain.asset", file);
```

新类型不修改 Loader、Project、存储或中央类型分支。二进制接口只作用于文件类型，运行类型可以有自己的构造参数。带分块的文件类型可以提供 `CreateWriter`；带磁盘引用的类型可以提供 `GetDependencies`。这些生成基础设施方法对下游程序集可用，并以 `EditorBrowsable(Never)` 与普通领域 API 区分。

新建文件可以让 `ToAsset()` 分配编号；替换已有文件时明确沿用它的编号，例如 `fields.ToAsset(existingId)`。`AssetWriter.Write(openedAsset, copyPath)` 保留原文件的字段、类型目录和分块。修改需要重新生成负载的文件时由对应导入器生成完整文件内容，不从运行 Texture、Mesh 或 Material 反向序列化。

## 导入、索引与存储包

`IAssetImporter` 返回 `ImportedAsset` 输出记录。`SourceMeta` 的源编号保持不变，源编号与子资产键的确定性编号算法只位于导入工具。输入文件和设置的摘要决定增量导入；输出记录集中在源 `.meta` 中，输出文件本身可通过格式重新注册到目录。直接创建的资产只依靠文件自身重建索引。

`AssetManifest` 以 `AssetEntry` 为主要数据，保留可重建的路径、磁盘依赖和反向依赖查询。`RegisterAssetAsync<T>` 打开文件并注册字段，不创建 T。Loader 的公开读取选项包括 `ReadLimits`、`MaxConcurrentReads`、`MaxQueuedReads`、`MaxCachedFiles` 和 `MaxCachedRootBytes`；没有 Type 到 object 的选项袋。

`AssetPackStorage` 直接保存最高优先级在前的有序包集合，取代单独的包叠加对象。`AssetPackBuilder(basePack)` 直接写差异包，未变化的文档不会进入输出；同一个构建器支持普通写入和签名写入。`AssetPack`、`AssetPackEntry`、RSA/SHA-256 签名验证和包内文档收据继续使用 Serialization 实现。关闭包存储会关闭它的包访问，但已打开的资产范围通过独立的二进制文档访问保留所需字节。

## 文件格式与重导入

二进制文档信封仍是 `SEBDOC03`；目录、摘要、压缩方式、分块键及原生上传布局保持。Texture、Mesh、Shader、场景和配置的原有二进制类型名通过 `BinaryContract.LogicalName` 保留，不因 C# 文件字段类型更名而改变编码。

材质文件现在使用 **格式 3、schema epoch 2**，加入父级与参数覆盖；旧材质格式不会在运行时回退读取。glTF 导入器版本为 **5**。Shader 的文件字段版本仍为 **8**，Slang 导入器版本由其实现声明。源 `.meta` 保留原源 GUID 和导入设置；缺少新输出记录的旧源需要重新导入，旧 `.asset.meta` 不再读取。

在工程根目录重建内置内容与索引：

```powershell
dotnet run --project tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj -- --reimport
```

单独重导入着色器仍使用 `SomeEngine.AssetCook shader <source.slang> --profile <profile>`。新默认生成列表包含 VSM Hi-Z 初始化和缩减着色器，保留已有高级渲染能力。

## 验证入口

完整解决方案构建和 Assets、下游 Consumer、Render、Render.Cluster、Serialization 测试覆盖打开与对象创建分离、关闭和取消隔离、版本并存、类型与损坏拒绝、第三方格式/存储、分页读取、父级覆盖、签名及补丁。Render 的 Texture 测试使用 D3D12 WARP，覆盖基础 mip、BC、数组、立方体、三维纹理、多设备、上传取消与释放边界。

```powershell
dotnet build SomeEngine.slnx
dotnet test tests/SomeEngine.Assets.Tests/SomeEngine.Assets.Tests.csproj
dotnet test tests/SomeEngine.Assets.Consumer.Tests/SomeEngine.Assets.Consumer.Tests.csproj
dotnet test tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj
dotnet test tests/SomeEngine.Serialization.Tests/SomeEngine.Serialization.Tests.csproj
dotnet publish tools/SomeEngine.Assets.AotSmoke/SomeEngine.Assets.AotSmoke.csproj -c Release -r win-x64
& tools/SomeEngine.Assets.AotSmoke/bin/Release/net10.0/win-x64/publish/SomeEngine.Assets.AotSmoke.exe
```

本次拆分保留现有分块文件、部分纹理上传和底层稀疏资源接口，不把它们描述为已经完成“自动纹理采样反馈 → 稀疏映射 → GPU 淘汰”的完整系统。CPU 解压预算不是 GPU 纹理驻留预算。实际执行结果、测试数量和平台跳过项记录在单独的验证记录中，不以历史测试通过代替当前验证。

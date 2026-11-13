# ShaderAsset、Block 与驻留 Shader

`ShaderAsset` 提供 shader 的资产身份。材质、场景和管线配置保存 `AssetId<ShaderAsset>`；编译目标、入口和特化由同一个资产的 Slang 数据表达。完整规则见 [Shader Block、驻留与 Slang 重构规范](../shader/Shader-Block-Residency-Refactor.md)。

## Block 组成

```text
ShaderAsset
  +-- ShaderSourceBlock           根输入、输入位置、共享资产引用与特化
  +-- 业务自己的 ShaderInputBlock 业务入口 IR
  +-- ShaderBlock                 已生成的目标及逐入口代码
               -> Shader         驻留的 Slang 对象、反射及代码
```

可读 `.shader` 的 `SourceNames` 指定相对于定义文件的根源码。内容构建保存各根输入的 `IModule.Serialize` 结果。公共输入通过独立 shader 资产保存 IR，纯输入资产设为 `SourceOnly = true`，包含入口的资产同样可以被导入；调用者的 `Dependencies` 保存对应资产 GUID。模块的文件输入归它自己的 `SourceBuildRecord`，调用者的构建记录使用 `AssetInputs` 表达资产依赖。

`ShaderInputBlock` 保存 `SourceNames` 与逐份序列化 Slang IR。消费者通过 `[AssetBlock("业务的持久名称")]` 声明具体 partial 类，生成器负责它的复制、静态读取和保存。`ShaderSourceBlock.Inputs` 用 `ShaderInputLocation(BlockTypeId, SourceIndex)` 选择本资产中的实际输入，顺序决定 Slang 输入与入口顺序。`ShaderAsset.SelectInputs<TBlock>()` 选择已经取得的强类型 Block。

## 共享材质与入口

`standard_material.shader`、`unlit_material.shader`、接口和透明内核等公共输入在引擎 `assets/Shaders` 各烘焙一份。应用材质的组合源码选择顶点、曲面和可见性求值类型，通过 Slang `import` 使用公共实现。依赖生成布局和材质类型别名的配置头文件见 [Shader Build](../shader/Shader-Build.md#宏配置头文件)。

用户入口可以分别放在本资产的强类型 Block 中，公共材质实现由共享资产引用取得：

```csharp
[AssetBlock("Game.ForwardMaterialEntries")]
public sealed partial class ForwardMaterialEntriesBlock : ShaderInputBlock;

using var asset = new ShaderAsset { Name = "Material entries" };
asset.GetBlock<ShaderSourceBlock>().Dependencies = [materialInput];
using var forward = new ForwardMaterialEntriesBlock { SourceNames = [entryName] };
forward.SetSerializedSource(0, entryIr.Span);
asset.SetBlock(forward);
asset.SelectInputs<ForwardMaterialEntriesBlock>();
Shader shader = await shaders.CreateAsync(asset, files);
```

`ShaderLibrary` 先取得 `Dependencies`，再从所选 Block 恢复当前资产的 IR，组合并链接入口。共享输入只装入库的 session；公共 IR 的存储和加载分别由它自己的资产与库承担。

`Shader.GetSlangDeclarations()` 提供该 shader 根输入及实际资产依赖的原生声明闭包，按输入名称去重。材质标量、贴花标量和透明求值器从这组声明读取属性，布局由各自已链接 shader 的反射给出。根输入数量、入口顺序与保存的源码数量保留各自含义。保存代码、已经烘焙的特化与组合 shader 共同保留这些原生模块引用。

内置姿势、皮肤包围体、Canvas 与模板缓冲传输 shader 同样通过 `EngineShaderAssets` 中的固定 GUID 引用。对应 `.slang` 与 `.shader` 位于引擎内容目录，feature 使用传入的 `AssetLoader` 和同一个 `ShaderLibrary` 加载。

## 构建与保存代码

项目通过 `AssetProject.ShaderTargets` 选择编译目标，通过 `BuildAllAsync` 按输入资产依赖排列构建。DefaultGame 开发内容选择 DXIL。独立工具和共享内容目录说明见 [Shader Build](../shader/Shader-Build.md)。

已有强类型入口 IR 通过 `shaders.CompileAsync(asset, files)` 生成共同 `ShaderBlock`。`InputVersions` 包含本资产和全部传递依赖的输入版本。每项由资产 `AssetId`、输入的 `BlockTypeId` 和 `SourceIndex` 标识实际 Slang 输入，`DataVersion` 记录该输入所在 Block 的版本；按这组身份去重、排序后保存。共享输入的构建结果也保存自己的完整版本闭包，消费者沿同一构建次序收集这些数据。变体的表达式和入口名保存在来源块，加载时统一完成特化，使用时直接查表。

`ShaderBlock` 保存实际代码和逐入口资源使用数据，Slang 提供入口、资源、类型、布局与属性反射。整目标代码可选；逐入口代码能够单独提供执行代码。加载时，保存代码要求完整输入版本和目标设置共同匹配；同一组 IR 恢复的反射与保存的代码、资源使用数据一起组成 `Shader`。输入版本变化或目标缺少适用代码时，同一 `ShaderLibrary` 从当前 IR 生成匹配的代码和资源使用数据。

当前资产格式为 `ShaderBlock.Version = 13`，`ShaderAsset.Version` 随之变化；`SlangShaderImporter.ImporterVersion = 36`。公共输入与消费者按资产依赖顺序重新构建这些产物。

## 取得与期限

```csharp
await using AssetLoader files = EngineShaderContent.OpenFiles(contentRoot, manifest);
using var shaders = new ShaderLibrary(device.Capabilities.CompileTarget);
Shader shader = await shaders.GetAsync(files, shaderId, cancellationToken);
```

`ShaderLibrary` 按编译目标持有一个 global session、一个 session、已加载输入表和 Shader 强引用表。专用线程执行 Slang 前端调用；文件 I/O 在编译线程之外进行。库先取得当前传递依赖，再将其输入版本与资产自身内容一起用于已有缓存键。每个缓存值共同持有 `Shader` 和这次完整的输入版本集合，多个内容根中相同的资产及依赖内容得到同一对象。

一次公共加载、创建或编译调用按资产身份共用本次依赖解析的任务表。多个分支引用相同输入时，共用同一个解析任务；每个节点的资产打开、Block 解码、缓存键计算和模块就绪检查执行一次。任务表随本次解析结束释放，持久 Shader 缓存继续负责跨调用共享。

Runtime 的 `ApplicationShaders` 在进程范围按目标主持有这些库。每次 `Application.RunAsync` 与场景切换取得同一目标库，`ApplicationRenderer` 保留消费引用；应用退出释放其 renderer、内容访问与 display，进程退出统一关闭库。工具与独立调用方通过普通 `ShaderLibrary` 构造和 Dispose 管理自己的库。

命中 Shader 缓存后，库通过当前 session 的输入表确认模块就绪并直接返回对象。刷新 session 后，缺失模块的依赖恢复按 Shader key 共享一次加载。依赖读取和资产释放在 shader 表的锁外完成；文件存储的同步版本查询直接读取固定文件头。

仅更新传递的 `SourceOnly` 资产也会改变消费者的缓存选择。新请求取得与当前依赖配对的完整 `Shader`，既有读者继续持有原来的对象和代码。文件监视器的替换同时采用新缓存值中的 Shader 与输入版本。

活动输入条目共同持有原生输入、声明来源与资产 `DataVersion`。同名输入版本变化时，Slang 线程在同一请求中刷新 session 并恢复该 shader 的实际依赖闭包与根 IR；普通入口和每个特化编译均携带这组上下文。同内容缓存命中继续使用原 Shader 与已驻留输入。

`Shader` 通过普通引用保留 Slang 对象、反射、blob 和托管代码数组。Asset 与文件访问结束后，持有该 Shader 的材质和 pipeline 继续使用已驻留数据。pipeline 创建消费已有 Shader；draw/dispatch 消费创建时派生的管线和绑定数据。

`EngineShaderContent.OpenFiles` 将应用目录与 `content-directories.json` 声明的引擎目录组合为多存储加载器。材质通过 `MaterialAsset.CreateAsync` 或 `UpdateAsync` 使用已取得的 Shader 引用；具体材质类型解释自己的资源和标量字段。

## 更新

开发构建的文件监视器根据来源文件与资产依赖构建受影响 shader，通过同一个库取得替代对象。帧边界的 `ReplaceShader` 更新材质和受影响的 pipeline；历史、实例与其它 feature 资源继续由各自持有者维护。

实现入口：[ShaderAsset](../../../src/SomeEngine.Assets/Schema/ShaderAsset.cs)、[ShaderInputBlock](../../../src/SomeEngine.Assets/Schema/ShaderInputBlock.cs)、[ShaderSourceBlock](../../../src/SomeEngine.Assets/Schema/ShaderSourceBlock.cs)、[ShaderBlock](../../../src/SomeEngine.Assets/Schema/ShaderBlock.cs)、[ShaderLibrary](../../../src/SomeEngine.Resources/ShaderLibrary.cs)、[Shader](../../../src/SomeEngine.Resources/Shader.cs)。

[ShaderDefinitionTests](../../../tests/SomeEngine.Assets.Tests/Assets/ShaderDefinitionTests.cs) 通过公共加载与编译入口验证传递输入变化、旧读者保留、重复请求共享，以及按入口名取得的代码和实际使用资源名称的一致性。[ShaderFileTests](../../../tests/SomeEngine.Assets.Tests/Assets/ShaderFileTests.cs) 验证从保存 IR 生成所选目标及其特化变体。

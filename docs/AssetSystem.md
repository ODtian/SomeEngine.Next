# Asset 系统

2026-09-30。当前规则与职责见 [架构导航](../wiki/architecture/Asset-Files.md)、[对象与 Block](../wiki/architecture/Assets/Asset-Objects.md)、[导入与工程存储](../wiki/architecture/Assets/Asset-Import-and-Storage.md)。[执行任务](../wiki/architecture/Assets/Asset-Rebuild-Tasks.md) 保存各轮范围，本次托管数据与共享结果见 [2026-09-30 验证](../wiki/architecture/Assets/Asset-Verification-2026-09-30.md)；具名修复及验收未关闭项见 [审查及后续状态](../reviews/AssetSpecReview-2026-09-29.md)。

## 身份与强类型数据

Asset 保存身份、引用和 Block 索引；实际数据在独立的强类型 Block 中。TextureAsset、MeshAsset、MaterialAsset、ShaderAsset、WorldAsset 不与某一种使用对象一一绑定，也不从使用对象生成一套字段镜像。

同一个 MeshAsset 可以包含 MeshBlock、MeshMaterialsBlock、ClusterMeshBlock 和后续扩展的其他表示。Scene 保存 AssetId<MeshAsset>，不因管线增加一个 Block 而改变语义引用。

Block 持久类型 ID 建立直接索引。ContainsBlock<T>() 不读取负载，GetBlock<T>() 不隐式执行磁盘 I/O，GetBlockAsync<T>() 显式取得所需内容。Block 自己选择文本、二进制、外部引用或延迟表示；Asset 不分文本资产与二进制资产。

## 取得与使用

```csharp
using TextureAsset asset = await TextureAsset.OpenAsync(path);
TextureBlock pixels = await asset.GetBlockAsync<TextureBlock>();
using SomeEngine.Resources.Texture texture = await pixels.CreateAsync(asset.Name);
```

打开 Asset 不创建 GPU 资源，也不保证像素或几何负载全部驻留。显式转换负责取得当前需要的数据；转换后的使用对象保留自己的必要内容，不通过 Asset 的异步访问模型执行普通业务访问。未来 mip 或几何页仍经显式流送取得，再更新可用状态。

Block 使用普通字段、集合与不可变托管数组。业务 tile、mip、LOD 和几何页决定取得粒度；所有字节语义共用整数 LRU 缓存、CPU 字节预算和实际读取任务表，规则见 [数据共享](../wiki/architecture/Assets/Asset-Sharing-and-Writes.md)。Duplicate 保留身份，复制需要独立编辑的字段与集合结构，共享不变数据；CreateCopy 创建新身份。普通引用保活数据，不使用通用 CPU 页、页级 COW 或每文件解码缓存。关闭不是保存。

## 保存、目录与来源

SaveAsync 写出现有二进制文档并返回 AssetEntry。只有显式登记后目录才变化；AssetProject 统一完成来源构建、保存和登记。目录只保存 Library/AssetManifest/catalog.json，来源、输出与直接依赖一起切换，不回读三份旧索引。

人工原生定义保存自己的身份和强类型 Block。外部来源的 `.meta` 保存 Source ID、Importer 选择、强类型参数和必须保留的元素 ID 映射；`.import.json` 是可丢弃的构建记录。内容版本、文件路径、来源 ID 与资产 ID 不互相代替。缓存命中必须核验实际输入、输出内容及文档版本。

Loader 与存储按已选条目取得字节范围。AssetLoader 的 `GetShaderAsync`、`GetTextureAsync` 按资产身份共享实际依赖，Material 的具体类型与使用期由业务控制。完整性、认证、压缩、包、差分和范围读取由原有通用实现提供。详见 [文件读写](../wiki/architecture/Assets/Asset-File-IO.md)、[引用和版本](../wiki/architecture/Assets/Asset-Identity-and-Dependencies.md)。

## 扩展导入

扩展实现 IAssetImporter<TSource,TSettings>，直接接收原生来源库对象。多个 Importer 可向同一 Asset 写不同 Block。每个 Block 在同次结果中只能有一个生产者；冲突由配置或明确检查解决，不以执行顺序覆盖。Context 合并遍历与依赖调用，处理循环、取消和结果释放。

项目引用决定能力，SG 为 Importer、Importer 能力描述和导入组合生成 Microsoft DI 注册。Importer 使用无参构造，实际来源和设置由导入上下文提供；其余资产操作通过对象方法和静态编码完成。来源配置与本次覆盖分别生效。标准 glTF 集成位于引擎 `SomeEngine.Assets.Importers`；外部扩展示例位于 `samples/SomeEngine.Curves.Assets`。

## 项目模板与工具

独立项目通过 `templates/DefaultProject/Install.ps1` 安装到新目录。一个游戏项目编译游戏本体与所选开发能力。项目维护自己的代码、顶层 Slang 和 Shader 定义；引擎提供标准材质、导入和资产构建能力。引擎 Shader 库以明确 include 路径提供算法，应用读取自己输出目录中的目录文件及资产。配置与 API 见 [游戏开发组合](Game-Development.md)。

```powershell
# 在引擎检出目录内生成统一渲染样例内容。
dotnet run --project samples/DefaultGame/DefaultGame.csproj -- sample

# 游戏的开发组合调用引擎资产命令。
dotnet run --project <project-directory>/Game.csproj -- prepare-materials
dotnet run --project <project-directory>/Game.csproj -- import assets/Models/character.glb
```

`GenerateDefaultAssets` 调用 `samples/DefaultGame` 的内容生成实现。[Shader 来源](ShaderSources.md)、[可读源格式](AssetSources.md)、[资产字节与业务分块](AssetPages.md) 和 [通用编码](BinarySerialization.md) 说明各自边界。

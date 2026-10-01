# 独立 Shader 定义与运行时编译

`.shader` 是 `ShaderAsset` 的人类可编辑来源。它拥有独立 ID，并在同一个语义资产中声明 `ShaderSourceBlock`；内容构建还会产生 `ShaderBlock`。不从同名 `.slang` 推导资产身份，也不生成重复的 `.slang.meta`。

架构规则见 [Shader Block、驻留与 Slang 重构规范](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)。当前实现及验收缺口见 [2026-09-29 审查](../reviews/AssetSpecReview-2026-09-29.md)。

## 来源格式

当前 `.shader` 的 Shader 数据只声明 `SourceNames`。以下只展示该字段；完整文件还包含身份、类型、schema 和 Block 描述，格式见 [可读资产源文件](AssetSources.md)：

```json
{
  "Blocks": [
    {
      "Fields": {
        "SourceNames": ["surface.slang"]
      }
    }
  ]
}
```

名称相对于定义文件。Slang 自己解析 include/import、入口、阶段、泛型和属性；引擎不保存入口选择、阶段、target 或反射副本。一个定义可以列出多份根模块，所有由 Slang 声明的入口按 Slang 顺序组成。

target/profile 与特化属于一次具体编译调用：调用方直接提供 Slang `SessionDesc`、`TargetDesc` 和 `SpecializationArg`。这些值不以引擎枚举或字符串配置写进 `Shader`。

## 内容构建

`ShaderSourceFormat` 通过 `AssetProject.BuildAsync` 调用 `SlangShaderImporter`。构建行为：

1. 用 Slang 加载每份根源码；
2. 让 Slang 处理实际 include/import；
3. 对每个根 `IModule.Serialize`，把 Slang 不透明 IR 保存到 `ShaderSourceBlock`；
4. compose/link 所有 `IEntryPoint`；
5. 保存可取得的整 target 代码和每个入口代码到 `ShaderBlock`；
6. 写入实际依赖、内容摘要和 importer 版本。

二进制 `ShaderSourceBlock` 因此包含用户机器可取得的 Slang 输入，不依赖构建机器的源码路径。`ShaderBlock` 只保存代码 chunk，不保存入口、资源、类型、布局或属性的引擎副本。

整 target blob 是可选的；Slang 对多入口组合不提供整 blob 时，逐入口代码仍是有效产物。

## 用户机器编译

用户机器上的显式编译通过 ShaderSourceBlock 完成。CreateAsync 使用已有 Slang 数据或调用方搜索路径中的源码；CompileAsync 返回 Shader、序列化 Slang 数据和实际读取文件的摘要。调用已有 Slang 数据的示例：

```csharp
await using ShaderAsset asset = await ShaderAsset.OpenAsync(loader, shaderId, cancellationToken);
ShaderSourceBlock source = await asset.GetBlockAsync<ShaderSourceBlock>(cancellationToken);
Shader shader = await source.CreateAsync(
    slangGlobal,
    sessionDescription,
    asset.Name,
    specializations,
    cancellationToken);
```

`CreateAsync` 异步取得 IR，在新 `ISession` 中恢复 `IModule`，取得 `IEntryPoint`，compose/specialize/link/codegen，并返回驻留完成的 `Shader`。`Shader` 直接持有必要 Slang 接口、反射句柄和生成的 `ISlangBlob`，不为统一形式把 blob 复制进页。它不实现 `IDisposable`，调用方通过普通 GC 引用保持使用期限。

如果保存代码适用于本次 Slang 设置，可由 `ShaderBlock.CreateAsync` 取得：

```csharp
ShaderBlock code = await asset.GetBlockAsync<ShaderBlock>(cancellationToken);
Shader shader = await code.CreateAsync(
    source,
    slangGlobal,
    sessionDescription,
    asset.Name,
    specializations,
    cancellationToken);
```

这条路径恢复相同的直接 Slang 对象，并将所需代码交给最终 `byte[]`，由 `Shader` 通过普通引用持有。当前实现仍先取得页，再复制到数组；总规范要求收敛到最终存储的取得边界，见审查 I11。Asset 和文件随后可以释放，Shader 不持有页保留，也不承担页的释放责任。

内部 `ShaderRef.LoadAsync` 与公共材质/管线配置创建 API 也要求调用边界显式提供 Slang 参数或 Shader 加载行为。`AssetLoader` 不推断设备 target。材质 UpdateAsync 同样接收显式 Slang 参数或 Shader、Texture 加载行为，完整取得替代内容后在既有更新边界更新 Material。

## 增量构建与失败

构建记录位于完整来源名追加 `.import.json`，是可丢弃的派生数据。定义内容、实际依赖、Slang 编译设置、编译器版本、Block 编码和输出摘要共同决定复用；只相信记录文件不足以复用。

未读取的其他源码变化不会触发构建。定义、实际 include/import、编译器或 target 设置变化会重建同一 ID。缺失或损坏产物只在登记身份与路径一致时修复，不能覆盖另一有效资产。

编译失败、输入在编译期间变化或保存失败时，旧成功产物和目录继续有效。旧 Shader schema 没有兼容 reader；升级后必须重新构建 `.shader.asset`。

## 显式工程和库路径

项目根目录与 include 路径由调用方提供。默认局部构建以定义所在目录为根，不向父目录搜索工程，也不以进程当前目录猜引擎库。

```powershell
dotnet run --project tools/SomeEngine.AssetCook/SomeEngine.AssetCook.csproj -c Release -- shader assets/Shaders/hello_triangle.shader --project <project-directory> --include <engine-directory>/assets/Shaders
```

`AssetCook` 使用正常 `AssetProject`/Block 行为，并在保存后重新打开资产、恢复直接 Slang 数据和检查每个 target 的入口数。

## 模板边界

Standard、Unlit 的具体 C#、顶层 Slang 和 `.shader` 随默认项目模板分发；创建项目后由用户维护。引擎只提供共享 Shader 算法与接口库，不补回用户删除的来源，也不静默覆盖修改。

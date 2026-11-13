# 独立 Shader 定义与运行时编译

`.shader` 是 `ShaderAsset` 的人类可编辑来源。它拥有独立 ID，并在同一个语义资产中声明 `ShaderSourceBlock`；内容构建还会产生 `ShaderBlock`。不从同名 `.slang` 推导资产身份，也不生成重复的 `.slang.meta`。

架构规则见 [Shader Block、驻留与 Slang 重构规范](../wiki/architecture/shader/Shader-Block-Residency-Refactor.md)。当前实现及验收缺口见 [2026-09-29 审查](../reviews/AssetSpecReview-2026-09-29.md)。

## 来源格式

当前 `.shader` 的 Shader 数据声明 `SourceNames` 和 `Inputs`。以下展示单个源码 Block 的默认输入；完整文件还包含身份、类型、schema 和 Block 描述，格式见 [可读资产源文件](AssetSources.md)：

```json
{
  "Blocks": [
    {
      "Fields": {
        "SourceNames": ["surface.slang"],
        "Inputs": []
      }
    }
  ]
}
```

名称相对于定义文件。Slang 自己解析 include/import、入口、阶段、泛型和属性；引擎不保存入口选择、阶段、target 或反射副本。一个定义可以列出多份根模块，所有由 Slang 声明的入口按 Slang 顺序组成。

target/profile 与特化属于一次具体编译调用：调用方直接提供 Slang `SessionDesc`、`TargetDesc` 和 `SpecializationArg`。这些值不以引擎枚举或字符串配置写进 `Shader`。

## 内容构建

`AssetProject.BuildAsync` 直接打开 Shader 定义并调用 `SlangShaderImporter`，编译搜索路径通过 `shaderIncludeDirectories` 提供。构建行为：

1. 用 Slang 加载每份根源码；
2. 让 Slang 处理实际 include/import；
3. 对每个根 `IModule.Serialize`，把 Slang 不透明 IR 保存到 `ShaderSourceBlock`；
4. compose/link 所有 `IEntryPoint`；
5. 保存可取得的整 target 代码和每个入口代码到 `ShaderBlock`；
6. 写入实际依赖、内容摘要和 importer 版本。

`SlangShaderImporter` 通过 `ShaderAsset.CompileAsync` 完成上述编译与代码保存。预先准备好的强类型入口 IR 也使用这个公开入口生成共同代码；完整输入版本、保存代码与反射的配对规则见 [Shader 资产](../wiki/architecture/Assets/Asset-Shaders.md#构建与保存代码)。

二进制 `ShaderSourceBlock` 因此包含用户机器可取得的 Slang 输入，不依赖构建机器的源码路径。`ShaderBlock` 只保存代码 chunk，不保存入口、资源、类型、布局或属性的引擎副本。

整 target blob 是可选的；Slang 对多入口组合不提供整 blob 时，逐入口代码仍是有效产物。

## 用户机器编译

运行时通过 `ShaderLibrary.GetAsync(files, id)` 取得共同 Shader。库按编译目标持有 Slang global session、session、已加载输入和 Shader 强引用表；专用线程完成 Slang 操作，文件读取在该线程之外执行。强类型入口、共享资产引用和保存代码配对见 [Shader 资产](../wiki/architecture/Assets/Asset-Shaders.md)。

```csharp
await using AssetLoader files = EngineShaderContent.OpenFiles(contentDirectory, manifest);
using var shaders = new ShaderLibrary(device.Capabilities.CompileTarget);
Shader shader = await shaders.GetAsync(files, shaderId, cancellationToken);
```

来源中的 `Dependencies` 指定先加载的公共输入资产。库从各资产的 IR 恢复 Slang 输入，完成 compose、specialize 与 link，并取得匹配目标的代码。`SourceOnly` 输入只装入共享 session。保存的特化变体随资产加载完成，后续直接取得已链接变体。

`Shader` 直接持有 Slang 对象、反射句柄、blob 与最终托管代码数组。Asset 和文件访问结束后，Shader 的普通引用继续维持这些数据的期限。构建已有的入口 IR 使用 `shaders.CompileAsync(asset, files)`；程序生成的源码使用同一个库的创建入口。

引擎的公共 shader 在 `assets/Shaders` 构建一份，应用使用 GUID 和多个内容存储根引用。内置姿势、皮肤包围体、Canvas 和模板缓冲传输同样采用资产路径。`tools/BuildContent` 执行引擎或应用内容构建，宏配置清单见 [Shader Build](../wiki/architecture/shader/Shader-Build.md)。

## 增量构建与失败

构建记录位于完整来源名追加 `.import.json`，是可丢弃的派生数据。定义内容、实际依赖、Slang 编译设置、编译器版本、Block 编码和输出摘要共同决定复用；只相信记录文件不足以复用。

未读取的其他源码变化不会触发构建。定义、实际 include/import、编译器或 target 设置变化会重建同一 ID。缺失或损坏产物只在登记身份与路径一致时修复，不能覆盖另一有效资产。

编译失败、输入在编译期间变化或保存失败时，旧成功产物和目录继续有效。旧 Shader schema 没有兼容 reader；升级后必须重新构建 `.shader.asset`。

## 显式工程和库路径

项目根目录与 include 路径由调用方提供。默认局部构建以定义所在目录为根，不向父目录搜索工程，也不以进程当前目录猜引擎库。

```powershell
dotnet run --project <project-directory>/Game.csproj -- prepare-materials
dotnet run --project <project-directory>/Game.csproj -- build assets/Shaders/standard.shader.asset
```

游戏的 `prepare-materials` 通过强类型 Block 选择本项目的 Standard、Unlit 与 Cluster IR，调用 `CompileAsync` 保存共同代码及实际模板。通用 `AssetCommands build` 对 `.shader` 调用 `AssetProject.BuildAsync`，对 `.shader.asset` 编译资产中已经选择的 IR 集合并保存结果。编辑器使用同一公开资产入口。

## 模板边界

Standard、Unlit 的具体 C# 和共享 Slang 实现由引擎提供。标准材质接口、实现及 Cluster 入口以序列化 IR 供资产准备使用；业务通过强类型入口 Block 选择自己的管线入口。顶层 Slang 和 `.shader` 继续服务项目的源码编译。具体管线的类型、资源、布局与执行依赖由对应入口及实现持有，项目自行组织材质与导入扩展。

游戏本体、编辑器与 CLI 通过游戏项目编译，共用引擎资产 API。编译组合和开发资产操作的边界见 [游戏项目编译与开发资产](../wiki/architecture/Assets/Project-Development.md)。

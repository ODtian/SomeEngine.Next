# 独立 Shader 定义

2026-09-16。`.shader` 使用 [AssetText 源格式](AssetSources.md)，在 ShaderAsset 内保存 ShaderSourceBlock；编译输出保存在 ShaderBlock。定义拥有自己的 ID，不从同名 Slang 文件推导，不生成重复的 `.shader.meta` 或 `.slang.meta`。

## Slang 来源

ShaderSourceBlock 保存 SourceFiles、Specializations、CookProfile，以及构建时写入 Block chunk 的 Slang 序列化单元。SourceFiles 相对于定义文件，可包含多份源码。Slang 的 import、include、入口和阶段声明是唯一权威。

所有由 Slang 声明的入口都会进入组合；引擎不再保存入口选择 record。Specializations 按 Slang 参数顺序提供原生特化表达式，不创建另一种组合语言。CookProfile 只选择内容构建使用的 Slang 配置。用户机器编译所需目标由调用方直接通过 Slang `SessionDesc.Targets` 提供，不写入引擎目标枚举。

```csharp
using var definition = new ShaderAsset { Name = "Surface" };
ShaderSourceBlock inputs = definition.GetBlock<ShaderSourceBlock>();
inputs.SourceFiles = ["Geometry.slang", "Lighting.slang"];
inputs.CookProfile = "d3d12-sm6.6-bindless";
await definition.SaveSourceAsync(Path.Combine(projectDirectory, "Surface.shader"));

var project = new AssetProject(projectDirectory, [],
    sourceFormats: [new ShaderSourceFormat(["../EngineShaderLibrary"])]);
Guid id = await project.BuildAsync("Surface.shader");
```

调用代码需要 `SomeEngine.Assets`、`SomeEngine.Assets.Schema`、`SomeEngine.Assets.Importers` 命名空间。IAssetSourceFormat 表达来源格式能力，AssetManifest 记录具体实例；SG 不生成这里的来源编译流程。

## 输入、增量构建和失败

AssetProject.BuildAsync 通过 ShaderSourceFormat 核验定义、实际传递依赖、编译选项、目标、编译器版本、Block 编码版本及已有输出文件。只相信缓存记录不足以复用：还要检查实际输出摘要和文档内容版本。

不变输入与未被读取的其他源码不会触发重新编译。定义名称、实际 include、Slang 入口、特化或 CookProfile 变化会重新构建同一 ID。缺失或损坏产物仅在已登记身份和路径相符时允许修复，不能覆盖另一份有效资产。相同内容恢复可以保持原内容版本，不把版本当每次构建递增的计数。

构建记录保存在完整来源名追加 `.import.json`，是可丢弃的派生数据；人工 ID 与来源仍在 `.shader`。输出和记录保存完成后才切换目录；编译失败、输入中途变化或文件保存失败会保留旧的有效产物与目录。多个独立文件的进程内回退不承诺断电时整体原子性。

需要直接编译而不登记文件的代码使用 SlangShaderImporter.CompileDefinition。旧的单个 Slang 文件隐式导入与独立缓存入口已删除。

## 显式工程和库路径

项目根目录与 include 路径由调用方提供。默认局部编译以定义所在目录为根，不向父目录寻找工程文件，也不使用进程当前目录推断引擎库。

`SlangShaderImporter.BuildAsync`、`CompileDefinition` 与 `GetDefinitionInputs` 均接受可选 `projectRoot`，含义一致。调用方选择工程目录后，构建与后续输入核验应传入同一目录；省略则记录定义目录相对的依赖，不因为上级存在 `Directory.Build.props` 改变行为。

依赖由实际 Slang 编译读取获得，不用正则模拟语言。相同卷中的项目外文件使用完整相对路径而不是只记文件名；跨卷路径按平台路径规则保留完整位置。库位置和解析顺序参与构建键，换机器或改变库配置后按新位置核验并重建。

```powershell
dotnet run --project tools/SomeEngine.AssetCook/SomeEngine.AssetCook.csproj -c Release -- shader assets/Shaders/hello_triangle.shader --project <project-directory> --include <engine-directory>/assets/Shaders
```

AssetCook 在构建后通过存储打开资产并创建 Shader，报告 ID、配置、后端和存储读取结果。它不扫描仓库猜测默认项目。

## 模板边界

Standard、Unlit 的具体 C#、顶层 Slang 和 Shader 定义随项目模板分发；创建后由用户维护。引擎只提供原有可复用 Shader 算法和入口库。构建不会补回被用户删除的来源，也不会静默覆盖修改。

项目构建从自己的源码生成 63 项默认资产；来源改动与删除衍生记录后重建仍保持 Shader ID 和材质引用。原有六种渲染场景以及独立目录运行结果见 [当前验证](../wiki/architecture/Assets/Asset-Verification-2026-09-16.md)。

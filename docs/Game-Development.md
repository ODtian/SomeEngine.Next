# 游戏项目开发组合

游戏项目编译自己的代码、引擎运行能力和所选开发能力。CLI 命令、编辑器资产操作和游戏启动共用这次编译得到的具体类型与 SG 注册。NativeAOT 发布会统一编译这些静态引用。

## 构建配置

游戏的 `Directory.Build.props` 在引擎基础配置之后导入 `build/SomeEngine.Game.props`；`Directory.Build.targets` 导入引擎基础目标和 `build/SomeEngine.Game.targets`。这样游戏应用的 `.csproj` 只需保存项目自身配置。

```xml
<!-- Directory.Build.props；SomeEnginePath 指向引擎目录 -->
<Project>
  <PropertyGroup>
    <SomeEnginePath>../../</SomeEnginePath>
    <SomeEnginePath>$([System.IO.Path]::GetFullPath('$(SomeEnginePath)', '$(MSBuildThisFileDirectory)'))</SomeEnginePath>
  </PropertyGroup>
  <Import Project="$(SomeEnginePath)/Directory.Build.props" />
  <Import Project="$(SomeEnginePath)/build/SomeEngine.Game.props" />
</Project>
```

```xml
<!-- Directory.Build.targets -->
<Project>
  <Import Project="$(SomeEnginePath)/Directory.Build.targets" />
  <Import Project="$(SomeEnginePath)/build/SomeEngine.Game.targets" />
</Project>
```

`SomeEngineDevelopment` 选择开发组合。Debug 默认使用 `true`，Release 默认使用 `false`。两种组合分别使用 `bin/development`、`obj/development` 和 `bin/game`、`obj/game`，各自维护构建输出和还原结果。

引擎运行库与生成器随游戏项目引用。开发组合增加 `SomeEngine.Assets.Importers` 引用及 `SOMEENGINE_DEVELOPMENT` 编译常量。项目自定义导入代码放在项目自行选择的位置；开发专用源码可用此常量控制编译。

Release 默认启用 `PublishAot`。开发组合和编译方式分别选择，NativeAOT 开发构建使用：

```powershell
dotnet publish Game.csproj -c Release -p:SomeEngineDevelopment=true
```

发布游戏使用：

```powershell
dotnet publish Game.csproj -c Release
```

`PublishAot` 支持显式覆盖，例如 `-p:PublishAot=false` 用于托管构建。`SomeEngineProjectRoot` 默认取游戏 `.csproj` 的目录，项目可在导入 `.props` 前设置自己的资产根目录。

## 资产命令与编辑器调用

开发构建生成游戏 `RootNamespace` 下的 `ProjectPaths`，其中 `Root` 是项目资产根目录，`EngineShaders` 是引擎 Shader 库目录。路径在游戏编译时确定。

游戏声明自己的能力注册入口，SG 发现引用程序集及项目里的具体导入器，生成注册代码和选择用的 enum。创建资产项目时传入这份注册：

```csharp
[AssetCapabilities]
public static partial class GameAssetCapabilities;

AssetProject assets = AssetProjects.Create(
    ProjectPaths.Root,
    [ProjectPaths.EngineShaders],
    services => GameAssetCapabilities.AddAssetCapabilities(services),
    GameAssetCapabilities.BuildVersion);

int result = await AssetCommands.RunAsync(assets, args, Console.Out);
```

`AssetCommands.RunAsync` 支持以下命令，来源路径相对于资产项目根目录解析：

| 命令 | 行为 | 输出 |
| --- | --- | --- |
| `import <source>...` | 使用项目选择的来源导入器处理每个来源 | 每行一个导入资产 GUID |
| `build <definition>...` | 使用对应来源格式构建每个引擎资产定义 | 每行一个构建资产 GUID |
| `list [asset-type]` | 列出已登记的资产，可按资产类型过滤 | 每行用制表符分隔 GUID、资产类型、相对路径 |

成功返回 `0`，命令用法错误返回 `2` 并输出用法。资产操作的异常和取消通过调用方已有的错误处理路径传递。

编辑器资产操作直接使用同一个 `AssetProject`：

```csharp
await assets.ImportAsync(selectedSource, cancellationToken);
await assets.BuildAsync(selectedDefinition, cancellationToken: cancellationToken);
IReadOnlyList<AssetEntry> items = assets.List();
```

依赖追踪、缓存检查、输出版本和资产登记由 `AssetProject` 维护。游戏运行通过 `AssetLoader` 及具体资产的加载 API 打开已处理资产。正式编辑器控件与状态组织继续使用 [SomeUI 规格](SomeUI-Requirements.md)。

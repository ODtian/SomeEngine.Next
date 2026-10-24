# Default project

模板以一个 `Game.csproj` 编译游戏。引擎提供标准材质、glTF 导入和资产构建；项目维护 Shader 定义、组合入口以及实际游戏内容。

```powershell
pwsh -File <engine>/templates/DefaultProject/Install.ps1 -Destination <project> -EnginePath <engine>
dotnet run --project <project>/Game.csproj -- build assets/Shaders/standard.shader assets/Shaders/unlit.shader
dotnet run --project <project>/Game.csproj -- import assets/Models/character.glb
dotnet run --project <project>/Game.csproj -- list
dotnet publish <project>/Game.csproj -c Release -o <project>/artifacts/Game
```

Debug 默认编译开发能力。`import`、`build` 与 `list` 由引擎处理，使用游戏 SG 生成的导入器注册与选择类型。编辑器资产操作直接使用同一 `AssetProject`。开发者添加定制实现时自行选择代码目录和程序集组织。

Release 默认采用 NativeAOT。游戏启动通过现有 Application API 读取已处理资产和游戏自己的场景、应用配置。需要 NativeAOT 开发构建时，设置 `-p:SomeEngineDevelopment=true`；开发与游戏构建使用各自的输出目录。

`StandardMaterial`、`UnlitMaterial`、配套资产类型与共享 Slang 实现由引擎提供。`assets/Shaders/standard.slang` 和 `unlit.slang` 是项目组合入口，通过 include 选择通用阶段实现和管线入口。具体管线的资源、视图、布局和执行依赖收在对应入口及实现内。材质经阶段输入取得求值数据。

`EnginePath.props` 记录引擎路径；开发构建将项目目录与引擎 Shader 目录编译为路径常量。Shader 编译使用项目来源和明确的引擎 include 库。引擎升级维护共享实现，项目继续维护自己的来源。

正式编辑器界面按 SomeUI 规格建设，并在游戏项目的开发组合中编译。渲染样例和统一场景验收位于引擎的 `samples/DefaultGame`。
# SomeGame

SomeGame 是本仓库中的第三人称建造与模拟经营游戏。首期范围见 [游戏目标](docs/Game-Goals.md)，术语见 [GLOSSARY.md](GLOSSARY.md)。

`Game.csproj` 是游戏、编辑器与 CLI 的编译组合入口，`Game.slnx` 包含该游戏项目。引擎路径由 `EnginePath.props` 配置，通用实现位于仓库根目录。

| 内容 | 归属 |
| --- | --- |
| 游戏启动、玩法、项目扩展 | 游戏项目 |
| Shader 定义与顶层组合入口 | `assets/Shaders` |
| 标准材质、glTF 导入、资产构建 | 引擎公开实现 |
| 编辑器资产操作与 CLI | 游戏开发构建中的引擎开发能力 |
| 游戏目标与设计 | `docs` |

在本目录执行：

```powershell
dotnet run --project Game.csproj -- build assets/Shaders/standard.shader assets/Shaders/unlit.shader
dotnet run --project Game.csproj -- list
dotnet publish Game.csproj -c Release -o artifacts/Game
```

Debug 默认编译开发资产命令和 SG 发现的项目扩展。Release 默认编译 NativeAOT 游戏；`-p:SomeEngineDevelopment=true` 可以生成 NativeAOT 开发构建。自定义材质与导入器按实际需求添加，组织位置由游戏决定。

游戏启动读取自己的已处理资产、场景与应用配置。游戏内容按游戏目标建设；引擎统一渲染样例位于 `samples/DefaultGame`，验收入口为根级 `tools/Measure-Scenes.ps1`。
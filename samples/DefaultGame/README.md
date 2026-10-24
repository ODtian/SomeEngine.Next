# DefaultGame

DefaultGame 是引擎的统一渲染样例。1024 小球场景、Sponza、Amazon Bistro 的导入配置、测试面板和内容生成代码由本目录持有。

游戏项目编译引擎运行能力；开发构建同时编译 `Content` 中的样例资产生成与自定义导入。标准材质、标准导入和 Shader 构建使用引擎实现。

项目 Shader 定义和顶层组合入口位于 `assets/Shaders`。各管线入口封装自己的资源和执行依赖，材质使用阶段输入完成求值。

统一性能验收从根级 `tools/Measure-Scenes.ps1` 进入。

```powershell
# 开发组合生成样例内容。
dotnet run --project samples/DefaultGame/DefaultGame.csproj -- sample

# NativeAOT 游戏组合发布到统一场景工具使用的目录。
dotnet publish samples/DefaultGame/DefaultGame.csproj -c Release -o samples/DefaultGame/bin/game/Release/net10.0

./samples/ImportedScenes/build_scenes.ps1
./tools/Measure-Scenes.ps1
```

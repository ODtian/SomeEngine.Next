# 游戏项目编译与标准资产验收

2026-10-13，集成分支 `codex/game-project-composition`。来源规格 [#204](https://github.com/ODtian/SomeEngine.Next/issues/204)，任务 [#205](https://github.com/ODtian/SomeEngine.Next/issues/205)、[#206](https://github.com/ODtian/SomeEngine.Next/issues/206)、[#207](https://github.com/ODtian/SomeEngine.Next/issues/207)、[#208](https://github.com/ODtian/SomeEngine.Next/issues/208)。交付 [PR #209](https://github.com/ODtian/SomeEngine.Next/pull/209)。

## 编译与调用边界

游戏项目编译游戏启动和所选开发能力。引擎提供标准材质、标准 glTF 映射、资产构建和命令处理。项目持有 Shader 定义与顶层组合入口；对应管线入口持有具体资源、布局和执行依赖。正式编辑器资产操作共用 `AssetProject`，界面按 SomeUI 既有规格建设。

DefaultGame 持有场景生成、Sponza/Bistro 导入、测试面板、快捷键、测量与报告。通用应用帧入口使用 `ApplicationScene` / `ApplicationFrame`，截图复用 `SceneEdit` / `SceneImage.SaveBmp`，渲染继续经过 Game world 提取到 Rendering world。

## 实际验证

| 路径 | 结果 |
| --- | --- |
| 模板安装 | 一个 C# 项目、四个项目 Shader 来源 |
| Template、SomeGame、DefaultGame 开发构建 | 通过；迁移前工程构建目录与游戏源码通过构建配置划分 |
| 标准材质与 glTF 导入 | 18 项资产行为、10 项扩展注册、3 项资源材质检查通过 |
| 材质与管线 Shader | 独立材质 DXIL/SPIR-V 编译 2 项、既有入口 8 项通过 |
| 迁移后的 Shader 数据 | ClusterShaderDataTests 23 项通过，自动测试规则 0 违规 |
| 审查后的公开行为 | 资产 7 项、Consumer SG 2 项、帧截图与重载及既有 SceneScript 图像行为 3 项通过 |
| 自定义 classlib | 子目录中的实际库构建通过；输出类型 Library，自动 ProjectReference 为空 |
| DefaultGame 内容 | 开发命令生成 67 项资产 |
| 安装后项目的 JIT 开发命令 | 在项目外目录构建 Standard/Unlit Shader，导入真实小球网格并列出 5 项资产 |
| 安装后项目的 NativeAOT 开发命令 | 新来源目录中首次构建两个 Shader，再导入真实小球网格，列出材质、网格、世界和两个 Shader |
| DefaultGame NativeAOT 游戏组合 | 发布成功；在项目外目录启动，完成 3 帧并通过 1280×720 实际输出检查，1024 小球正常呈现 |

小球网格验证使用样例已有的 `.meta` 来源身份规则。引擎默认的持久元素 ID 与序号身份选择见 [游戏开发组合](../../../docs/Game-Development.md)。开发与游戏发布采用各自的编译配置和输出目录。

NativeAOT 开发发布记录 SharpGLTF 的 IL2104、IL3053，以及 Silk.NET 的 IL3000、IL3002 依赖警告；实际 Shader 构建、网格导入和游戏帧运行均成功。

## 审查

Standards 复核通过：测试分别表达创建、映射或缓存复用，资产命令由引擎统一识别，通用帧状态沿既有持有关系访问。

Spec 复核通过：测试平台与 JSON 上下文由样例持有，样例导入读取自身 Shader 来源，应用项目配置与自定义库配置分别由各自工程表达。

架构事实见 [游戏项目编译与开发资产](Project-Development.md)、[材质资产](Asset-Materials.md) 与 [Shader 组合](../shader/Shader-Composition.md)。

# 游戏项目编译与开发资产

## Definition

游戏项目是游戏本体、编辑器与 CLI 的编译组合入口。引擎提供通用实现，游戏通过普通程序集引用和源码生成的注册代码组合自己需要的能力。NativeAOT 在该组合的发布阶段统一编译。

## Invariants

- 开发构建包含引擎开发工具和项目扩展；游戏发布构建包含运行游戏所需的代码与已处理资产。
- CLI 与编辑器的资产操作共用 `AssetProject.ImportAsync`、`BuildAsync` 和资产加载接口，缓存与依赖由 `AssetProject` 持有。
- SG 在游戏编译时发现引擎、依赖与游戏中可访问的导入器，生成 DI 注册和静态选择 enum。扩展实现的代码位置由项目决定。
- 标准材质对象、配套资产类型、标准 glTF 材质映射和公共资产处理由引擎提供。
- 项目持有 `.shader` 定义和顶层 Slang 组合入口，选择材质与管线入口。对应管线入口及实现持有具体管线的类型、资源、布局与执行依赖。
- 材质通过阶段接口取得求值数据，并解释自己的曲面和材质字段。具体管线负责从自身资源取得阶段需要的数据。
- 默认项目模板提供游戏编译入口和项目 Shader 来源。渲染测试内容、场景生成和测试面板由 DefaultGame 样例持有。
- 编辑器正式界面沿用 SomeUI 规格；界面发起的资产操作使用同一项目编译组合中的资产 API。

## Relations

[规格 #204](https://github.com/ODtian/SomeEngine.Next/issues/204) · [材质资产](Asset-Materials.md) · [Shader 组合](../shader/Shader-Composition.md) · [资产代码生成](Asset-Code-Generation.md)

验收记录：[2026-10-13 游戏项目编译与标准资产验收](Project-Verification-2026-10-13.md)。

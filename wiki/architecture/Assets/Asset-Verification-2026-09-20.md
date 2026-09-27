# Block 索引与库导入器补充验证

工作树：`F:/SomeEngine.Next/worktrees/asset-rebuild`，分支 `codex/asset-rebuild`。本记录只覆盖 2026-09-20 的继续重构，不把 2026-09-16 的完整应用验收当成本轮结果。

## 本轮实现

- 删除全局 Block 下标初始化及读取委托注册。每个资产在打开时建立自己的持久 ID 目录，查询复用目录，`IAssetBlock<T>.ReadAsync` 静态分派到对应读取代码；仅内存 Block 无需持久读取实现。
- 项目 SG 继续生成 Microsoft DI 注册，并新增 `BlockKind`、`ImporterKind`、GUI 可绑定的 `BlockChoices` / `ImporterChoices`、`SelectImporter` 及枚举 Block API。枚举值不作为持久文件身份。
- glTF 来源读取、遍历、设置和依赖收集移入 Importers 库。本轮之后的场景组装重构又删除了项目的独立材质映射接口：现在由选中的项目 `GltfSceneImporter` 直接组成纹理、材质、Mesh 和 World 资产。
- 明确一个语义资产可混用不同存储 Block，可读格式不是必备主文件。Asset 与使用对象的驻留差异是同一底层页系统的使用约定。

## 验证结果

| 验证 | 结果 |
| --- | --- |
| `dotnet build SomeEngine.slnx --no-restore -v minimal` | 成功，0 警告、0 错误 |
| SomeEngine.Assets.Tests | 213 通过 |
| SomeEngine.Assets.Consumer.Tests | 83 通过 |
| SomeEngine.Serialization.Tests | 255 通过 |
| SomeEngine.Render.Tests | 85 通过 |
| SomeEngine.Render.Cluster.Tests | 121 通过 |
| SomeEngine.Runtime.Tests | 21 通过 |
| 六组公共行为回归合计 | 778 通过，0 失败，0 跳过 |
| Assets Native AOT，Release / win-x64，发布并实际运行 | 两组 smoke 均成功 |

新增公共行为覆盖：引用库的 Block 无项目初始化即可使用；生成枚举与 GUI 选项使用同一身份；同一进程中的不同项目可使用不同 Block 集合；库中的 glTF 来源 Importer 可直接使用项目生成的 DI 服务和外部生产者。既有行为继续覆盖文本 / 二进制混合、未知 Block 原编码保存、多个 Block 共用局部 chunk key、取消与关闭、按需读取、导入依赖及保存期间的服务作用域。

Native AOT 执行结果包含 `generated-di=ok enum-api=ok gui-choices=ok`，同时验证静态读取、引用库 Block、配置、依赖去重、生产者冲突、持久化、进程内 Block，以及资产关闭后使用对象仍可读取。发布命令为：

```powershell
dotnet publish tools/SomeEngine.Assets.AotSmoke/SomeEngine.Assets.AotSmoke.csproj -c Release -r win-x64 -o obj/asset-block-aot-check -v minimal
```

实际执行生成的 exe 后已删除本轮临时发布目录；smoke 自己创建的数据也由其 finally 清理。

本轮未重跑独立窗口多帧呈现、其他平台 Native AOT 或全部解决方案测试。GUI 交付为编译期生成的可绑定选项与枚举 API，不包含新建编辑器窗口。

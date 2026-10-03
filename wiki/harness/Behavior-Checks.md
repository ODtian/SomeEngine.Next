# Behavior Checks

行为检查通过公共 API 验证外部可观察的结果、失败处理和状态转换。每个测试只表达一条行为，名称直接陈述该行为；文件名与被测行为对应。

## 测试规则

- 通过公共 API 验证外部行为。共用辅助只提供最小夹具，例如创建设备、图、世界和读回结果；每个测试自行表达动作与期望。
- 禁止使用多个布尔值或模式开关驱动的共享场景函数。
- 断言只观察公共结果，不依赖内部计数、内部位布局、内部存储结构或具体算法。
- 禁止面向测试程序集的内部成员暴露，包括 C# 的 `InternalsVisibleTo` 和 MSBuild 的等价声明。内部行为通过公共入口覆盖；确实属于调用方能力的行为应设计为公共 API。
- 失败断言使用异常类型、参数名或错误码。调用方需要区分的失败应提供可区分的类型或错误码。禁止比较、匹配异常消息文本，包括子串、相等、正则和由消息计算后传给断言的结果。开发期检查只断言检查被触发。
- 时序通过显式同步点和记录的事件顺序证明。超时只作为防止挂起的上限，不作为“被阻塞”或先后顺序的判据。
- 禁止 `Thread.Sleep` 与静态可变状态。每个测试持有自己的状态与同步对象。
- 无断言测试必须以“不抛出”命名，或通过显式的不抛出断言表达意图。

渲染端到端测试经过引擎公共启动入口；测试内容自给并在结束后清理。图像优先与到达同一状态的另一种方式比较；绝对期望使用明确的像素区域、颜色及容差。性能验收采用根级 AGENTS 的统一场景平台，单独记录性能证据。

## 自动检查

本页是测试规则的唯一来源。规则变更先修改本页，再修改检查器与样例测试。自动检查覆盖以下三条可识别的规则，其余规则通过行为评审落实。

| 检查编号 | 检查内容 |
| --- | --- |
| TEST001 | 向测试程序集授予 `InternalsVisibleTo`：C# 程序集属性、项目或共享 `.props` / `.targets` 中的 `InternalsVisibleTo` 项、`AssemblyAttribute` 项 |
| TEST002 | 测试代码中的异常消息文本断言：`Assert` / `Xunit.Assert` 的参数读取 `.Message`，以及当前成员中由该文本计算的局部变量；`Assert.True` / `False` 的失败说明参数和 `Assert.Fail` 的诊断输出属于失败报告 |
| TEST003 | 测试代码调用 `Thread.Sleep`，包括限定名称、类型别名和静态导入 |

检查使用 C# 语法树，字符串、注释中的样例文字不参与判定。TEST002 通过语法中的 `Message` 读取识别文本依赖，并使用可解析的类型排除普通数据的同名成员。检查包含条件编译分支。测试目录和仓库自有 C# / MSBuild 声明参与扫描；`bin`、`obj`、`.git` 和第三方 `external` 内容按构建输出或依赖处理。

根级 `Directory.Build.targets` 在每个测试项目的 `VSTest` 入口构建 C# 检查工具并执行检查，`dotnet test`、指定项目的 `dotnet test` 和 `dotnet test --no-build` 均执行。发现新违规后，命令以失败退出，输出检查编号、文件、行列、成员和本页链接。

现有违规记录在 [TestRuleWhitelist.txt](../../tools/TestRules/TestRuleWhitelist.txt)，按测试项目分组。每条记录包含检查编号、相对文件路径、所属成员及去除空白的语句 token；重复项按出现次数匹配。移动行号和调整排版保留匹配，新增重复违规会耗尽对应记录并报错。项目迁移时同步移除已修复的条目，白名单只用于逐批移除现有违规。

检查器的公共输入输出测试位于 `tests/SomeEngine.TestRules.Tests`，覆盖违规样例、合规样例、位置报告和白名单行为。执行：

```powershell
dotnet test tests/SomeEngine.TestRules.Tests/SomeEngine.TestRules.Tests.csproj
dotnet msbuild tests/SomeEngine.TestRules.Tests/SomeEngine.TestRules.Tests.csproj -t:CheckTestRules
```

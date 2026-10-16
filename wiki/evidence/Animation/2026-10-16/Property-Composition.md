# 属性动画与合成公开行为

日期：2026-10-16。关联 [#49 属性动画](https://github.com/ODtian/SomeEngine.Next/issues/49)、[#50 合成](https://github.com/ODtian/SomeEngine.Next/issues/50)，覆盖完整 Spec、来源关系、控制、完成与阶段写回。

| 验证 | 队列 | 实际结果 | 原始记录 |
| --- | --- | --- | --- |
| 自定义状态及 Spring 从池迁入声明目标 | `b5e8ef83f95b493abe7d4aeace1ac171` | 3/3 通过，退出码 0 | [命令与退出码](property-takeover/result.json)、[stdout](property-takeover/stdout.log)、[stderr](property-takeover/stderr.log) |
| 前次失败回归及新增接管行为 | `0d165f5b65c648f7b2b346962ccf733d` | 11/14 通过；定位三项手写后续阶段行为 | [命令与退出码](property-regressions/result.json)、[stdout](property-regressions/stdout.log)、[stderr](property-regressions/stderr.log) |
| 上述三项阶段行为修复后重跑 | `9fcdb0aa75164f46911d5ce7d0cd4660` | 3/3 通过，退出码 0 | [命令与退出码](property-stages/result.json)、[stdout](property-stages/stdout.log)、[stderr](property-stages/stderr.log) |

14 项回归按原执行的 11 项通过与三项修复后重跑闭环。接管专项与该回归集合存在重叠。三次执行均完成 Release 构建，测试行为规则为 0 violations。

## 公开结果

完整泛型 Spec 的用户状态从池转入声明目标时，复用目标的具体状态容量。同算法延续历史与 Retarget；切换到便利 Spec 时释放旧状态并初始化新算法。目标关系完成迁移后，旧池实体进入可复用状态，Spring 保留速度。

其余回归确认普通完成字段可独立修改、池复用恢复新时钟配置、同字段来源顺序、首次位移采样前暂停、外部完成边界、目标销毁与保持来源遮罩变更。

三项阶段修复保留调用方已有的后续 TimelineStage，并让完整 Spec 与手写公有 Track 共用后继输出选择。跨越两个固定时长阶段时，当帧呈现后继阶段采样；进入 Settled 阶段后按该阶段继续采样。完成计数保持一次。

## 复现

每组 `result.json` 保存实际命令、完整测试过滤、工作目录、开始/结束时间与退出码。命令通过公共 FIFO 提交：

```powershell
& F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1 dotnet test `
  tests/SomeEngine.Animation.Tests/SomeEngine.Animation.Tests.csproj -c Release `
  --filter '<对应 result.json 的 --filter 值>' `
  --logger 'console;verbosity=minimal' `
  '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false'
```

字段生成及引用路径访问由 GeneratedChannelTests 验证；完整路径的时间、分配、容量内存、实际机器码与 NativeAOT 证据见同目录 [Typed Execution](Typed-Execution.md)。界面目标提交与 HUD 证据见 [Interface Execution](Interface-Execution.md)。

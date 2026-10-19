# 持久读取声明与同函数控制

NativeAOT 对应源码 `f1cdfbb98a76b4e7e1b6a4d0da87b23afa6449b0`。MeasurementData 在原 All<Body>/None<ExcludedBody> 成员定义上追加场景实际读取，并通过原 Members 持续持有其 World 查询匹配数据。finite 读取 CollisionLayer，character 读取 Hair，default/bitmap-space 读取 Body，其余空间组合读取 Body 与 Hair。[实际成员项](member-terms.csv)逐项投影当前 QueryDefinition 的 ComponentId、Kind、Access、Filters；工作区原始报告也保留同一数据。

声明、登记与匹配数据形成进入 SetupTicks 以及每个独立 cold 的真实构造时间。双方终端沿原 WithOptionalRead 复用已声明读取的原定义。引擎和独立手写核心沿 e17，真实输入、共同静态工作区、CPU2/High、16 帧预热、31×8 个逐帧交替样本、7 对冷构造及全部时钟边界保持。

## 正式 E/H 反馈

四项全部得到等价的实际身份行和消费结果，严格通过 2/4。[完整比值](summary.md)与[原始指标](metrics.csv)独立保存。本轮所有正式与控制样本的逐帧全线程分配均为 0；对应原 default 2760B、finite 3288B 的逐帧匹配数据形成已移入持久工作区构造。

| 场景 | E median ticks | H median ticks | median E/H | p95 E/H | 严格通过 |
| --- | ---: | ---: | ---: | ---: | --- |
| default-small-dynamic10 | 1093 | 1090 | 1.0027522935779816 | 0.97959734259602738 | False |
| finite-large-mask1-mutual | 282810.5 | 285827.5 | 0.98944468254454176 | 0.97261966115116738 | True |
| finite-large-mask4-oneway | 300072.5 | 302205.5 | 0.9929418888802487 | 0.99604099936135737 | True |
| default-large-dynamic50 | 325448.5 | 325304 | 1.0004441998868749 | 0.93563420265186903 | False |

两个 default 剩余项的[尾部记录](sample-tails.json)保存绝对微秒与 round/先后顺序/frame。small 的 median 为 109.3/109µs、p95 为 145.24/148.265µs；large 为 32544.85/32530.4µs、p95 为 44019.325/47047.58µs。两侧 GC0/1/2 合计均为 0，CPU 端点均为 2。

## H1/H2 同函数诊断

两项控制复用正式 Verify、Run、统计算法及真实工作区，两端各持独立侧计数。FirstHandwrittenType 与 SecondHandwrittenType 来自实际对象类型，原始样本使用 H1/H2、H2/H1，独立中性报告的 Required=false。控制数据见[独立表](control-summary.md)和[完整中性指标](control-metrics.csv)。

| 场景 | H1 类型 | H2 类型 | H1 median ticks | H2 median ticks | median H1/H2 | p95 H1/H2 |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| finite-large-mask1-mutual-control | HandwrittenMutualCandidatesSide | HandwrittenMutualCandidatesSide | 275907 | 276093.5 | 0.99932450419875873 | 0.99644920262334746 |
| default-large-dynamic50-control | HandwrittenDefaultSide | HandwrittenDefaultSide | 295250.5 | 295030 | 1.0007473816222079 | 0.99614245750650265 |

该控制直接记录同函数两侧的本次分布；正式 E/H 继续按原精度门槛逐项判断。

对同两份 HH raw 的一次[先后位置派生分析](handwritten-order.json)按 SideOrder 分组，保存 H1/H2 各自 First/Second 时的样本数、median/p95 原始 ticks 与微秒。31×8 原配置中，H1/H2 顺序有 128 帧，H2/H1 有 120 帧。分析源与完整输出见[独立 CPU 命令](../commands-handwritten-order-r30.json)，原 HH 样本保持原值。

## 队列边界

[完整收据](queue-boundaries.json)确认最后 case 提交 `06:38:03.9002586Z`，准备结束 `06:38:05.3836502Z`，首 case 开始 `06:39:50.5563021Z`，末 case 结束 `06:41:30.1582978Z`，首分析开始 `06:41:30.1590612Z`。准备、6 个 case、3 个分析均退出 0；场景进程执行合计 `91.8801543` 秒，场景间隔 `7.7218414` 秒。

最终 report 事件监听在 `06:38:51.7489907Z–06:38:51.7625824Z` 取得准备清单中的最后报告编号，读取早于首 case，完整时间表的 OverlappingCases=[]。[观察记录](protected-observation.json)与所有 case 起止一起保存。最终 `result.json` 事件于 `06:41:35.2293072Z` 收到，报告实际结束 `06:41:35.2289671Z`、Exit0。监听在成功与失败时均由最终收据结束。

实际 Native 发布、脚本检查、独占准备、三项分析、边界及尾部审计的完整 result/stdout/stderr 由[命令索引](../commands-controlled-reads-r30.json)持有，各 case 的三件记录随本目录 index.json 保存。

## 当前 Default 调用边界

CPU `6ae23a991ef945e896bc1214fee5759d` 精确读取关系与手写两个 World Admitted 闭合函数，object UTC `06:36:10.6020657Z`、26,203,802B。关系范围 `0x225c10..0x225f4e` 为 5 push 加 0x40 栈空间，手写 `0x209fb0..0x20a2b2` 为 4 push 加 0x48，两者均为 104B 栈、24B 初始化。关系 Required 分支 `0x225d65` 调用独立核心 `0x1d4e80`；手写 `0x20a0e1` 调用独立 PairExecution.Execute `0x15eb40`。当前两侧均通过一个独立热函数进入候选循环。

后继单项提示将 ComponentPairsWithin 私有核心声明为 AggressiveInlining，目标是让其候选循环与当前 World 回调合并。公开接口、同一值参数、组件事实、算法、输出与计时继续沿当前；调用与栈的实际变化由后继 Native 证据和两项 Default 实测记录。

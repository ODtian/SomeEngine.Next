# 私有配对核心内联回归

NativeAOT 对应源码 `edbd5ab42d3dac63c7c4a98deecc070b57c8cf77`。本轮唯一 C# 改变为 ComponentPairsWithin 私有 Enumerate<TPresence,TOutput> 的 AggressiveInlining 提示。持久读取、真实输入、强手写核心、16 帧预热、31×8 个实际逐帧交替样本、7 对冷构造及严格门槛沿 r30。

两个场景实际身份行与消费结果均等价，逐帧全线程分配为 0。[原精度结果](summary.md)按原门槛为 0/2；[metrics.csv](metrics.csv)与两份 raw.gz 保存全部分布、方差、吞吐量和冷样本。

| 场景 | E median ticks | H median ticks | median E/H | p95 E/H |
| --- | ---: | ---: | ---: | ---: |
| default-small-dynamic10 | 1192 | 1061 | 1.1234684260131951 | 1.1492156001756415 |
| default-large-dynamic50 | 311122.5 | 273507.5 | 1.1375282213467639 | 1.1311168276851726 |

## 相同 World 回调边界的机器码

原生读取 `d8880c685740475381cc661a037c912c` 与保存文本统计 `199788599e0a4e43af226bcf4117322b` 对应 object UTC `06:50:38.9209903Z`、26,201,607B。下表统一按完整 World Admitted 函数计算，包含 cold/EH、去除尾部 padding；call 为静态站点数量。

| 函数 | 指令数 | 字节 | call 站点 | 栈 B | 入口清零 B | 独立 pair 热函数 call |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 关系 r30 | 221 | 831 | 34 | 104 | 24 | 1 |
| 关系 r31 | 517 | 2237 | 53 | 1848 | 1744 | 0 |
| 手写 r30 | 208 | 771 | 33 | 104 | 24 | 1 |
| 手写 r31 | 208 | 771 | 33 | 104 | 24 | 1 |

r31 关系函数为 `0x225ae0..0x22639d`，Required 与 Optional 均进入同一回调：Required 区 `0x225c40..0x225e95`，Optional 区 `0x225e95..0x2261b4`。Required 行推进在 `0x225ccc/0x225e42` 调用 QueryRowEnumerator<NoSharedFilter>.MoveNext `0x1556c0`，列绑定在 `0x225cf7/0x225e6d` 调用 ComponentRowEnumerator<Body,Body>.BindColumns `0x161c00`。两变体共用 1848B 入口帧和 1744B 初始化，当前候选代码形成也发生变化。手写函数仍为 `0x209e70..0x20a173`，`0x209fa1` 调用独立 PairExecution.Execute `0x15ebe0`。

实际帧、调用形成与两项比值共同支持恢复原独立私有核心。后继提交仅撤回这一提示；本轮所有原始数据和原生对照继续保留。

## 受控时序

[边界收据](queue-boundaries.json)确认最后 case 提交 `06:57:41.3089803Z`，准备结束 `06:57:42.7551094Z`，首 case 开始 `06:57:42.7559402Z`，末 case 结束 `06:58:01.7334150Z`，首分析开始 `06:58:05.1407700Z`。准备、两个场景与三个分析全部退出 0，场景执行合计 `18.9767265` 秒。

[预监听观察](protected-observation.json)记录监听启动于 `06:52:15.0465582Z`，早于准备提交；manifest Changed 事件在 `06:57:42.5042767Z–06:57:42.5218438Z` 注册最终 report 编号，注册早于准备结束及首 case，OverlappingCases=[]。最终收据事件于 `06:58:09.5544685Z` 结束，实际报告结束 `06:58:09.5541243Z`、Exit0。完整发布、准备、分析、原生读取及离线统计的 result/stdout/stderr 见[命令记录](../commands-controlled-inline-r31.json)。

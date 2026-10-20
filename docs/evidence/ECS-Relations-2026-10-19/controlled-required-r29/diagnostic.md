# 构造时组件必需事实的受控 27 项

NativeAOT 对应源码 `e17a82f594c8e6c9ed4f4ff35b5e316a67afa314`。`QueryDefinition.Requires<T>()` 集中解释 All 与唯一 Any 的必有事实，`ComponentPairsWithin` 构造时保存当前两位置组件的 `_componentsRequired`；枚举入口读取该字段选择已有 Required/Optional 核心。描述形成成本计入实际索引/查询初始化。输入、共同静态工作区、独立手写算法、CPU2/High、16 帧预热、31×8 个真实单帧样本、7 次冷构造及逐帧 E/H、H/E 交替沿 r28。

[完整原精度结果](summary.md)为 10/27 同时通过 median 与 p95：finite 8/18，default 2/9。27 项真实输出与消费结果均等价。[metrics.csv](metrics.csv)保留全部比值、分配、方差与吞吐量；[sample-tails.json](sample-tails.json)保存 17 项剩余比较的绝对微秒、GC、CPU、round、side order 与 frame 分布。本轮全部判定独立保留。

Default large dynamic50 的 median 为 E/H `282335/282275` ticks，比值 `1.000212558675051`，p95 比值 `1.0059990403673869`。finite large mask1 mutual 的 median/p95 比值为 `0.9920640937220917/0.9835128506211317`。两个场景实际逐帧分配分别为 default 2760B、finite 3288B，保留当前读取声明与派生查询的实际形成成本。

## 队列与授权状态读取

[完整边界](queue-boundaries.json)记录准备、每个场景、三个分析的 Submitted/Started/Finished 与 ExitCode。最后 case 提交 `05:49:35.4048332Z`，准备结束 `05:49:36.7767500Z`，首 case 开始 `05:50:40.7147958Z`，末 case 结束 `06:19:56.0724017Z`，首分析开始 `06:20:46.9623185Z`。准备、27 个 case、三个分析均退出 0。

从首 case 开始至末 case 结束共 `1755.3576059` 秒，其中 27 个 case 的进程执行合计 `169.6701125` 秒，场景间隔合计 `1585.6874934000002` 秒，最长间隔 `270.4339581` 秒。`BetweenCases` 逐段列出实际间隔，区分公共队列历时和本次场景的执行时间。

根任务授权进行一次状态读取，UTC `06:07:41.1781258Z–06:07:41.3298360Z`，持续 `0.1517102` 秒。[原始观察](protected-observation.json)保存该时刻的 18 个已完成场景与 9 个待完成场景收据；完整时间表确认该读取位于场景间隔，`OverlappingCases=[]`。源、Native 与所有采样配置在此期间保持冻结。

原 `cold.csv` Created 监听于 `06:24:09.0863170Z` 结束。后继监听以最终 report 的 `result.json` 为结束条件，覆盖成功与失败；该事件在 `06:24:09.3462179Z` 收到，报告任务实际结束于 `06:24:09.3458757Z`、Exit0。实际发布、独占准备、三个分析、边界与尾部审计及 Native 读取的 result/stdout/stderr 保存于[命令记录](../commands-controlled-required-r29.json)。每个 case 的三件原记录随本目录 index.json 持久保存。

## 当前 NativeAOT 调用路径

CPU `ba3fdb831aef4febab85231b13da372c` 精确读取同一产物的 Default caller 与 Required 核心，object UTC `05:38:53.2050839Z`、25,507,711B。实际 Single 调用者范围 `0x219070..0x2193ae`，栈由 r28 的 136B 收为 104B，初始化由 48B 收为 24B；`0x2191b6` 直接比较已保存事实，`0x2191c5` 调用 Required 核心。必要的 24B 成员查询在 `0x2191a4..0x2191b3` 形成，`0x2191c1` 直接传递其地址，输出使用原字段地址。Required 候选体 `0x1c8395..0x1c8636` 保持原 673B 形状。

当前优化归属一次性查询准备；[r28 的 137 指令核心与双方 Mutual 拒绝路径](../controlled-paired-r28/diagnostic.md)为独立对照证据。后继对实际持久成员查询的读取声明与 QueryState 持有方式单独核对，原 r29 输入与判定继续保留。

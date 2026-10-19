# ECS 关系查询性能与架构证据（2026-10-19）

本次测量通过真实 World、公开查询、现有键索引、实际二叉 AABB 树、维护依赖、成对输出缓冲与几何消费执行九类工作。完整配置为108个独立场景，主查询与额外完整帧共126项验收指标；每项分别使用原精度 median 比和 p95 比 ≤1.00。固定输入与采样见[配置](ECS-Relations-Measurement-Configuration.md)，逐项功能及包交付见[证据索引](ECS-Relations-Delivery-2026-10-19.md)。

这些优化对应的公开读取、静态事实、许可与借用回调归属见[接口调整表](ECS-Relations-API-Changes-2026-10-19.md)。

## 测量边界

| 名称 | 开始 | 完成 | 实际工作 |
| --- | --- | --- | --- |
| Setup | 创建该侧数据 | World、Scheduler、查询、输出和接触累计形成 | 冷装配和真实初始分配 |
| IndexBuild | 开始构建派生索引 | 索引构建或首维护完成 | 同输入树、摘要、键桶与维护初始化 |
| FirstQuery | 冷索引已形成 | 首次公共查询返回 | 查询准备、遍历、筛选和真实pair写入 |
| FirstConsume | 首次pair已形成 | 首次消费完成 | 计数或真实几何/穿透累计 |
| Query | 进入公共查询 | 输出数量已写回 | 查询准备、遍历、筛选与输出的融合工作 |
| MaintenanceAndQuery | 当帧数据写入完成 | 维护依赖与查询填充完成 | 提交、维护、查询及输出 |
| FrameTotal | 开始当帧写入或静态配对 | 消费依赖完成 | 实际整条工作链 |
| ScheduledPairOutput | 外层成对任务提交 | 全部分区与输出完成 | 分区、count/prefix/fill、子任务调度与join |
| PairsAndConsume | 进入公开配对入口 | 实际几何消费完成 | 配对和真实接触累计 |
| IndependentMaintenance/Query/Consume | 该阶段前置依赖已完成 | 该阶段提交至完成 | 独立诊断采样，原始报告各自具名 |
| ProcessToFirstResult | 启动新进程 | 父进程收到首结果行 | 操作系统启动、程序集初始化及Main内首执行 |

MainTicks从Main入口开始；FirstQueryTicks只包围该进程的首次查询。进程到首结果单独使用父进程时钟。维护、查询和消费的独立阶段采样位于 `--mode phases`，主维护链仍按真实依赖连续调度。Query将融合的遍历、筛选与输出作为一个实际边界记录。

所有帧在计时范围外记录当前/全线程分配与GC/Managed/WorkingSet，原始 FrameAllocatedBytes覆盖该帧实际查询和消费。cold total覆盖装配、索引、首查询及首消费。计数/前缀/填充按现有公开调度边界进入ScheduledPairOutput。

## 对照算法与共同内核

两侧各持自己的 World、成员查询、索引、树/摘要、观察者、Scheduler、输出和接触累计。固定输入、身份创建顺序、业务值、遍历顺序、摘要布局、分区/线程配置、输出格式、采样与消费相同。

| 工作 | 扩展入口/独立部分 | 手写入口/独立部分 | 共同具体内核 |
| --- | --- | --- | --- |
| 默认配对 | ComponentPairsWithin，具体组件绑定和IRelation调用 | HandwrittenDefaultPairs，剩余行组合与直接Kind条件 | World查询、相同ReadRows列绑定、完整Entity先后规则、真实输出 |
| 角色键桶 | KeyGroups/KeyPairs及具体键语义 | HandwrittenKeyPairs，键收集、桶对和实体循环直接展开 | World.GetByIndex、相同HashSet键序、成员确认与输出 |
| 有限类别 | 固定候选→BitmapFilter→具体OneWay/Mutual许可 | HandwrittenBitmapCollision，直接字段位交集 | 同候选、World查询与可选引用读取、身份输出与接触计数 |
| 空间筛选 | SpatialTraversal与具体组合筛选/证明 | HandwrittenSpatialBitmap/Keys/Groups，节点/方向/证明/叶子循环独立 | CollisionTree、相同摘要数组/布局、基本树/摘要操作与查询读取 |
| 增量维护 | CollisionMaintenance与具体摘要变更组合 | HandwrittenSpatialMaintenance，观察/版本/成员/父链路由独立 | EntityTableChanges、ChangedChunks、相同树/摘要基础更新和Scheduler |
| 串并行输出 | CollisionPartitions与公开FillPairs/FillPairsParallel | HandwrittenSpatialPartitions，独立EnumeratePartition | 相同CreatePartitions、CollisionPairBuffer、count/prefix/fill、ReadOnlyQueryPairJobs和子任务 |
| 实际消费 | 公开配对结果进入消费 | 手写配对结果进入同一消费 | 相同PairJob/ConsumeJob、Body几何读取、接触次数及穿透累计 |

完整 `complete`、`consume`、以及空间键/bitmap类直接从公开关系入口与 `HandwrittenSpatialCollision.Filtered/Bitmap/KeyPairs` 入口成对计时，查询准备成本随实际调用计入。持续更新和调度类记录各自实际维护与输出路径。

## 内存范围

原始 IndexMemory 将 TreeLive、TreeAddressed、TreeCapacity 分列；live为存活拓扑，addressed为可寻址节点高水位，capacity为实际数组。类别和键摘要分别记录同三种范围。LeafMap记录有效键值payload、条目数及实际entry容量；KeyBuckets记录现有索引实体payload。

输出记录实际行数、已用payload和容量。分区记录节点对、count、offset、有效分区索引四数组及访问数组的已用/容量；维护访问与接触累计单列。具体元素大小来自目标产物 `Unsafe.SizeOf<T>()`。对象、字典管理、查询、观察者、Scheduler与租用数据进入真实分配和进程内存读数。线程类的pair与接触累计分别由其实际缓冲持有。

## 原始差值与落实的架构变化

| 源码提交 | 实际差值/指令 | 实现变化 | 原始记录 |
| --- | --- | --- | --- |
| 1bc8ebb4 初始 | 完整空间重复三次归一读取定义；每次1632B，稳定帧多4896B；维护未变化行进入值类型默认接口方法，456×32B装箱 | 复用已经满足的读取定义、单成员融合范围；库内HasSummaryChange显式实现；逐块保存具体filter | [initial-small](evidence/ECS-Relations-2026-10-19/initial-small/index.json)、[optimized-1-small](evidence/ECS-Relations-2026-10-19/optimized-1-small/index.json) |
| 7c6febe4 → 920e4eb4 | 固定候选先验证成员，后续绑定再次回查；空间来源也取得了相同行 | 查询专用输出/谓词借用两行、末端一次身份适配；键源先排除身份顺序；空间逐叶复用行 | [optimized-2](evidence/ECS-Relations-2026-10-19/optimized-2/index.json)、[独立阶段](evidence/ECS-Relations-2026-10-19/optimized-2-phases/index.json) |
| 4f52aa7a | Has和读取列重复定位；摘要热调用重复取得Nodes跨度；内外两层exclude | TryReadRef借用引用；节点引用直接进入摘要证明；Where/Except在既有筛选持有处统一选择 | [optimized-3](evidence/ECS-Relations-2026-10-19/optimized-3/index.json) |
| 22d9b1d3 | exclude仍处于热布尔数据流；普通组件、bitmap、key可在循环前选择同一接受策略 | 静态WhereSelection/ExceptSelection；过滤层仅持实际数据 | [optimized-4](evidence/ECS-Relations-2026-10-19/optimized-4/summary.md) |
| 35e71c5a | 默认绑定第二参数在语义短路前定位；两侧原行遍历具有同一成员/版本行为 | ReadRows在块边界绑定可选列，引用按行索引借用；手写默认配对同用公开准备；bitmap缺失早返回后直接许可尾表达式 | [optimized-5](evidence/ECS-Relations-2026-10-19/optimized-5/summary.md)、[完整matrix-r5](evidence/ECS-Relations-2026-10-19/matrix-r5/summary.md) |
| b68e892d | Native默认循环在排序和输出各读两次Entity；两个方向展开两套Has/Matches；顺序条件先物化三向数值 | 缓存外/内Entity；先选择有向组件引用后共用一次Matches；Precedes表达完整身份bool先后，手写同步采用相同准备 | [optimized-6](evidence/ECS-Relations-2026-10-19/optimized-6/summary.md) |
| a40d6c75 | 键桶80B枚举复制与32B Current中转；维护逐行重复组件/版本读取；Tree getter多次调用 | MoveNext(out group)、原成员视图复用；chunk版本绑定与静态组合变化读取器；同类型几何/摘要变化共享，Tree局部引用 | [optimized-7](evidence/ECS-Relations-2026-10-19/optimized-7/summary.md)、[构建与红绿](evidence/ECS-Relations-2026-10-19/commands-r7.json)、[实际Native读取](evidence/ECS-Relations-2026-10-19/native-r7.json) |
| 3bf336e1 | 必有组件仍逐对判断存在性；交换不变规则先排序；整体CategoryBits投影提前读取双方字段 | Requires闭合Required/Optional模式，IsSymmetric先匹配后排序；具体许可按方向读取标量，摘要保持原布局 | [optimized-8](evidence/ECS-Relations-2026-10-19/optimized-8/summary.md)、[行为与发布](evidence/ECS-Relations-2026-10-19/commands-r8.json)、[实际Native读取](evidence/ECS-Relations-2026-10-19/native-r8.json)、[九个独立维护阶段](evidence/ECS-Relations-2026-10-19/phases-r8/summary.md) |
| dcf50ecc | 默认私有循环只使用First成员，但接收完整视图；同组件AddReads执行两次 | 私有循环直接接QueryCursor，入口一次Requires分类，同类型读取静态合并；恢复键收集的独立边界 | [optimized-9](evidence/ECS-Relations-2026-10-19/optimized-9/summary.md)、[构建、检查与caller读取](evidence/ECS-Relations-2026-10-19/commands-r9.json) |

每份原始记录包含其实际完整提交。公开行为检查覆盖原成员、异型/可选组件位置、Where/Except、字面结果、ref输出与提前结束；[交付索引](ECS-Relations-Delivery-2026-10-19.md)列出实际文件。最终包、生成物与Native消费链沿[实际包刷新入口](ECS-Relations-Packages-AOT-2026-10-19.md)对应代码。

a40d6c75 的结构变化来自同一 NativeAOT 产物的指令定位：键桶枚举把相同成员视图保留成长期局部，挤占输出身份字段寄存器；`KeyGroups.MoveNext` 先写 Current 再由调用方读取。实现改为直接使用原成员视图和 `MoveNext(out group)`。筛选使用静态 `RequiresMatch` 直接选择接受分支。维护把 Tree 引用保留在 Reconcile/Refit 调用局部，并通过 `ReadRowChanges<T>()` 在 chunk 边界绑定原版本跨度；组合筛选给回调提供具体变化读取器，原 `RowIndices` 继续形成成员集合。手写维护使用相同公共版本读取。

新增公共版本读取的红检查队列为 `98eed6b306cf4c7991fe012c33768823`（公共成员尚待实现时的 CS1061），绿检查 `6212b321edd94f24afbb42b73bb595f5` 通过1项真实 World 行版本检查。关系桶与筛选队列 `2bafedde700a4ac88bb35bcb332a2775` 通过5项；维护及并行消费队列 `61d5cfc267384e9ba435cba3322d9687` 通过4项。这些检查均运行仓库行为规则，结果为0 violations。

r7 的原生证据同时明确了已收敛的部分：键桶直接进入 HashSet.MoveNext 和 GetByIndex，80B枚举复制与32B Current中转均已消去；原来提前形成的身份tuple也已标量化为三个寄存器与一个栈字段，手写使用两个寄存器与两个栈字段。exclude只参与桶级接受选择，inner pair直接执行身份顺序、成员和输出。后续键改动因此集中在仍独立调用且复制24B输入/结果的KeyGroups构造，使用原构造器内联完成准备融合。

r7 稀疏维护在小/中/大的 MaintenanceAndQuery 原精度比值分别为0.9479976090854751/0.913827655310621、0.9288389513108615/0.9190815542019042、0.925881086898036/0.9221554987415026，三个完整帧也通过同一严格判断。默认配对的下一处成本来自排序在Kind匹配之前执行以及两次HasValue；已有查询的Requires事实闭合必有模式，作者显式IsSymmetric事实允许先匹配再排序。有限类别的具体许可改用标量读取，单向按需取得两个字段，双方许可按两个方向短路，CategoryBits/摘要保持原布局。

r8 的 Required+IsSymmetric 默认循环与手写已逐条对应：配对主段均为673字节，内层组件绑定、Kind、身份、排序和输出均为175字节；通用入口额外抽取24B成员查询，frame为912B，手写为880B，行栈位置相差8。实际Where caller先形成216B视图，再复制First查询并读取Requires；手写直接向循环传入24B查询。后续把私有循环输入收敛为QueryCursor members，并按相同组件类型合并AddReads。

r8 同时给出了构造器强内联的实际回退证据：KeyPairs栈由r7的392B升到808B，收集键所用成员视图的四个字段长期占用寄存器，候选和输出新增Index/Generation栈重载。小规模角色16聚集的原精度比值变为1.0904528217711353/1.095232464179428。实现恢复独立的键收集边界，桶级选择和已标量化tuple继续沿原路径。两侧HashSet/List、成员查询、GetByIndex算法和数据持有均相同。

r9 的 SingleExecution caller 把完整216B视图就地形成：Access/First共48B，另外七个查询共168B清零，然后独立调用公开selector。它已收敛r8的整块BulkMove，剩余完整构造由只读CPU队列 `a52f561cbb374a3fb960995e245516ca` 直接确认。下一处实现只内联这个无候选循环的轻selector，让其仅First读取在调用处裁剪；候选循环保持独立。键构造进一步收敛为原三个字段的薄赋值，具体收集helper独立接收scoped in查询借用，成员字段的存活在收集函数结束。

## 执行与复现

实际公共队列入口为当前主检出已有 `F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1`。提交脚本通过 QueueScript 参数指定该绝对入口，默认按仓库tools定位；每次queue的Executable/Arguments/Directory/SessionId与Submitted/Started/Finished时间均保存在持久index。以下命令使用运行器同一Release/NativeAOT输出，实际SourceCommit由原始报告和提交参数指定：

```powershell
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 dotnet publish benchmarks/SomeEngine.ECS.Relations.Benchmarks/SomeEngine.ECS.Relations.Benchmarks.csproj -c Release -r win-x64 --no-restore '-p:SourceRevisionId=<实际提交>' -o benchmarks/SomeEngine.ECS.Relations.Benchmarks/bin/Release/net10.0/win-x64/native-publish
& benchmarks/SomeEngine.ECS.Relations.Benchmarks/Submit-Measurements.ps1 -QueueScript F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 -SourceCommit <实际提交> -Series <系列>
```

MSBuild属性通过实际 `[string[]]` 参数传递；每项场景分别FIFO提交。`-Backend JIT` 使用同一Release托管入口并单独保存后端。`-Mode phases` 只选择持续更新场景并保存真实独立阶段。原生进程首结果由 [Measure-Startup.ps1](../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Measure-Startup.ps1) 按七对交替新进程采样。

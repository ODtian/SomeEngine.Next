# ECS 关系查询性能与架构证据（2026-10-19）

本次测量通过真实 World、公开查询、现有键索引、实际二叉 AABB 树、维护依赖、成对输出缓冲与几何消费执行九类工作。完整配置为108个独立场景，主查询与额外完整帧共126项验收指标；每项分别使用原精度 median 比和 p95 比 ≤1.00。固定输入与采样见[配置](ECS-Relations-Measurement-Configuration.md)，逐项功能及包交付见[证据索引](ECS-Relations-Delivery-2026-10-19.md)。

这些优化对应的公开读取、静态事实、许可与借用回调归属见[接口调整表](ECS-Relations-API-Changes-2026-10-19.md)。

## 完整矩阵记录

`matrix-r15` 对应源码 `cc08ae37b03e7d9066adbd78207a6e8249a4b1d1` 的同一NativeAOT产物，108项均完成完整结果核对，126项严格指标中99项通过。[逐项原精度表](evidence/ECS-Relations-2026-10-19/matrix-r15/summary.md)和[原始报告索引](evidence/ECS-Relations-2026-10-19/matrix-r15/index.json)保存每项的实际判断。该轮继续提供后继优化的完整输入，分布、内存、冷执行和队列输出分别保留在同目录。

| 场景类 | 主指标通过/数量 | 额外完整帧通过/数量 |
| --- | --- | --- |
| finite | 4/18 | — |
| default | 1/9 | — |
| character | 9/12 | — |
| bitmap-space | 18/18 | — |
| character-space | 5/6 | — |
| complete | 9/9 | 9/9 |
| updates | 8/9 | 9/9 |
| threads | 18/18 | — |
| consume | 9/9 | — |

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
| fc2b9c2d | r9轻selector前仍构造216B视图；键收集与配对共享成员字段的长存活 | 轻selector内联后仅形成First24B，caller栈256B降为88B；薄构造器赋原字段，独立CollectKeys接收查询借用 | [optimized-10](evidence/ECS-Relations-2026-10-19/optimized-10/summary.md)、[构建、原生与成员检查](evidence/ECS-Relations-2026-10-19/commands-r10-r11.json) |
| 87559c84 | 纯成员判断形成40B QueryRow；Contains全内联后两侧对子调用选择不同 | Contains复用同一World匹配，输出需要组件行时静态保留TryGetRow；共同bool匹配边界独立并复用一次EntityStore读取 | [optimized-11](evidence/ECS-Relations-2026-10-19/optimized-11/summary.md)、[构建、原生与许可检查](evidence/ECS-Relations-2026-10-19/commands-r11-r12.json) |
| 89a59155 | 许可两方向直接test/je；pair中转有四次int写入、16B重读和回调this读取 | 无状态static abstract结果操作，原pair以scoped in直达输出；默认私有loop仅接members/predicate/output，共同Contains单次取得Store | [optimized-12](evidence/ECS-Relations-2026-10-19/optimized-12/summary.md)、[实际指令与检查](evidence/ECS-Relations-2026-10-19/commands-r12-r13.json) |
| d361242b | static结果和原身份直接写输出；两端候选loop各展开两大份World匹配 | TryGetEntityQueryRow与Contains保留共同点匹配边界，内部复用同一record helper，具体许可与结果继续内联 | [optimized-13](evidence/ECS-Relations-2026-10-19/optimized-13/summary.md)、[实际原生与分布读取](evidence/ECS-Relations-2026-10-19/commands-r13-r14.json) |
| 0f4fdf4b | 行匹配保留同一独立入口；Key caller仍形成216B视图，bitmap输出引用跨组件绑定存活 | 键轻入口向静态桶循环仅传具体成员与访问输入；先绑定组件引用，再向具体许可借用唯一输出 | [optimized-14](evidence/ECS-Relations-2026-10-19/optimized-14/summary.md) |
| cc08ae37 | 默认Bound在轻分支前清1136B回退状态；固定候选先持两份QueryRow跨第二次点读取 | 轻只读选择与完整回退的数据形成分开；固定身份通用绑定先取得第一组件再进入第二次点读取 | [optimized-15](evidence/ECS-Relations-2026-10-19/optimized-15/summary.md)、[实际原生与IL](evidence/ECS-Relations-2026-10-19/commands-r15.json) |
| 92d8b230 | Bound回退清零已裁剪；Single仍独立清120B、复制32B描述两次并判断可空版本 | 明确内联现有Single，在轻入口继续合并普通调用的版本与执行准备 | [optimized-16](evidence/ECS-Relations-2026-10-19/optimized-16/summary.md)、[外层原生与聚焦检查](evidence/ECS-Relations-2026-10-19/commands-r16-r17.json) |
| 5bf03d44 | Single已合入公共调用并闭合默认版本；仍有三次32B描述复制，位图每候选形成两份40B行 | Single构造以scoped in直接接收原描述字段并持有一次值副本；类型化公共点读取将成员匹配与单组件引用形成放在同一操作 | [optimized-17](evidence/ECS-Relations-2026-10-19/optimized-17/summary.md)、[实际外层与点读取红绿](evidence/ECS-Relations-2026-10-19/commands-r17-r18.json) |
| 22fad2a6 | 共同typed point已缩为8B out；每候选仍清16B；scoped in构造另形成64B执行临时 | 内部点读返回单个ComponentReference，公开轻包装形成bool/out；固定数组直接foreach；执行局部直接赋已有字段 | [optimized-18](evidence/ECS-Relations-2026-10-19/optimized-18/summary.md)、[实际四函数与聚焦检查](evidence/ECS-Relations-2026-10-19/commands-r18-r19.json) |
| bb799424 | typed point已通过RAX返回；循环首引用/count仍有栈存取，默认仍两段32B描述 | 深point直接借用原QueryCursor输入；轻只读选择直接读取原readonly描述字段 | [optimized-19](evidence/ECS-Relations-2026-10-19/optimized-19/summary.md)、[实际四函数与聚焦检查](evidence/ECS-Relations-2026-10-19/commands-r19-r20.json) |
| 81478391 | 许可/首引用/count热段已对应手写，输出准备仍有两段24B，候选loop展开在World作用域 | 薄入口与独立数组core分开；filtered直接形成现有字段；已有读取轻复用与新定义创建分开 | [optimized-20](evidence/ECS-Relations-2026-10-19/optimized-20/summary.md)、[实际四函数与聚焦检查](evidence/ECS-Relations-2026-10-19/commands-r20-r21.json) |
| 9f749801 | 独立数组core接受热段30条/101B，手写29条/100B；每候选多一次输出引用探测及提前地址形成 | core以单一局部具体输出执行整个候选循环，finally回写原输出；普通调用、异常和提前结束沿同一回写范围 | [optimized-21](evidence/ECS-Relations-2026-10-19/optimized-21/summary.md)、[实际四函数与7项行为](evidence/ECS-Relations-2026-10-19/commands-r21-r22.json) |
| 0ac01593 | 单向28/29条、双方31/32条热段已直接对应；默认准备仍有第一份32B metadata值及固定初始化，Where/Except在描述中为bool | BitmapFilter与ComponentPairsWithin以TSelection闭合接受选择；生成Where/Except直接返回对应普通组合值，既有过滤输出与许可继续复用 | [受控完整系列](evidence/ECS-Relations-2026-10-19/controlled-r22/summary.md)、[后继五函数与10项行为](evidence/ECS-Relations-2026-10-19/commands-r22-r23.json) |

每份原始记录包含其实际完整提交。公开行为检查覆盖原成员、异型/可选组件位置、Where/Except、字面结果、ref输出与提前结束；[交付索引](ECS-Relations-Delivery-2026-10-19.md)列出实际文件。最终包、生成物与Native消费链沿[实际包刷新入口](ECS-Relations-Packages-AOT-2026-10-19.md)对应代码。

a40d6c75 的结构变化来自同一 NativeAOT 产物的指令定位：键桶枚举把相同成员视图保留成长期局部，挤占输出身份字段寄存器；`KeyGroups.MoveNext` 先写 Current 再由调用方读取。实现改为直接使用原成员视图和 `MoveNext(out group)`。筛选使用静态 `RequiresMatch` 直接选择接受分支。维护把 Tree 引用保留在 Reconcile/Refit 调用局部，并通过 `ReadRowChanges<T>()` 在 chunk 边界绑定原版本跨度；组合筛选给回调提供具体变化读取器，原 `RowIndices` 继续形成成员集合。手写维护使用相同公共版本读取。

新增公共版本读取的红检查队列为 `98eed6b306cf4c7991fe012c33768823`（公共成员尚待实现时的 CS1061），绿检查 `6212b321edd94f24afbb42b73bb595f5` 通过1项真实 World 行版本检查。关系桶与筛选队列 `2bafedde700a4ac88bb35bcb332a2775` 通过5项；维护及并行消费队列 `61d5cfc267384e9ba435cba3322d9687` 通过4项。这些检查均运行仓库行为规则，结果为0 violations。

r7 的原生证据同时明确了已收敛的部分：键桶直接进入 HashSet.MoveNext 和 GetByIndex，80B枚举复制与32B Current中转均已消去；原来提前形成的身份tuple也已标量化为三个寄存器与一个栈字段，手写使用两个寄存器与两个栈字段。exclude只参与桶级接受选择，inner pair直接执行身份顺序、成员和输出。后续键改动因此集中在仍独立调用且复制24B输入/结果的KeyGroups构造，使用原构造器内联完成准备融合。

r7 稀疏维护在小/中/大的 MaintenanceAndQuery 原精度比值分别为0.9479976090854751/0.913827655310621、0.9288389513108615/0.9190815542019042、0.925881086898036/0.9221554987415026，三个完整帧也通过同一严格判断。默认配对的下一处成本来自排序在Kind匹配之前执行以及两次HasValue；已有查询的Requires事实闭合必有模式，作者显式IsSymmetric事实允许先匹配再排序。有限类别的具体许可改用标量读取，单向按需取得两个字段，双方许可按两个方向短路，CategoryBits/摘要保持原布局。

r8 的 Required+IsSymmetric 默认循环与手写已逐条对应：配对主段均为673字节，内层组件绑定、Kind、身份、排序和输出均为175字节；通用入口额外抽取24B成员查询，frame为912B，手写为880B，行栈位置相差8。实际Where caller先形成216B视图，再复制First查询并读取Requires；手写直接向循环传入24B查询。后续把私有循环输入收敛为QueryCursor members，并按相同组件类型合并AddReads。

r8 同时给出了构造器强内联的实际回退证据：KeyPairs栈由r7的392B升到808B，收集键所用成员视图的四个字段长期占用寄存器，候选和输出新增Index/Generation栈重载。小规模角色16聚集的原精度比值变为1.0904528217711353/1.095232464179428。实现恢复独立的键收集边界，桶级选择和已标量化tuple继续沿原路径。两侧HashSet/List、成员查询、GetByIndex算法和数据持有均相同。

r9 的 SingleExecution caller 把完整216B视图就地形成：Access/First共48B，另外七个查询共168B清零，然后独立调用公开selector。它已收敛r8的整块BulkMove，剩余完整构造由只读CPU队列 `a52f561cbb374a3fb960995e245516ca` 直接确认。下一处实现只内联这个无候选循环的轻selector，让其仅First读取在调用处裁剪；候选循环保持独立。键构造进一步收敛为原三个字段的薄赋值，具体收集helper独立接收scoped in查询借用，成员字段的存活在收集函数结束。

r10 实际指令确认轻selector收敛：caller仅形成First的24B，读取Requires后直接进入Required循环，栈由256B变为88B。Mutual许可按方向懒读并使用内存test；最终bool进入Where仍保留setne/movzbl/test。对应小规模mask4双方许可的median为4740/4885.5ticks、p95为5460.95/5423.95ticks，实际输出1008对。这些原始结果逐项保存实际判定。

键桶中的纯成员检查继续使用同一World点匹配流程：QueryCursor.Contains只取得成员事实，TryGetRow取得后续组件读取所需的行。KeyPairs按输出ReadsComponentRows静态选择，手写键枚举及Between去重同步采用Contains。公开Changed行与optional读取检查先得到缺少Contains的红结果，再通过1项真实World行为；既有角色、组合、ref输出提前结束和Between共5项通过。

r11 的26个反馈场景完整保留。其中默认私有循环和手写的673B主段、175B内层、137条归一化操作、880B栈及三个共享推进/列绑定调用均对应；实例入口额外两条操作使主循环相对起点增加5B。实际默认比值仍有5%–9%差值，后续将私有循环输入改为显式成员与关系值，测量继续观察实际入口和布局的影响。键桶的内联成员判断中，扩展保留IsAlive/TryGetValue调用，手写展开IsAlive并直入FindValue；成员匹配保留独立bool边界，使双方进入同一具体实现。角色键tuple和输出字段继续具有同样紧凑形状。

有限类别的结果消费接口为具体许可增加Evaluate回调，默认实现继续调用该许可自己的Matches。Builtin许可按方向进入Matched/Unmatched，原BitmapFilterOutput单持有当前身份和输出状态，组件链沿原scoped行转发。自定义完整目标类别许可的字面结果与既有六项位图行为共7项通过，检查队列为 `fd00d6ee4a6445ecb374592797908f36`。

r12 在15项反馈中确认中/大角色键及小规模dynamic90通过严格判断。实际原生读取显示许可两个方向已直接test/je；当前pair仍在适配器中写四个int，接受后再读16B写输出。后继把输入改为scoped in，将结果操作改为static abstract无状态类型，原输出仍单持有。7项检查队列 `d153f245229045c1b700c0283f89615b` 通过。r12 的默认static入口准备由5条变为2条，手写为3条；键的四个成员检查均调用同一Contains函数，其中Store引用形成一次并复用。

r13 的16项反馈中8项严格通过。原生读取确认了static结果操作与原身份直接进入终端输出；双方方向判断均为Allowed读取、内存test及直接条件分支。共同World行匹配在提取helper后被两侧候选loop各展开两份，包括结构状态、record、archetype字典和逐行筛选。后继为TryGetEntityQueryRow保留独立边界，和Contains复用同一匹配helper。剩余Output借用跨组件绑定的现象由同一原生记录保留，反馈继续按实际边界定位。

r13 单向mask4小场景的高尾集中在连续时段：扩展后段变慢后，紧随的手写帧也连续变慢；其原精度1.3779131621735523判定仍保存。中规模角色64交错的扩展分布整体右移，median61225/59574.5ticks，两侧先后次序均出现持续差值。所核对的五项每侧248样本均记录GC=0、DataWrite=0及恒定一致的输出量，后续优化据此区分具体执行成本和实际波动。

r14 保留共同点匹配边界后，16项反馈中3项严格通过；单向mask1小规模为0.90920840064620356/0.94650281360586308，中规模角色64交错为0.99643525823937729/0.9909627398585259。双方许可mask4大规模为1.0090796568356213/1.0141933825092542。原生键caller仍构造Access/First共48B并清零其余168B，再独立进入大循环；其具体配对规则只读取两个成员集合。后继将公开入口与桶循环分开，Within沿同一First借用，Between/Product保留各自实际读取范围；语义和实现以scoped in继续保持描述的只读调用含义。

r15 的16项代表反馈中9项严格通过。小/中角色键的median/p95比值分别为0.93889668573896146/0.72199782401740809、0.92901277737097154/0.92179233462713184；大规模角色16交错为1.0002589194514533/1.0174027101224457。小规模mask4单向与双方为0.9789706696181516/0.94767167825777465、0.99105545617173529/0.93550897237590025；大规模mask4双方为0.9885768930983212/0.97329500175662587。两向匹配、自定义结果转发及缺失组件规则由7项公开检查覆盖，键位置、组合和提前结束由5项检查覆盖。

r15 的[实际原生读取](evidence/ECS-Relations-2026-10-19/commands-r15.json)确认键caller只形成24B第一成员输入，并将同一地址传给Within桶循环的三个查询借用；core栈为248B。位图两侧各调用两次同一World行匹配函数，输出在组件引用形成后直接取地址，原输出地址的跨绑定栈存放已收敛。当前固定身份来源先形成两份40B行，再进入组件绑定；手写在第二次点匹配前已形成第一组件的8B借用，二者的实际寿命与frame分别记录在同一输出中。

默认外层的独立读取定位到实际数据形成范围：EnumerateBoundQuery入口为1192B栈，在只读CanWrite条件前清1136B；只读分支随后进入另一个168B栈、清120B的Single。手写入口为104B栈、清56B。双方GetQueryDefinition、WithOptionalRead、AcquireQueryRecord和ReleaseQuery各执行一次。完整回退的数据形成因而需要独立于轻量的单成员选择入口；实际边界与指令由同一原生记录持有。

固定候选的身份数组由当次参与实体形成，有限类别采样保持该成员集合。来源直接转发身份后，通用identity binder依次执行第一成员点读取、第一组件引用、第二成员点读取、第二组件引用；手写保持相同两次公共点读取并直接执行字段位运算。多层固定身份筛选各自绑定的成本进入其完整调用，已经取得行的Within/键/空间来源继续复用查询专用借用输出。

r16 的32个反馈场景形成33项主/完整帧判断，其中5项通过。小规模角色16聚集/交错为0.90826284970722182/0.93446435379336668、0.92376388586364622/0.96284720521236455；稀疏维护和完整帧、空间角色交错均通过。位图18项的实际median比为1.0010687566797292至1.153845045045045，默认配对9项为1.0008403633609675至1.02089081592432，原精度逐项保存在同一报告中。

r16 原生外层确认Bound的1192B栈和1136B回退清零已裁剪，Single仍保留120B初始化、两次32B描述复制和可空版本分支；两侧外层初始化合计176B/80B。[有限类别函数摘录](evidence/ECS-Relations-2026-10-19/native-r16-mutual-excerpt.json)保留明确的选取范围与源队列元数据：查询字段和输出已在候选循环前形成局部，第一组件引用跨第二次point读取存活；每候选仍有覆盖两份40B QueryRow的六次16B零写，手写在入口初始化行home。该记录为按函数选取的有界摘录，范围说明随证据保存。

r17 的12项反馈中3项通过：默认small dynamic10为0.99429073342116825/0.88786111669682954，large dynamic10为0.99807369137956747/0.99244240602079692，角色large hair64 clustered为0.95837238104866929/0.96186877128710302。其余默认7项与角色2项逐项保留原精度判定。原生Single符号已消入RelationArraySide.Query，默认基线直接调用同一ExecuteQuery；外层栈合计320B降为264B，初始化仍176B，手写对应192B/80B。描述仍经三段32B复制进入执行数据，后继让Single构造以scoped in直接接收原描述字段并持有一次普通值副本，输出保留值字段及finally回写。普通描述的ref借用实验由C#输出回写的CS8352约束收敛到这一值持有方式，实际构造复制继续由原生证据核对。

类型化公共点读取通过真实World红绿检查：Changed成员、托管组件和可选读取给出字面结果4,-1,-1,-1，红队列5b97d35094724620a72e63a1a6a06398、绿队列d1f56e6fe2484602950f347998c941ed。QueryCursor.TryReadRef直接复用原World成员匹配和访问声明，成功只形成8B组件借用。位图身份绑定与手写候选依次执行相同两次公共操作，绑定失败直接进入具体Unmatched，成功由具体许可Evaluate驱动输出；7项位图行为通过队列7b766e27a4a240ecb196ce6d895a475b。

r18 的30个反馈场景均完成等价核对，5项通过严格判断：finite 2/18、default 0/9、character 3/3。三项角色键的原精度比值分别为0.97809878844361609/0.95980235042735063、0.97042172944079652/0.97734208970487202、0.97680443499685976/0.96925287715227315。中规模mask8双方的实际比值为1.2490853752267348/1.2400116983307163，该项原样保留。

r18 的四个完整原生函数确认两侧共同调用同一typed point，成功写8B引用并返回bool。通用每候选清零从96B缩为16B，第一组件引用跨第二次调用存活，许可按方向直接load/test/je；组件读取成功后的旧bool合流也已消去。剩余offset栈存取、count双读及两份引用home初始化与手写逐项记录。默认scoped in构造实际产生64B执行临时，frame与清零各比r17多32B，后继直接初始化执行局部的三个字段。内部点读进一步用既有ComponentReference的HasValue表达存在性，以单值返回替代bool和out-buffer，公开TryReadRef包装继续保留；固定数组来源直接foreach原数组，保留身份复制与输出语义。

r19 的30项反馈中15项通过：finite 6/18、default 6/9、character 3/3。默认三项剩余p95比为1.0005188644281373、1.0131834303687346、1.0035292589086611，全部按原精度保留判断。小规模mask1单向为1.1486469154803129/1.1834857251575210，中规模mask8双方为0.97966326262765147/0.99850081364069820。

r19 的World点读取直接通过RAX返回8B引用，候选中的第五out-buffer和16B引用home清零均已收敛，数组迭代使用指针递增及dec/jne，原offset栈存取已消去。通用首引用仍跨第二次调用存入栈，手写保存在寄存器；通用同时持有World/查询状态/版本三个字段，后继让深point借用同一原QueryCursor，在该边界读取字段。默认直接局部赋值使frame由296B降为216B，清零由208B降为120B，描述为两段32B、输出为一段24B。六项关系与一项真实World检查均通过，源码及完整原生输出由同一证据文件关联。

r20 的30项反馈中13项通过：finite 3/18、default 7/9、character 3/3。默认small dynamic50的median/p95为0.98155814828754229/1.0267195923929140，small dynamic90为1.0030674846625767/0.96410583166454256，其余七项严格通过。所有输入与原始帧保持同一配置。

r20 单向接受热段的扩展为28条/105B，手写为29条/100B：首组件引用均在callee-saved寄存器中跨第二次point存活，count均为一次load及递增，许可均为load/test/je。通用从查询home取地址，手写保留查询地址；两侧输入与输出读取均按实际指令记录。剩余输出准备在通用World回调中先形成24B临时，再复制24B至filtered.Output，后继直接形成原filtered的Relation/Output字段。固定候选公开入口向独立core仅传原数组与ref具体输出，使World作用域控制状态的寿命留在循环调用侧。默认准备仍为216B栈/120B初始化；WithOptionalRead进一步用既有归一化CanRead事实形成轻复用入口，新定义路径保留原类型验证及构造。

r21 的30项反馈中11项通过：finite 3/18、default 6/9、character 2/3。独立core使World作用域状态留在调用侧；caller准备从360B降至280B，新增core为88B，二者峰值368B。filtered.Output从原输出直接复制一次24B。默认WithOptionalRead命中已有读取定义时在轻入口返回原值，创建路径独立；实际描述仍为两段32B、输出一段24B。

原始调度记录补充了测量控制层的实际活动：r20提交30项用时9.1070587秒，其中13项在最后一次提交前开始执行。后续完整系列将Submit-Measurements自身作为公共队列独占准备任务：准备期间直接取得固定场景列表，显式使用同一session依序提交全部case及collect/export/report，然后退出。case在准备任务结束后开始，报告读取与计算沿预排CPU任务完成。每份系列保留queue-submission.json中的准备起止、session、源码和报告任务编号；原有系列保留其实际时间戳与原始判定。

首个[受控完整系列controlled-r22](evidence/ECS-Relations-2026-10-19/controlled-r22/summary.md)对应源码0ac0159349d41d46fa8197b86a4e1bd148debf4a，108项均完成等价核对，126项严格指标通过108项。有限类别4/18、默认配对8/9、角色键11/12、bitmap加空间17/18、键加空间5/6；完整配对主/完整帧18/18、持续更新主/完整帧18/18、串并行调度输出18/18、物理消费9/9。每项原精度median/p95及18个剩余判断随同原始样本保存。

[队列边界审计](evidence/ECS-Relations-2026-10-19/controlled-r22/queue-boundaries.json)确认最后case提交03:28:27.997716Z、独占准备完成03:28:29.2372729Z、首case开始03:28:29.2382396Z；末case完成03:40:42.8891869Z，首分析任务03:40:42.8898856Z开始。测量733.6509473秒，108case及3个分析任务同session、退出码均为0。[完整命令与输出](evidence/ECS-Relations-2026-10-19/commands-controlled-r22.json)保留独占准备的result/stdout/stderr及r22实际候选函数。

r22 单向候选的扩展接受热段为28条/93B，手写为29条/100B；逐候选输出引用探测与提前Output地址形成已收敛，首引用跨第二点读取保存在寄存器，count为单次读取及递增。原具体输出局部56B在进入core时形成一次，并沿正常/finally路径回写；core准备为136B。该事实与首轮受控结果共同记录，后继定位继续按实际剩余路径进行。

## 执行与复现

实际公共队列入口为当前主检出已有 `F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1`。提交脚本通过 QueueScript 参数指定该绝对入口，默认按仓库tools定位；每次queue的Executable/Arguments/Directory/SessionId与Submitted/Started/Finished时间均保存在持久index。以下命令使用运行器同一Release/NativeAOT输出，实际SourceCommit由原始报告和提交参数指定：

```powershell
[string[]]$publish = @('dotnet','publish','benchmarks/SomeEngine.ECS.Relations.Benchmarks/SomeEngine.ECS.Relations.Benchmarks.csproj','-c','Release','-r','win-x64','--no-restore','-p:SourceRevisionId=<实际提交>','-o','benchmarks/SomeEngine.ECS.Relations.Benchmarks/bin/Release/net10.0/win-x64/native-publish')
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 @publish
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 --session <会话> pwsh -NoProfile -File benchmarks/SomeEngine.ECS.Relations.Benchmarks/Submit-Measurements.ps1 -QueueScript F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 -Session <会话> -CollectReports -SourceCommit <实际提交> -Series <系列>
```

MSBuild属性通过实际 `[string[]]` 参数传递；每项场景分别FIFO提交。JIT在相同Release输出目录以`-p:PublishAot=false -p:SelfContained=false -p:SourceRevisionId=<实际提交>`构建普通托管入口，再以`-Backend JIT`单独提交系列；报告记录.NET10默认TieredCompilation/ReadyToRun及实际环境。`-Mode phases`只选择持续更新场景并保存真实独立阶段。原生进程首结果由[Measure-Startup.ps1](../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Measure-Startup.ps1)按七对交替新进程采样。

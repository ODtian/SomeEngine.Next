# 关系性能测量配置

来源规格：#231，第 12 票 #243。配置在首轮性能测量前固定，实际列表由 `benchmarks/SomeEngine.ECS.Relations.Benchmarks/MeasurementCases.cs` 的 `MeasurementCases.All` 形成；`--mode list` 输出同一份完整 JSON。业务 Body/Hair、运动与类别规则来自 benchmark 的 Relations 夹具，属于本次业务例子。

设备读取：Intel Xeon E5-2698B v3 @ 2.00GHz，16核/32线程；总内存68,552,818,688字节；Windows 11专业工作站版10.0.26200（build26200）。SDK10.0.113、Runtime10.0.13、win-x64。各份正式报告继续保存其实际CPU标识、处理器数、OS、Framework、GC、JIT配置、WorkerCount、计时频率与完整命令。

机器配置以运行器的`--machine Normal|Fixed`显式选择，实际字段记录于Device.Machine。两配置的输入、31×8采样、预热和实现相同：

| 配置字段 | Normal | Fixed |
| --- | --- | --- |
| 测量主线程mask | 进程允许mask | bit2，逻辑CPU2 |
| 进程允许mask | 实际进程mask | 实际进程mask |
| 进程优先级 | Normal | High |
| Scheduler工作线程 | 场景原定0或4 | 场景原定0或4 |

运行器通过[SetThreadAffinityMask](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadaffinitymask)设置当前测量线程，进程允许范围继续供Scheduler使用；[Process.PriorityClass](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.priorityclass?view=net-10.0)设置该进程的整体优先级。报告保存设置前的主线程mask、应用mask、实际进程mask/优先级及设置后CPU。每帧在计时边界外通过[GetCurrentProcessorNumber](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getcurrentprocessornumber)记录前后CPU；这些端点观察随原始样本保存。

机器配置诊断先用同一Native产物运行default-small-dynamic50和finite-large-mask1-oneway各两种配置，共四项。提交器对第一个输入采用Normal→Fixed，第二个输入采用Fixed→Normal，全部case和报告先在独占准备任务中提交。两种配置各有自己的完整原精度判定；后续统一系列显式记录选定的同一配置。此前系列保留各自实际设备与提交条件。

| 场景类 | 小／中／大 | 每规模变体 | 必须测量的边界 |
| --- | --- | --- | --- |
| finite | 128/512/2048，候选4096/32768/262144 | mask位数1/4/8 × 单向／双方 | 公共查询准备、固定候选、位判断、实际输出 |
| default | 128/512/2048 | D/S/K占比10/60/30、50/30/20、90/5/5 | 剩余行配对、运动判断、身份排列和输出 |
| character | 128/512/2048 | 16/64根每角色 × 聚集／交错 | 既有键桶准备、组对、身份去重和输出 |
| bitmap-space | 512/2048/8192 | 稀疏／均匀／密集 × 单向／双方；mask位数分别1/4/1 | 相同树和摘要布局的节点证明、叶子与输出 |
| character-space | 512/2048/8192 | 密集输入上的聚集／交错角色 | 键摘要、完整覆盖证明、叶子与输出 |
| complete | 512/2048/8192 | 稀疏／均匀／密集，运动＋双方类别＋角色排除 | 完整公开关系查询；另验配对与实际消费的完整帧 |
| updates | 512/2048/8192 | 稀疏／均匀／密集，固定八帧变化 | 维护加查询；数据写入至消费完成的完整帧 |
| threads | 512/2048/8192 | 三种密度 × 0worker-inline／4worker-parallel | 外层任务提交至全部计数／前缀／填充完成 |
| consume | 512/2048/8192 | 稀疏／均匀／密集，完整三条件 | 直接公开查询至真实几何消费完成 |

共108个独立场景，每个分别提交公共队列。静态warm测量由外层范围持有一份真实MeasurementData：World、WorldJobs/Scheduler、成员查询、实体与候选数组、pair/contact工作数组在双方串行执行中复用。双方各自构造具体查询、树、摘要、观察者、计数状态及成对作业输出资源，按原E/H、H/E次序完成完整实际查询与消费。持续更新的双方各持真实MeasurementData，分别执行其构建、变化与维护；每个cold样本同样独立构建。完整手写查询直接进入 `HandwrittenSpatialCollision.Filtered`，保留其独立节点／证明／叶子循环；手写维护直接展开观察、版本和父链路由。共同具体内核包括 ECS 查询、树基础操作、摘要数组更新、分区、count/prefix/fill、子任务调度及接触消费。

每份新报告的DataSharing明确记录SharedStaticWorkspace或IndependentUpdatingWorkspaces。Workspaces按实际对象逐个保存使用方、一次构造ticks和已知数组payload/capacity；静态共同工作区只列一次，每侧树、摘要、分区、CollisionPairBuffer与SpatialContactTotals在侧内存中各列其实际资源。World内部管理结构及对象头继续由真实分配、Managed与WorkingSet记录。cold的SetupTicks直接包围该次独立MeasurementData构造；IndexBuildTicks直接包围随后的该侧查询/树/摘要/资源构造，两个真实边界分开保存。

输入采用 seed=231243 的固定整数 LCG 产生候选端点，保留候选位置及重复。类别域为8位；类别为一个已声明位，允许掩码为旋转的1/4/8位集合，实际通过率记录输出行数。75%成员具有Hair，其余成员保留普通物体语义。角色数量为 `ceil(N*75%/每角色根数)`，随规模增长；角色身份先于Body按固定顺序创建。

所有 AABB 边长为1。稀疏输入为间隔3的双物体簇；均匀输入为间距0.8的32×32格点层；密集输入为64物体簇，簇间距4，簇内y/z偏移0.03。同角色聚集按连续成员分组；交错按角色数取余。组合场景使用D/S/K=50/30/20、允许mask4位、每角色16根。

持续更新每帧改变10%位置、2%类别与运动类型、2%角色键、1%成员；固定八帧周期。成员变化按三组执行标签加入／移除、Body加入／移除、销毁／创建。写入前取得系统版本，维护各自读取/接受变更并推进成功基线。

Release NativeAOT 为必验后端，JIT 独立记录。每轮双方各执行8帧，共31轮；每个frame立即依次执行双方，round顺序E/H、H/E交替，因此SideSwitchIntervalFrames=1。预热16帧、冷初始化7对、每帧实际调用一次，FramesPerSample=1。最小场景仍执行真实 World/查询和成组候选。分区容量16，实际分区数随树记录。Scheduler采用Fast，双方参数一致。

JIT在同一Release输出目录以PublishAot=false、SelfContained=false及实际SourceRevisionId构建普通托管入口，采用.NET10默认TieredCompilation/ReadyToRun配置，环境项记录为default；双方在同一进程使用相同配置和上述固定预热/采样。JIT报告独立记录Backend、实际命令、源码、原始样本和阶段，NativeAOT验收使用其独立报告。

测量前先执行第一侧Query与Consume，立即保存其完整Entity身份行和真实contact/depth值，再执行第二侧并逐项核对；这些物化与比较均在Verify阶段完成。持续更新核对覆盖完整八帧周期。原始记录包含 ticks、先后顺序、输出数、接触次数／深度、全线程与当前线程分配、GC、Managed/WorkingSet。median及R-7 p95按原始精度计算，分别以扩展/手写≤1.00判断主场景及表中额外要求的完整帧。融合的遍历、筛选和输出属于同一个Query边界。

机器配置版的allocation起始计数位于before-Managed/WorkingSet/CPU观察之后，结束计数紧跟实际frame工作并先于after观察，记录当前及全部线程的实际工作分配。r24及以前系列的FrameAllocatedBytes同时包含before-WorkingSet观察的分配；其原始值和原scope随历史系列保留。elapsed查询/整帧时钟及31×8采样边界继续按原定义执行。

内存按实际值数组payload记录Live、Addressed与Capacity；Tree.NodeCount为包含复用地址的范围，存活二叉拓扑由LeafCount形成。叶映射单列有效键值payload及entry容量，分区四数组、访问数组、输出容量、接触累计、维护访问与已有ECS键桶分列。对象头、字典管理、查询／观察者／Scheduler和租用句柄同时进入真实分配与进程内存指标。

原始报告、每次优化前后结果及公共队列 result/stdout/stderr 保留并由最终证据索引链接。实际启动采用新进程独立观察；入口前的元数据初始化计入进程到首结果，Main和首次公共查询分别使用自身真实边界。后续源码优化完成后刷新同一八包feed/cache及实际包NativeAOT消费方。

controlled-fixed-r26及以前系列保留其双方独立World/数组的原始配置；共享工作区系列使用相同输入值、算法、输出工作和31×8采样，另行记录此数据持有调整。该调整将实际只读输入与工作地址对齐，按完整新系列判断每项原精度结果。

controlled-shared-r27保留原先round内E×8/H×8的块顺序。该轮实际连续高尾包括middle mask4双方许可的round17八帧、large mask4单向许可的round27七帧；后继统一按round→frame→双方配对，使相邻的双方实际帧处于同一时间窗口。单帧时钟、observer、样本数、输入和严格阈值沿原范围，原始块顺序报告继续保存自己的判定。新case的Input显式保存SideSwitchIntervalFrames与FramesPerSample。

完整系列的提交准备本身作为公共队列独占任务执行，直接读取同一固定列表，并用显式Session将全部独立case及collect/export/report按FIFO提交后退出。实际测量在准备任务结束后开始；报告计算与读取按预排CPU任务执行。queue-submission.json记录准备起止、源码、session和报告任务，逐case队列记录继续保存实际起止。此执行控制保持上述输入、采样、预热和双侧实现配置。

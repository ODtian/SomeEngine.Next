# Job 调度路径预算

分析时间：2026-10-22 22:50 +08:00。状态：S-E/S-W/S-B 为源码计数；D 同步接口、领取寿命和摘要完成为设计预算。

分析方法：按 EasyEcs 的 World.Parallel/System 调度与当前 SomeEngine.Job 的提交、依赖、队列、执行、完成源码展开；ECS 范围内核与整个公共调度入口分层核算，口径见 [[Cost-Model]]。

## 数据与核心入口

ECS QueryRanges 持有本次工作范围；每项 `{Chunk 引用, FilterColumns 引用, Start int, End int}` 共 24 B。SystemRunner 从查询生成范围，按批次提交访问类型和实际范围。Job 持有工作、队列、依赖、资源访问与完成状态。

核心入口：Execute（当前线程）、ExecuteParallel（同步完成）、Schedule/ScheduleParallel（显式完成 handle）、Combine、Complete。维护关系使用显式任务依赖，结构命令使用 [[Commands|producer 分段与回放]]。依赖图、范围资源冲突与完成状态各有实际消费者。

Job后端在线程缓存中复用空闲工作线程，空闲数量以JobRuntimeConfig.DefaultWorkerCount为上限。WorkQueue继续持有本次队列、范围和完成状态；构造时为各worker编号分配线程并启动本次循环。循环先从线程对象取走队列引用，清除该对象上的引用，再设置本次线程局部编号及计数。Dispose排空工作、发布结束条件并唤醒worker，等待各循环返回后归还线程；超出空闲上限的线程结束并释放信号。新线程沿UnsafeStart启动，Job的追踪与访问上下文继续在实际工作入口登记。首次建立线程与两个信号的分配进入实际首次费用，缓存命中计入绑定、唤醒、等待和归还费用。

## 等价配对

| 能力 | EasyEcs 参照 | SomeEngine 方案 |
|---|---|---|
| 同步单线程查询 | 原生 ForEach | 直接行/Chunk 内核 |
| 同步并行查询 | 原生 ForEachParallel，调用返回时完成 | ExecuteParallel；完整队列和等待入账 |
| 异步 handle、显式依赖 | E*：同一 Job 后端调度 EasyEcs 查询工作 | Schedule/ScheduleParallel + 同一 Job 后端 |
| 系统静态冲突分组 | S-E SystemSchedule.Publish + RunStage | SystemGroup 的声明/改动路径与每次执行分开 |
| 挂接子任务、范围资源和失败传播 | D-E：声明同一访问与依赖，使用同一外部 Job | 原生 Job 对应能力 |

同步行遍历包含查询冻结/解冻、追踪与实际行处理。异步与静态 stage 的完成语义分别对照。

## 源码边界

S-E：[ArityGenerator.cs:964–1015](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Generator/ArityGenerator.cs#L964)、[World.Parallel.cs:65–99、205–307](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Parallel.cs#L205)、[World.Systems.cs:263–289](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Systems.cs#L263)。

S-W：当前 [Scheduler.cs](../../../../src/SomeEngine.Job/Scheduler.cs)、[Scheduler.Lifetime.cs](../../../../src/SomeEngine.Job/Scheduler.Lifetime.cs)、[JobWorkStreams.cs](../../../../src/SomeEngine.Job/JobWorkStreams.cs)、[WorkQueue.cs](../../../../src/SomeEngine.Job/WorkQueue.cs)、[Scheduler.ExecutionStore.cs](../../../../src/SomeEngine.Job/Scheduler.ExecutionStore.cs)、[Scheduler.CompletionStore.cs](../../../../src/SomeEngine.Job/Scheduler.CompletionStore.cs)、[ResourceManager.Access.cs](../../../../src/SomeEngine.Job/ResourceManager.Access.cs)。S-W 行号对应分析日期工作树。

## 同步调度的候选与数据归属

固定 R 个可动态领取的范围、调用者参与、h 个 helper、返回时全部完成、异常传播和共享 Job worker。同步返回与异步 handle 的观察语义各成一组。

| 候选 D | 每批固定状态与分配 | 每 R 个范围领取/完成 | 完成与回收 |
|---|---|---|---|
| 同步入口包装现有通用 ScheduleParallel+Complete | S-W state Reset W18、Release W17；group 写7/清6；h 个引用增加、h+1 次释放；热 Pool.Push L1 | 原子领取 R+e；q 次 CompleteItems 原子；执行上下文字段 W4q；实际队列/重排各次入账 | state/work 四次 H、多个 state 锁、free stack；现有等待流程 |
| Job 提供同步范围批次 | 六字段初始化；线程局部池 Rent/Return 共 W4；结束清字段/终止 epoch W4；热 L0 | 每有效范围 CAS1+Decrement1，合计 2R；失败 CAS f；排队/取票原子 2h | caller 等 Remaining，直接归还数组池；完整同步/队列式见下文 |
| 每 helper 固定连续区间 | 初始化 h+1 个区间；每区间两个 int 和 Job 值，数据写 h+1 项 | 每参与者完成一次原子，共 h+1；领取原子0 | 等待与第二项相同；动态负载均衡改为固定分区 |

选定第二项作为 ExecuteParallel；异步 Schedule/依赖/子工作继续使用通用 Job 状态，按相同后端的 E* 比较。固定分区作为显式等长工作配置，按相同固定分区能力配对。这样每份完成状态都有对应请求：同步批次持有自己的 Remaining，异步节点持有可等待 handle 与依赖。

`SomeEngine.Job` 持有 worker、队列、批次对象和同步；ECS 持有唯一 QueryRange[]，借给本次批次读取。同步批次保存 Work、Items、Count、Remaining、Error、EpochClaims；一次初始化6写。闭合类型的 Execute 入口指针在批次对象创建时写一次，随后只读。EpochClaims的高32位为epoch；R≤32时低32位为已领取位图，R>32时为下一个范围编号。发布时低位置1，将范围0交给caller；其余范围继续动态领取。结束先使旧epoch失效，再清Work/Items/Error，W4。同步票直接进入现有MPMC/local deque，worker取得票后通过同一控制字领取，再访问该批范围。

共享ReadyTicket为Data引用8 B与metadata 8 B，共16 B；Data的实际类型区分WorkStream与同步批次，每张票沿该类型分派一次。关闭统计时，同步票的metadata保存完整uint epoch。开启统计时，每次实际发布helper建立一个40 B TimedRangeBatch对象，保存8 B只读执行指针、原批次引用与uint epoch，票的metadata继续保存完整64位时间戳；所有helper共用该不可变对象。其费用为L1、构造W3、每张票一次额外间接转发，实际延迟采样与计数另计。关闭统计、容量复用的同步票沿值复制，热L0。

取票过程已经取得本线程的workerIndex，同步执行入口、Timed包装及typed Drain沿参数透传该值；每张helper票复用这次定位。执行指针仍为8 B，票及统计包装的布局保持上述预算。

三个优先级的injector与每worker的local deque沿首次实际Push建立，以CAS发布，读方以Volatile.Read取得当前实例。扫描按原优先级处理已发布队列，overflow与容量计数沿原协议。512配置下每个injector数组为32 KiB、每个local deque为8 KiB；3 worker原生根提交仅使用Normal injector时，数组数据为32768 B，相比全部建立的172032 B减少139264 B。对象、数组头及首次发布竞争产生的候选分配分别计入，实际逻辑容量保持配置值。

## ECS 侧的范围成本

| 路径 | S-E | D |
|---|---|---|
| 产生 R 个范围 | 四数组：table 引用、16 B RowFilter、start、end；4R 个元素写，32R B | 一个范围结构数组；R 个结构写，24R B |
| 取一个范围并进入行内核 | 四数组分别读取 table/filter/start/end，A4；各基址、table及过滤引用按实际生成方法计 | D QueryRange结构项 A1；Chunk/Filter字段各P1；World来自批次；实际值列绑定见[[Storage-and-Queries]] |
| 范围容量 c→c' | L4，copy(32c)，发布 W4 | L1，copy(24c)，发布 W1 |
| 结束清引用 | Zero_TableRef(c_tables)+Zero_RowFilter(c_filters)，按各自容量 | 绑定范围P1+Zero_QueryRange(R)+Count W1；清24R B，Kzero控制另计；数组复用 |
| 访问声明 | 静态类型访问与 stage 分组 | k 项声明写 k 个结构；按提交批次建立一次 |

范围项读取与列/行绑定分别计数；下文调度内核只承担领取与执行入口，实际查询工作 U 包含范围内的列绑定和行工作。

## S-E 同步并行的完整路径

定义 R 为实际工作范围，h 为成功排队 helper 数，k 为检查的 helper 数，f 为领取失败的 CAS 次数，t 为最终退出领取循环次数，s 为等待轮询次数。实际用户行内核为 U。全部变量是执行次数或输入规模。

1. `WorkOf<T>`：读取工作目录、取缓存对象，A1，范围/类型判断 B2；冷路径持锁、复制目录并创建工作对象。
2. `TryRunChunks`：helper 数判断、runner 判空；热路径进入 TryRun。
3. `TryRun`：busy CAS 一次；成功后 Run 写 `_work/_count/_error/_remaining/_spent` 五项，再 Exchange 发布 epoch 一次。
4. `Wide/Took`：自适应调度状态的判断与均值更新。Wide 在当前并行模式检查 `_single` 一次；Took 更新 wide/work 两个均值，按实际探测分支更新 period/run 等状态。该功能属于参照完整能力。
5. helper 检查 k 次：每次 CAS Queued 一次；h 个成功项写 Epoch，调用 ThreadPool.UnsafeQueueUserWorkItem h 次。每个成功项按源码重新取 helper 数组三次，A3/P6，再加循环长度检查的数组读取；被跳过项仍承担自己的 CAS、对象取得与判断。
6. 领取 R 个范围：cursor CAS 共 R+f 次；Volatile cursor 读 R+f+t 次；有效领取路径的两个范围/epoch 条件与 CAS 结果条件共 B3，每个终止尝试执行相应终止条件。每范围实际 Run 分派一次并执行 U。
7. 每范围固定读取时间戳两次、Interlocked.Add(spent) 一次、Interlocked.Decrement(remaining) 一次。因此领取及完成至少 **3R+f 次原子操作**；忙状态与 epoch 另加 2，helper CAS 另加 k。开始时间戳一次，采样末尾再一次。
8. caller 轮询 remaining，Volatile 读 s+1 次、循环 B(s+1)、SpinOnce s 次；采样分支、异常状态判断各一次；结束清 work 并 Volatile 写 busy。

热复用时 ECS/Runner 自身 L0；线程池的队列容量、首次线程与系统唤醒由 `TP(h,capacity,parked)` 计入。其调用次数固定为 h，实际系统调用取决于已唤醒线程数。线程池成本采用同版本 BCL 子过程，和自有 Job 队列分别列出。

## S-W 的完整提交、队列、执行、完成

`ScheduleParallel` 全路径为：EnterSubmission → ValidateDependency/BeginParallelSchedule → submission observer 登记 → batch/token 数计算 → CreateState → 资源登记/预留 → 挂接 parent → Bind → WorkStream 查找 → group.Rent → token 生成/发布或依赖登记 → worker 取 ticket → Drain → ExecuteStreamItem → 领取范围 → 完成 work → 释放资源/依赖 → 完成 state → 等待方观察 → 回收。

以下函数级数字均为 S-W 的明确路径：有效代数、成功、追踪与计数关闭、已复用容量、无 parent/依赖/资源的普通并行批次。BCL 锁、队列、栈和线程的内部另列调用。

| 子过程 | 已展开的常数/数据工作 | 外部同步与分配 |
|---|---|---|
| 进入/退出提交 | shutdown gate 与当前执行者引用；提交数增加/减少 W2；根提交路径条件预算 B5 | monitor 进入/退出 2 组；末次提交 PulseAll 1 |
| WorkStream 查找 | 每批 ConcurrentDictionary<Type,WorkStream>.GetOrAdd 1 次，类型键比较与命中链计入 | 首次类型创建 WorkStream 与字典节点 |
| `GetState`，记作 H | `_states` 范围读取、索引、对象取得：〈3,1,2,0,0〉 | L0 |
| `CreateState`，普通 free-stack 命中 | 取 free ID、state、调用 Reset；Reset **18 个字段写**；free stack pop 另 W2 | state pool 锁 1、state 锁 1；Reset 的五个列表清空检查与 Users 检查；容量复用 L0 |
| `ParallelJobGroup.Rent` | Pop 一次，写 job/length/batchSize/batchCount/nextBatch/references/measureCost，W7 | ConcurrentStack.TryPop 1；首次 group L1 |
| 发布 h 个 token | 每 token 复制 group/state，AddTokenReference 原子 h 次；调度方 ReleaseReference 原子 1 次 | ready queue 入队 h 次；group refs 修改计 W(h+1) |
| `EnqueueReadyMany` | 逻辑容量保留 CAS；每个 drainer 预留 CAS；ticket 发布维护 ready 数 | WorkStream ConcurrentQueue 入队；全局 injector 或本地 deque；实际重试次数单列 |
| 每个 token quantum | CanExecute 为 H+5B；设置/恢复两个线程上下文字段 W4；进入/退出执行上下文各一次 | trace 入口按实际关闭分支；令 q 为 quantum 执行次数 |
| 每次 quantum 内领取 | 领取 R 个 range，加 e 次越界领取；原子 R+e；ClaimQuantum=16 | 每 range 复制 Job 值一次，共 R·sizeof(Job) B；测量关闭时时间戳 0 |
| 每次 quantum 完成 | CompleteItems 为 H+7B、原子 PendingWork.Add 一次、W1 | 共 q 次；最后完成者进入以下释放链 |
| 最后释放 work | BeginWork/EndWork 各一次：2H，加 P4/B12/W4 | state 锁 2；开启资源/依赖时另加对应释放 |
| 最后完成 state | BeginDispatch/EndDispatch 各一次：2H，加 P6/B17/W3 | state 锁 2；完成队列唤醒与继续任务另计 |
| 成功 state 归还 | Release 写 **17 个字段**；free stack Push 一次 | pool 锁与 state 锁各 1；Push 容量变化另计 |
| token/group 归还 | h 次 ReleaseReference 原子；最后一个清 group 六字段 W6，Pool.Push 1 | **Pool.Push 每批 L1** |
| Complete 等待 | TryBeginWait 一次；w 次完成轮询；可执行其他工作 x 次；EndWait 一次 | 等待者计数增减 W2；活动等待各端 state 锁；park/pulse 按实际次数 |

H 内的 B2 为代数与目录范围条件。具体 gate 对象取得、调用者 `_execution/_completion` 接口引用和调用结果判断在相应阶段入账；上表以函数内核边界列出，跨阶段复用时保留各次 H。完整总量为各阶段向量之和，加下表实际队列/资源/依赖次数；R、q、h、e、重试与等待次数进入公式。

仅 state Reset/Release 已有 W35，且最后 work/state 释放重复四次 H；这部分与范围数组优化独立。每批 group 分配来自 [JobWorkStreams.cs:557–568](../../../../src/SomeEngine.Job/JobWorkStreams.cs) 的 Pool.Push；[.NET 10 ConcurrentStack.cs:273–280](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Collections.Concurrent/src/System/Collections/Concurrent/ConcurrentStack.cs#L273) 每次 Push 执行 `new Node(item)`。池命中表示复用 group，入池节点仍分配一次。

## 队列内核与竞争

以下 S-W 数字对应源码中的基址/数组/状态操作，原子写计入 W，原子次数再次单列。VR/VW 为 Volatile 读/写次数。

| 无竞争内核 | P | A | B | W | L | 原子/屏障 |
|---|---:|---:|---:|---:|---:|---|
| MpmcInjector.TryEnqueue 成功 | 1 | 1 | 2 | 3 | 0 | CAS1、VR2、VW1 |
| MpmcInjector.TryDequeue 成功 | 2 | 1 | 2 | 3 | 0 | CAS1、VR2、VW1 |
| ChaseLevDeque.TryPush 成功 | 2 | 1 | 1 | 2 | 0 | VR2、VW1 |
| ChaseLevDeque.TryPop，多于一项 | 1 | 1 | 2 | 1 | 0 | VR2、VW1、屏障1 |
| ChaseLevDeque.TryPop，最后一项成功 | 1 | 1 | 3 | 3 | 0 | CAS1、VR2、VW2、屏障1 |
| ChaseLevDeque.TrySteal，首试成功 | 1 | 1 | 3 | 1 | 0 | CAS1、VR2、屏障1 |

Injector 每次失败 CAS 再执行该次基址/元素访问、两项判断、VR2 和 CAS1；成功写与发布只执行一次。TrySteal 每次尝试增加一次循环、空队列和 CAS 结果判断；源码最多尝试四次。完整取票还遍历优先级与 worker 目录：扫描 a 个优先级、b 个 worker 时分别增加 a/b 次目录元素访问与循环条件。readyItems 的 ConcurrentQueue 入/出队及 group ConcurrentStack 操作使用 BCL 子过程，调用次数由 token 入队/重排和归还数量确定。

WorkQueue 每次成功 drainer 额外修改 readyTicketCount、activeDrainerCount 各一次，结束修改 activeDrainerCount 一次；保留/释放逻辑容量各一次；BeginDrain 修改 scheduledDrainers 一次。仍有任务时重排 ticket，重新承担发布路径。Completion.Pulse 另修改 wakeEpoch 一次并进入 state monitor。

## D 同步批次：完整逐步向量

以下先列R>32的游标领取内核：h张票通过MPMC环，热池/容量命中，root同步调用，caller直接处理已预留的范围0。f为失败CAS次数，t为同epoch的范围耗尽退出次数，z为旧epoch退出次数，s为等待轮询次数。根生命周期热检查为进入时Open判断、退出时最后提交者判断；锁的BCL内部工作另计。

| 步骤D | 〈P,A,B,W,L〉 | 原子与实际调用 |
|---|---|---|
| 空范围及领取类型选择 | 〈0,0,2,0,0〉 | 空范围一次判断；非空再选择位图或游标闭合类型 |
| root提交进入/退出 | 〈2,0,2,2,0〉 | lifecycle monitor进入/退出各2次，最后提交者PulseAll |
| 批次池Rent+Return | 〈3,3,4,4,0〉 | 线程局部数组池 |
| 初始化/清理 | 〈0,0,0,10,0〉 | 两次epoch Exchange；五个载荷字段初写，末尾清Work/Items/Error |
| h次发布循环控制 | 〈0,0,h+1,0,0〉 | h按实际可用worker与R确定 |
| h张票入队 | 〈h,h,2h,3h,0〉 | CAS h、VR2h、VW h |
| h张票出队 | 〈2h,h,2h,3h,0〉 | CAS h、VR2h、VW h |
| 唤醒选择与票分派 | 〈2h,0,2h,0,0〉 | sleeping VR h；Batch引用/只读Drain入口各P1；分派I h |
| R次执行与其余R−1次领取 | 〈R,R,3(R−1),2R−1,0〉 | caller的0号范围随epoch发布；其余CAS R−1，全部Decrement R；领取VR R−1 |
| f次CAS失败 | 〈0,0,3f,0,0〉 | CAS f、VR f |
| t次耗尽/z次旧票退出 | 〈0,0,2t+z,0,0〉 | VR(t+z) |
| caller等待与结果 | 〈1,0,s+2,0,0〉 | Remaining VR(s+1)、Error VR1、SpinOnce s次 |

合计 **〈6+5h+R,3+2h+R,8+7h+3R+3f+2t+z+s,15+6h+2R,0〉**。原子为 **2R−1+f+2h+2**；Volatile读为R−1+f+t+z+5h+s+2，写2h。每范围复制Work值sizeof(TWork) B并进入闭合工作体一次。错误路径每个异常另加Capture与Error.CompareExchange，结束统一传播首个错误。

原型接入共享WorkQueue时，在上述领取/MPMC内核外加入队列外层账：h>0的批次执行容量保留CAS一次、ready计数增加一次、唤醒epoch增加一次；每张同步票在取出后减少ready，完成整个Drain调用后释放容量，共2h次原子。成功且无重试的附加原子为2h+3[h>0]，合并为2R+f+4h+1+3[h>0]；单工作项由caller处理范围0，初始化/终止epoch及完成计数共3次原子。容量CAS重试、实际唤醒锁、队列扫描和溢出按实际次数继续加入。MPMC票载荷入队复制16h B、出队复制16h B并清零16h B；local deque的push/pop/steal按实际16 B读写计。同步提交门持有caller及批次寿命，每张helper票的容量计数持有从排队到Drain返回的完整寿命，旧epoch票同样在finally释放。WorkStream另用activeStreamDrainerCount覆盖实际Drain、容量释放及重排完成；两种票沿各自完成点结束。同步类型首次沿批次和执行入口建立，后续票直接沿共享队列分派。

R≤32复用同一控制字存已领取位图。worker首次优先选择编号CurrentWorkerIndex+1，随后每次选择剩余位中的最低位；任一参与者都可继续领取剩余范围。每次先检查epoch，计算`available=~claims & (uint.MaxValue >> (32−R))`；有剩余时选择有效的优先位或`available & -available`，以TrailingZeroCount取得范围编号，再CAS发布`old | selected`。成功后读取载荷。caller的范围0在发布时已置位；每范围唯一领取及R次完成计数沿原协议执行。

位图与游标由两个闭合值类型提供领取内核，各自拥有同形状的批次池及只读入口，状态仍由同一批次持有。位图新增每次尝试的取反、掩码移位/与、最低位选择和TZCNT操作；实际首次worker优先选择及其CAS重试各加范围/可用位判断，worker编号读取与接口调用按实际helper次数计。普通补领使用已绑定的空优先位；位图数据共用原32位控制空间，热容量命中L0。两种内核的成功CAS均为R−1，完成Decrement为R，原子总式相同。

共享队列扫描先读取ready计数，VR1/B1，0票时直接完成空扫描。local deque的TryPop/TrySteal复用IsEmpty：额外VR2/B1，空队列沿此返回；非空时再进入上表原有pop/steal内核。该空路径保留底部位置并结束扫描，容量写入、底部写入和内存屏障均由实际取票路径承担。发布先增加ready再写票，消费者成功取票后减少ready；等待路径继续读取ready与wake epoch，按实际轮询和唤醒计费。

完整ECS入口还加入：本次访问取得/释放A(k,d)、查询目录/刷新、范围生成/清理、实际行计算U、实际摘要合并、Wake、队列满/竞争/扫描。A(k,d)按下文同一Job资源表的k项访问与d条依赖展开；已经在系统访问范围中的调用直接复用。队列入出发生h次，延迟到达的票仍执行控制字段检查。

S-E领取/完成及busy/epoch/helper为3R+f+2+k次原子；其中每范围spent累计1次、时间戳2次。D关闭采样时的领取/MPMC内核为2R−1+f+2h+2，双方开启相同采样后分别加入实际时间戳/累计。S-E的ThreadPool入队h次，D的MPMC入出各h次；两种后端的唤醒和容量分别入账。[ThreadPoolWorkQueue源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/ThreadPoolWorkQueue.cs)提供对应BCL子过程。

票分派读取的Drain入口在typed批次对象建立时固定。Items/Work属于成功领取后的载荷读取；耗尽和旧票只访问控制字段。此顺序同时决定P计数和批次寿命。

## 资源、依赖与系统图

资源声明 k 项：S-W Enter tracker 遍历各项并查 observer；RegisterAccesses 持资源锁，逐项 Resolve；第二次遍历查询冲突；第三次遍历追加 ActiveAccess 与 frontier。内联 AccessBuilder 容量 4，前四项是字段写；第五项 rent 数组并复制前四项，再写第五项。每个内联 Get 包含范围判断、容量分支与字段选择；数组路径 Get 额外 P1/A1。

命中 d 个前驱：每个前驱调用 AddPending（H、state 锁、依赖计数 W1）、AddContinuation（H、前驱 state 锁、List append），再 Seal 一次（H、state 锁、封闭字段 W1）。完成时每个实际后继执行 ReleaseDependency（H、state 锁、计数 W1、就绪/失败分支），最后一个进入相应入队路径。继续项的 ToArray 在实际非空列表上分配 L1 并复制 d 项；清空列表按引用宽度处理。显式依赖已经完成时只检查状态；defer resource 的路径另持 Reservation，前驱完成后 Activate，再发布其资源依赖。

资源恢复的派生状态：≤4 项直接从 ActiveAccesses 删除并维护 frontier；更多项先构建实际资源集合，再对每个触及资源移除本 handle 的访问并重建其紧密 frontier。r 个无范围读前驱逐项读取；区间查询访问实际索引节点数 v，计 v 次节点取得、对应范围比较、输出 d 个依赖；范围集合插入/删除与摘要更新按该索引路径逐节点写。D-E 使用同一后端和相同声明时承担同一套费用。

S-E 系统结构变更时 Publish：Sort、levels[n]、n(n-1)/2 个候选系统对检查、按 level 再扫描 n 个系统、构造各 stage 数组与对象；热执行只读已建 stage。D 的系统声明同样在系统集合/访问声明改变时建立依赖，热路径读取持久图。动态资源依赖仍在实际提交时登记，静态图与资源表分别表达顺序与当前冲突。

### 异步候选的完整费用与缓存

Schedule 的依赖图/资源能力固定为同一个 SomeEngine.Job 后端；E* 调度 EasyEcs 范围时同样经过该后端。对照式为：QueryRanges + EnterSubmission + 类型 WorkStream 查找 + CreateState + k 项资源访问 + d 个依赖挂接 + token 发布 + 每次 quantum/执行 + 完成释放 + Complete + 回收。各阶段的 S-W H、字段写、原子、队列次数均按上表计，实际 d/k/q/e 输入相同。

每个依赖的共同控制内核：AddPending H、AddContinuation H、ReleaseDependency H，合计3H=〈9,3,6,0,0〉；再加三次 state 锁、计数增减 W2、continuation List.Add 的 Items P1/A1/B1/W3、就绪/失败判断。Seal 每批 H、state 锁、封闭标记 W1；实际非空 successor 复制为 L1+Copy_Handle(d)。已完成依赖只执行状态 H 与对应完成判断。

资源 k 项的主表由 Job 持有：声明记录 k 次值写；内联容量4时字段读取/选择逐项计，溢出时数组 Rent+Copy_Access(4)。三个实际扫描分别 k 次 Resolve、k 次冲突查询、k 次 ActiveAccess/frontier 追加；循环各 B(k+1)。每个资源索引访问的 v 个节点计 A v，类型/区间条件逐节点，输出 d 个依赖再支付前一段；结束按实际触及资源重新维护 frontier。与 E* 使用相同后端、相同资源输入时，这整段是同一源码子过程，调用次数亦相同。

系统声明变更时：n 个系统两两候选 n(n−1)/2，逐项比较实际访问集合，图节点/边只由 SystemGroup 持有。热运行读取既有 stage，A 为实际 stage/系统项数；系统/声明修改才重建。静态 stage 候选省每帧资源登记，动态资源提交提供运行时范围冲突能力，两种能力分开配置。

## 首次、扩容与外部依赖取舍

| 事件 | S-E | S-W / D 使用当前 Job |
|---|---|---|
| 首个并行 runner | Runner、Helper[]、h 个 Helper，L(h+2)，另工作对象/工作目录 | Scheduler/CompletionStore/ResourceManager/WorkQueue；worker 数与优先级决定队列矩阵 |
| 首个完成状态 | runner 字段复用 | CompletionState、Lock、四个 List，L6；状态目录在 Scheduler 建立时按上限分配 |
| 首个 WorkStream<T> | 泛型工作缓存对象 | WorkStream、Lock、slots List/数组、free Stack、优先级 queue 目录与计数数组；每优先级一个 ConcurrentQueue 及其初始段 |
| queue 扩容 | 线程池内部容量增长 | injector/deque 固定容量；满时进入 overflow ConcurrentQueue；每次新段分配及初始化计入 |
| 依赖等待项 | stage 预建 | slots 数组 rent；WorkStream 存储槽写入；就绪后再转 ready queue；完成归还数组 |
| 本线程首次批量 drain | 线程池内部 | TItem[至少64] L1，按 sizeof(TItem) 清零；结束清引用 |
| 组归还 | helper 长期复用 | ConcurrentStack 节点每批 L1 |
| worker 首建 | 线程池首次唤醒/建线程 | 每 worker Thread、闭包/委托及 native thread；优先级 deque 对象/数组分别分配 |
| 容量增长 | 四范围数组 + 工作目录 | 范围数组、free-state Stack、slots List、依赖 List、资源 builder、queue segment 分别增长与清零 |

当前通用后端用于同步调用时，字段写、锁和热分配多于EasyEcs Runner；24 B范围项改善适配层。本轮以同步范围批次替换该组合，整批D向量已经包含队列、epoch、等待和回收。

面向该目标的 D 外部需求为：Job 提供同步范围批次入口，继续共用自己的 worker/队列；同步批次直接持有范围领取与完成计数；异步节点只在请求 handle/依赖能力时建立；group 使用数组或侵入空闲链归还。拟议范围内核每范围领取/完成各一次原子，共 2R；批次 epoch 发布/终止另2次；队列、唤醒、提交生命周期与等待仍逐项计入。该入口涉及 `SomeEngine.Job` 的实现与接口变更，属于明确的外部依赖决策。

该选择从同步返回与异步依赖的实际消费者推导。ECS 持有一份查询范围，Job 持有一份调度状态；同步与异步入口分别承担所请求的完成能力。2R 是拟议领取/完成子内核预算；整条路径继续加队列与生命周期总账。

## D 同步方案的首次与增长总账

| 事件 | D 选定同步范围 | 通用异步后端 / S-E 对照 |
|---|---|---|
| 首个同步 Job 类型 | typed 批次对象 L1、只读执行入口W1；线程局部 free 目录 L1；ECS 范围数组 L1，清零24c B | S-E 工作对象 L1、Runner/Helper[]/h 个 Helper；异步 WorkStream 的对象/队列由原表列账 |
| 首次进程 Job worker | 共享 Job 后端启动一次；q 个优先级：injector 对象/64 B×容量数组各1；wq 个本地 deque 对象/16 B×容量数组各1；目录各1；每 worker 闭包/ThreadStart/Thread L3、native thread1 | S-W WorkQueue.cs:95–125 的实际构造；S-E 由 ThreadPool 首次创建对应线程 |
| 同步批次归还 | 数组池 Return〈1,1,2,2,0〉；热 L0 | 当前通用 group 的 ConcurrentStack.Push 热 L1 节点 |
| 范围增长 | Grow(QueryRange,c,c',R)，24 B/项 | S-E 四数组分别增长，共32 B/项 |
| free 目录增长 | Grow(ref,c,c',count) | 通用 ConcurrentStack 每 Push L1；S-E Helpers 长期保留 |
| ring 满 | 固定 ring 复用；实际 overflow 队列操作另计，新段对象+项数组各L1、清零其真实容量 | 当前 WorkQueue 的 overflow Queue 同样承担实际段增长 |
| 类型目录增长 | Grow(ref,c,c',c)；后续泛型类型绑定数组直接访问 | S-E CreateWork 复制完整目录；现有通用后端 ConcurrentDictionary 类型查找 |
| 已挂起依赖/资源首次 | 请求 Schedule 时建立实际 List/资源索引 | 同能力的 E* 调用同一后端与同一输入 |

选择结果：ECS 生成一份24 B范围数据，SomeEngine.Job 增加同步范围入口并共用原有 workers；异步能力保持由通用 Job 数据持有。整批同步公式与 Source 的原子/时钟/队列实际调用分别可核对。隔离Job原型已提供ExecuteRanges及同步批次票分派；目标预算、该源码实现和对应机器结果按各自固定版本归属。

本地deque容量复用JobRuntimeConfig.MaxQueuedWorkItems，取RoundUpPowerOfTwo(max(2,min(4096,MaxQueuedWorkItems)))。公共比较的512项配置、3个worker和3级优先级对应9个512项数组，共72 KiB票数据；目录与对象另计。首次构造按这份实际容量清零，满队列继续沿原injector/overflow通路。MPMC仍按原64 B cell与其配置容量计费。

## 外部接口与批次寿命

Job提供下面的同步范围接口；ECS传QueryRange[]，两层各自持有自己的数据。

```csharp
public interface IRangeWork<TItem>
{
    void Execute(in TItem item);
}

void ExecuteRanges<TWork, TItem>(
    TItem[] items, int count, in TWork work)
    where TWork : struct, IRangeWork<TItem>;

void ExecuteRanges<TWork, TItem>(
    TItem[] items, int count, in TWork work, JobHandle access)
    where TWork : struct, IRangeWork<TItem>;
```

Work/Items/Count/Remaining/Error是批次载荷；EpochClaims是高32位epoch、低32位领取状态的控制字。闭合类型Drain入口在对象首次构造时保存。票持Batch引用、预期epoch和种类。每个TWork/TItem/领取内核使用自己的线程局部批次池。

初始化先写五个载荷字段，Remaining=count；再以Interlocked.Exchange发布(epoch,1)，caller持有范围0。票在这之后入队。caller先执行0号，再沿同一领取循环补领；helper沿下面循环进入：

```text
Drain(batch, expectedEpoch, firstPreference):
    loop:
        old = Volatile.Read(batch.EpochClaims)
        if epoch(old) != expectedEpoch: return
        if SelectClaim(old, batch.Count, firstPreference) is empty: return
        (index, next) = selected claim
        if CAS(batch.EpochClaims, next, old) != old: continue
        firstPreference = none
        try:
            work = copy(batch.Work)
            items = batch.Items
            work.Execute(in items[index])
        catch error:
            CompareExchange(batch.Error, Capture(error), null)
        finally:
            Interlocked.Decrement(batch.Remaining)
```

caller的0号范围随epoch发布取得载荷寿命；其余范围由成功CAS取得。调用方以acquire读观察Remaining=0，此时全部范围的执行、异常记录和最后一次载荷访问均已结束。随后读取Error、Exchange推进epoch，再清Work/Items/Error并归还批次。Drain在Decrement之后只回到控制字检查；延迟票凭旧epoch退出。Count是控制判断输入，helper的实际载荷访问由成功CAS保护。

Error写入先于Decrement，最后的acquire读包含所有已完成工作。发生异常的范围同样完成一次计数，其余范围继续领取，调用方等全部范围结束后传播结果。ECS仍持有自己的范围数组，在摘要和访问释放完成后清理引用并归还。

同步嵌套ExecuteRanges由当前调用栈完整返回，内层调用方参加领取。带handle、异步子任务或scope完成语义的工作使用通用Schedule状态；这些状态的费用沿S-W完整链路计算。同步范围入口与通用scope各自对应明确的完成消费者。

同步World的ExecuteParallel持有一次Core结构读取范围，生成的方法使用已分区的行数据，调用第一个重载并同步完成。连接Job的World使用第二个重载传入已取得的访问handle。原型的两种入口均保存/清空Access各W1，此两写在Context项单列。caller随epoch发布取得范围0，helper在首次CAS成功后取得范围。两者都保存/恢复Scheduler并进入/退出JobExecutionContext；有access时保存、设置与恢复ScopeToken，空access沿原ScopeToken字段继续执行。s_currentScope的内部写入点都按执行范围成对恢复，此路径复用该不变量。

设p为进入载荷的次数、a为其中非空access次数，费用为**Context(p,a)=〈p,0,4p,4p+2a+2,0〉**。P为旧Scheduler读取，B为两次changeScope分支及Depth的checked加法/平衡检查，W为Scheduler与Depth各2p、Scope两写2a、批次Access两写。Index比较与局部初始化另列算术。JobHandle/ScopeToken各24 B，Access读取24p B，Scope保存24a B、设置/恢复48a B，批次Access保存/清空48 B；结构内引用沿整项复制计。同步Core与连接Job均加入该式，连接ECS的逐范围上下文再按RangeContext(R)加入；访问状态及生成行范围继续按原声明执行。

## 并行变化摘要的完成阶段

64行词是追踪写入的最小分区，同一列的一个词由一个范围写入。最后不足64行的词归同一个范围。worker写自己的行版本/WordChanges；完成方按本批已知词区间合并BlockChanges与LastWrite。

本批写版本在取得全部访问、确认至少一个匹配写范围后获得一次。一个Word的更新为：若Latest等于本批版本则OR该行位；否则把旧Latest写OtherLatest，以本批版本替换Latest并初始化Rows位。worker直接写的行和词各有唯一范围，块和列由完成方持有。

一个已绑定列扫描V个词，U个词的Latest等于本批版本，b个块首次提高到该版本。合并时绑定Words/Blocks各一次，每词读一次，改变词定位所属块：

```text
for each word in 本批覆盖的合并词区间:
    if word.Latest == writeVersion:
        block = blocks[wordIndex / 64]
        if block.Latest != writeVersion:
            block.OtherLatest = block.Latest
            block.Latest = writeVersion
            block.Words = bit(wordIndex)
        else:
            block.Words |= bit(wordIndex)
if any changed word:
    column.LastWrite = writeVersion
```

U>0时合并为 **〈2,V+U,2V+U+1,U+2b+1,0〉**，加变化头定位〈1,1,0,0,0〉。B为V+1次循环、V次版本比较、U次块版本比较；最后any判断另B1，故带该判断的完整列合并为 **〈2,V+U,2V+U+2,U+2b+1,0〉**。W包括b块各3写、其余U−b次OR及LastWrite一次。U=0沿同一词扫描/any判断结束。

同一Chunk/Type的独立追踪写批次取得同一个摘要写资源，版本分配发生在资源依赖完成后，因此它们按顺序合并。批内保持词级并行。Changed读取声明该摘要读资源；其工作依赖相应批次的摘要合并完成。Added使用相同行所有权与自己的行版本字段。

工作异常时同样合并实际已写词，随后释放访问、清本批暂存命令并传播异常。正常完成顺序为：行工作结束→摘要合并→工作资源释放→命令回放→显式关系维护→系统成功基线更新。工作释放事件和系统最终handle分别服务资源复用与业务依赖。

## Schedule的绑定、访问与发布协议

World的同步入口和Job入口共享存储内核。同步World由调用范围固定结构；连接Job后的公开入口通过同一个Job资源表取得访问。范围内传递已经绑定的数据和访问凭据，行循环执行实际存储操作。

源码已有[AcquireSynchronousAccess](../../../../src/SomeEngine.Job/Scheduler.SynchronousAccess.cs)、[RegisterResolvedAccesses](../../../../src/SomeEngine.Job/ResourceManager.Access.cs)、[显式依赖后的资源激活](../../../../src/SomeEngine.Job/Scheduler.Dependency.cs)。D在这个后端增加“取得结构后生成实际范围，再登记值访问”的准备阶段，具体顺序如下。

| 阶段 | 读取/写入的数据 | 进入下一阶段的条件 |
|---|---|---|
| 提交 | 查询描述、访问类型、显式依赖；World连接使用计数+1 | 创建一个可等待的工作状态，准备屏障保留 |
| 等待显式依赖 | Job依赖状态 | 所有显式依赖成功完成 |
| 取得结构读 | 注册World结构读访问，等待此前结构写释放 | 表集合、实体行位置、列数组固定 |
| 构造候选范围 | 刷新查询表匹配；读取Chunk.Count；生成全部表级候选范围 | 只按布局形成候选范围 |
| 登记全部值访问 | 在一次ResourceManager锁内查询各资源旧frontier，再发布全部k项访问 | 所有值/过滤/摘要资源依赖完成 |
| 选择实际行 | 读取Enabled、Changed/Added、动态Shared条件 | 过滤结果与本批访问一致；有写工作时取得版本 |
| 执行与合并 | 领取范围、写值/词、合并块/列摘要 | 所有范围及本批摘要完成 |
| 工作释放 | 释放k项值访问及结构读、通知work-released、清范围、连接计数−1 | 后继资源工作可进入；系统后续阶段继续其handle链 |

结构写操作只取得World结构写资源，访问全部行/值/邻接；它先完成显式依赖，再登记这一个资源。普通值工作先结构读、再一次登记全部值资源。所有值工作持有相容的结构读，值资源依赖沿登记先后形成；结构写等待结构读完成。这样资源图的顺序由实际资源表保持。

准备阶段保留一项内部依赖计数，结构访问和全部值访问登记完、实际依赖挂接后再Seal并释放该计数。工作在这之前保持等待。对跨阶段追加登记，Job持有两份访问记录并在同一work-released点释放；相同工作自身已持有的结构读在追加时直接复用。此准备回调和追加接口属于D的Job扩展。

公开同步访问也按结构→值两阶段取得访问：先用AcquireSynchronousAccess取得结构读，再经D的追加接口登记本次全部值访问并等待；结构写入口只登记结构写。两阶段复用同一工作状态，释放时一起完成。Read/Get返回ref、查询/索引/邻接返回视图时，调用者的显式范围持有访问到引用使用结束。现有Job上下文中的内部访问复用它已声明的资源。普通值拷贝入口在调用结束时释放。Core计数外的执行入口与访问费用由[[Cost-Model]]统一组合。

声明已知的同步入口复用内部同名AcquireSynchronousAccess(first,following)重载。资源表在同一锁内先登记结构首段，首段DependencyCount为0时才继续登记后续值；首段等待时仅交接该段，完成原等待后沿Append登记值。两份registration沿同一work-released点释放，登记异常显式释放已发布条目再取消状态；异常释放在资源锁内以完整Index/Version/Generation核对当前条目所属工作。单段入口沿原独立主体执行；新两阶段入口借用既有builder，另持48 B首段声明、约24 B结果及标量，L0，持久字段与容量保持原状。立即路径复用一次资源表锁并省去Append阶段维护；等待路径保持上述两阶段顺序。

首段为单项Read、可使用无范围frontier且不存在LastUnrangedWriter时，同一资源锁已能证明首段无依赖；待执行writer同样会设置该标志。合并后声明数落在既有4项内联容量内时，首段复用已Resolve状态与后续声明形成一次RegisterResolvedAccesses，持有一份registration，第二份为Empty；其余情况沿上述回退。命中时省一份registration池需求、一次状态登记锁及最终一次资源表释放锁，冷对象约少512 B，持久字段与容量保持原状；解析及提交失败仍沿原回滚/取消释放链完成。

普通单写类型查询复用WorldJobs既有资源目录中的只读声明。缓存域为writeAll=false、一个写类型、AccessTypes至多两项且包含该写类型，Shared/Group值过滤为空；宿主使用该写类型实际取得的Table资源项，结构写分类沿原展开路径。每组合保存固定QueryDefinition与内联AccessBuilder，至多四项声明。缓存命中以readonly字段的in引用进入既有Enter，结构先行、blocked等待、Require与最终释放保持原链；元数据展开、208 B builder初始化与Clear归独立冷方法。

冷路径成功Enter后借当前scope发布：沿既有资源目录锁重查组合，构造不可变节点并以Volatile发布；读取头使用Volatile，后继与声明均为readonly。发布异常先配对Exit，再清本地builder。资源handle和类型分类固定，Since留在Query值中，Clear/Restore继续复用；Release清资源目录时结束缓存持有。实际资源项A与查询/写类型组合C的新增持有为8A+240C B，P/V两类型一个组合为256 B，首次节点L1、热L0；具体类型过滤及更大声明沿原展开入口计费。

调用方已经给出一组连续结构修改时，以现有World.OpenStructureWrite覆盖整批。范围取得结构写资源后，内部Set/Add/Remove等公开类型入口沿嵌套访问深度复用同一许可；批次末统一释放。输入检查、实际迁移、填洞、位置缓存及变化版本继续由各类型入口完成。完整周期中的每个结构修改批次同时计入这一范围建立和释放成本。

Job准备状态使用四个阶段：显式依赖、结构访问、值访问、执行。每个等待阶段先把现有PendingDependencies置为1作为登记屏障，逐项挂接依赖后Seal并减去这一项；降到0的唯一完成者在既有state锁内切到下一阶段，再在锁外执行准备回调。前三阶段各有一次屏障建立/释放，共W6；阶段字段共四写。值阶段完成后才发布实际范围工作。所有已有值资源在一次登记中形成，准备期间只先持有结构读，因此等待顺序相同。

### 资源键

结构键只依赖World。值键统一为{World,存储种类,TypeId}；Chunk连续编号和行号编码为long位置：((long)chunkId << 32) + row，区间长度为实际行数。随机类型访问使用同一键的无范围访问，查询使用该键的具体区间，二者由现有ResourceManager匹配。摘要键为{World,摘要种类,TypeId}，区间为[chunkId,chunkId+1)。Chunk编号由ArchetypeStore分配，在World寿命内保持唯一。声明在批次外形成。

| 数据 | 资源与访问范围 | 完成点 |
|---|---|---|
| 表集合/行位置/列数组 | World结构Read；迁移、成员变化、增长使用结构Write | 工作摘要及引用使用结束 |
| 普通值 | 类型值键与编码后的Chunk行区间；随机类型访问覆盖该键全部区间 | 该工作release |
| 追踪行/词 | 相同值写范围，按64行词切分 | worker结束 |
| 追踪块/列摘要 | 类型摘要键与Chunk区间Write；Changed/Added为Read | 最后摘要合并 |
| 启用位 | 由结构范围固定；Enable/Disable在结构写范围执行 | 位、活动索引及版本一起完成 |
| Sparse值 | Sparse类型/实际紧密行区间；成员改变使用结构Write | 该工作release |
| 资源值 | World/资源TypeId的Read/Write | 该资源工作release |
| 关系与索引视图 | 结构Read固定成员；实际维护取得结构Write | 该视图使用结束 |

k包含结构之后实际登记的所有值与摘要项；范围资源的冲突按区间，摘要资源按实际列。过滤访问在读过滤数据之前登记，查询结果中的所有行复用已取得访问。

QueryDefinition首次建立时按TypeId升序收集Required、OptionalReads与Any的并集，持有一个AccessTypes数组。WorldJobs.EnterQuery沿这一数组登记本次实际访问，继续逐项处理写入、结构值、Changed和追踪规则。类型并集的扫描、List和最终数组分配进入首次定义费用，热入口按实际访问类型数遍历；资源键与当前World的资源登记仍由WorldJobs持有。

两/三列调度的BuildQueryAccesses与SystemGroup静态声明消费同一AccessTypes。显式T1写和T2/T3读沿原路径登记，其余Required/Any/Optional类型取得Read，结构类型由结构Read保持。动态声明按整类型覆盖或实际Chunk区间组合，静态声明在图重建时形成写后读依赖；AdditionalAccesses及Changed摘要沿原规则叠加。声明重建费用计入图建立，动态遍历与资源登记计入每次范围准备。

随机写范围同时声明该类型摘要Write，读取Changed/Added同时声明摘要Read。含引用普通值采用类型声明的复制/借用规则；内置Children只暴露只读目标视图，成员变更通过结构写入口。Buffer值写声明该buffer类型值键，扩容与元素span由同一借用范围约束。列绑定、随机访问和生成器使用同一资源键构造函数。

Enable/Disable、索引Set与拓扑提交在结构写阶段完成，正对应已确认的固定成员视图规则。仅写普通值的工作保留不同列/不同范围并发；追踪批次的列级摘要依赖作为确定成本计入。

### 查询缓存发布

1. 取得结构Read后捕获QueryMatchVersion与ArchetypeCount；整个范围内匹配版本及表集合固定。
2. acquire读取CheckedMatches。与QueryMatchVersion相等时取得Matches、Count与AllRows；冷路径锁私有QueryData并重读版本，过滤版本变化时重建，否则从CheckedArchetypes追加实际新增表。
3. 冷路径先写Match项/新数组引用/Count、过滤版本与覆盖状态，并更新CheckedArchetypes扫描进度，最后release发布CheckedMatches。后续热读取通过该字段观察到完整数据。
4. 遍历捕获的数组/Count；具体列基址在范围开始重取。释放结构Read后结束这些引用的寿命。

首次QueryData在查询目录锁内建立并发布；目录扩容先复制，再发布目录数组。热路径使用已绑定目录，冷锁和实际数组构造分别计入Construction。

### 完整访问与结束费用

一次异步查询的访问部分为：显式d条依赖 + 结构资源登记1项 + 范围构造 + 值/摘要登记k项 + 实际新增资源依赖 + 最后释放(k+1)项。两次登记各一次资源表锁；每次都按“Resolve→查冲突→发布”三个扫描执行；每项实际frontier节点访问与依赖使用本文S-W向量。前三阶段准备屏障共W6，阶段字段W4，第二份访问登记保存记录W1；实际依赖的计数增减属于对应d项子过程。完整完成方调用一次摘要内核。

Job连接的公开同步调用还承担S-W的CreateState/Release、等待和访问登记；它们按一次外层范围收取。专用ExecuteRanges复用已取得的访问，并加入上节Context(p)。World连接使用计数只服务连接解除/Dispose，工作资源状态由Job保存。任一准备阶段失败时，完成方释放已经登记的实际访问、清已取得范围、减少连接使用计数，再完成错误状态；这些终结动作沿既有state的唯一完成路径执行。

连接使用计数的BeginUse先检查Job上下文及closed/clearing两个Volatile状态。普通线程观察开放时以Interlocked递增用途计数，再次Volatile检查两个状态；二次检查开放才返回许可。状态变化时先沿EndUse完整撤销本次试探计数，再进入BeginUseSlow。Job上下文及其它状态直接进入该私有NoInlining慢方法，沿原child计算、准入锁、clearing等待、closed检查及Interlocked递增执行。原Enter的disposing免计数规则保持。

EndUse以Interlocked递减，归零后通过Volatile读取closed/clearing，关闭或清理期间取得原锁、复核计数归零并PulseAll。关闭与清理在原锁内以Volatile发布状态，WaitForUses两处锁内计数观察使用Interlocked.CompareExchange(ref uses,0,0)，提供状态发布到计数观察的完整屏障。关闭探测先于试探Inc且读到零时，Inc后复核发现关闭状态并回滚；Inc先发布且仍被持有时，关闭探测等待该计数；许可返回后的关闭同样等待已发布计数。Clear完整跨越检查时，开放状态由EndClear在清理完成后发布，许可可线性化到该次开放；清理进行时，试探计数先撤销再进入原等待。最终同锁复核与Monitor.Wait配对，通知继续由最后使用者完成。数据访问从BeginUse成功返回后开始。

普通成功Use计数协议的锁次数为0、计数原子为2，Begin前后合计读取四个状态，末次End归零另读至多两个状态。竞争回滚增加一对试探Inc/Dec及随后原门禁费用；Job回退增加一次上下文判断及冷调用，关闭等待每轮增加一至两次原子观察。EndClear沿原入口发布开放状态并唤醒准入等待者。字段、事件、池、容量及分配增量均为0；资源表、访问权限和实际回调的锁与执行成本沿各自原链另计。

EndUse的原子递减与状态早退沿AggressiveInlining热方法执行；原锁内计数/状态复核及Pulse保持原顺序，归NotifyUses私有NoInlining冷方法。每条路径的锁、原子与状态读取次数沿上段原协议；关闭通知增加一次冷方法调用。当前x64实际JIT中，Exit的普通EndUse调用已内联，代码由639 B降至554 B，rsp预留由120 B降至112 B、保存寄存器由四项降至三项；源码持有者与堆分配预算保持原值。具体主机的实际耗时随定点回执记录。

上述发布/观察顺序按[.NET 10内存模型](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/specs/Memory-model.md)的acquire读取、release写入及Interlocked完整屏障规则展开；关闭状态发布和用途计数探测之间的完整屏障进入冷等待预算。

WorldJobs的TLS访问frame池沿原104 B frame及初始四项引用目录复用，目录按原规则翻倍。RentFrame命中沿原减计数、读槽、清槽，ReturnFrame命中沿原写槽和计数发布；两段使用AggressiveInlining，新建frame与首建/扩容目录分别放入NoInlining冷函数。Enter/Exit的权限、结构先行、异常资源释放、frame清理及使用计数顺序保持原样。字段、池容量、冷对象数量及热分配增量0，实际调用边界、调用方代码与栈由JIT回执单列。

系统命令阶段在work-released之后取得结构Write并执行顺序Playback，再执行所需Maintain。公开系统handle在这些阶段完成后才成功，LastSystemVersion此时更新。依赖失败沿Job的失败传播结束；已完成普通值写及摘要保持可观察，系统基线由成功规则推进。

## 批次权限与单范围执行

Job资源表保护不同工作的冲突；同一并行批次的范围由生成器形成互斥写入分区。普通查询各范围使用自己的Chunk/行区间，追踪写范围按完整64行词分割；这些分区由构造过程形成。

连接Job的ECS执行上下文增加当前RangeAccess值{World引用,DriverKind,DriverId,Start,End}，24 B，并持有4 B执行depth；主列的DriverId为ChunkId，Sparse的DriverId为TypeId。每次实际执行范围前保存旧值并安装本范围和depth，执行和本范围收尾完成后恢复，再递减Remaining。正常与异常路径共用finally。每范围两次24 B范围状态写及两次4 B depth写，共4次TLS状态写；命名总式RangeContext(R)=〈2R,0,0,4R,0〉、状态写56R B，depth读取及引用屏障另列。通用Job内核保持原边界，完整ECS公式加入此总项。

范围与World及当前JobExecutionContext.Depth相同时使用其权限，Scope恢复先于Remaining递减。World.Get/Set、Buffer和Lookup写入口复用已定位的record核对Chunk/行，QueryChunk.Write/GetBuffers核对实际区间两端，GetBuffer(row)核对实际行；SetKnown批次在发布写入前预扫描其实际身份。上述入口核对及预扫描分别计入所属公开操作。

生成的逐行方法使用本范围已绑定的ref/span。World随机写与WriteLookup.Get先复用类型写声明，再按DriverKind找到该实体在驱动布局中的位置。普通主列驱动复用record；Sparse驱动复用已取得的cell或执行实际T+SFind。World/驱动种类/驱动编号/行范围各一次判断，通用路径B4；生成器已确定的驱动种类在闭合方法中归约。范围对象在绑定时保存相应权限，范围内方法复用它。

随机读取按静态声明取得相应类型或实际区间的Read。若某类型声明全类型随机Read且同批次写该类型，生成器选择串行范围执行；显式全类型随机Write同样选择串行范围执行，并登记全类型Write/摘要Write。普通分区查询继续并行，跨追踪写布局的批次沿Sparse页选择串行。调用者需要并行处理独立随机写集合时，以各自明确的范围提交普通Schedule，交由现有资源图处理依赖。

生成器在批次外从声明确定并行/串行策略，普通行循环保持固定策略。串行随机追踪写使用完整Stamp即时维护所触及的词/块/列，范围写采用本页完成合并；实际触及范围与完整Stamp费用各自入账。嵌套同步范围使用保存/恢复的RangeAccess，外层上下文随内层返回继续有效。

## 写版本的取得

World以64位单调计数保存版本。连接Job时，在全部值/摘要访问就绪后通过一次Interlocked.Increment取得本批写版本；独立写范围在首次实际写入时取得并复用。同步Core的串行写沿同一版本语义递增。生成行写、随机写、事实和本批完成阶段复用该值。完整预算对每个实际写批次追加一次版本写；Job连接再计一次原子操作。

## 普通访问frame的单份句柄

405969c/030ac09的WorldJobs.AccessFrame同时持有SynchronousResourceOwner（Scheduler引用8 B、JobHandle24 B）和同一JobHandle副本。x64对象104 B；独立同步入口Enter保存两份，Exit释放Access后同时清空两份。结构和值资源登记、等待、权限与嵌套深度由现有链完成。

D的收敛实现复用frame.Handle保存唯一访问句柄：删除frame.Access字段及其赋值/清理，两处Access.Handle.Index使用已存Handle.Index。Enter取得资源时继续使用局部SynchronousResourceOwner，异常收尾沿该局部Dispose。成功入栈时记录的JobDepth来自JobExecutionContext.Depth，IsActive等价于Depth非零；退出深度归零后，JobDepth为0的frame通过同一Scheduler.ReleaseSynchronousAccess(frame.Handle)释放同步取得的许可，其余frame结束Job借用。disposing线程免Use计数仍沿相同Acquire/Release配对，Counted继续控制BeginUse/EndUse。

该布局为72 B/对象，每个新frame少32 B，每次frame首次入栈/最终Depth归零出栈省32 B保存和32 B清空；同World嵌套继续只增减Depth；TLS池目录、资源表、注册、锁、原子、回调及拥有值的数量保持原预算。各上下文恢复、ValuesBound和结构优先登记顺序继续沿现有方法执行。实现时核对普通嵌套、Job借用和disposing三个现有完成链，再只测受改动影响的job-execute两种规模。

固定初始布局的job-execute热样本按三个实际Chunk调用用户行体，DefaultEcs适配沿原生连续列调用一次；完整周期的回调次数沿实际结构批次后的Chunk数量核算，回调范围及其调用成本分别归各实现。iterate-two沿TryGetValues的完整覆盖命中绑定两列N行，再进入相同MoveValues；N=100000时有效数据为读取P/V各800000 B、写回P800000 B，共2400000 B。该行的剩余机器差额沿原计时门槛核对；上述frame候选的源码字节收益单独归入Job路径。
## 当前访问计数的源码预算

030ac09的WorldJobs.BeginUse普通路径先检查Job上下文与closed/clearing标记，以Interlocked.Increment保留本次使用，再检查标记后返回；EndUse使用Interlocked.Decrement，结束标记及计数要求通知时才进入NotifyUses。普通成对使用的计数RMW仍为2次，入口Monitor为0，字段与容量沿原预算。BeginUseSlow与NotifyUses分别承担实际等待、关闭/清理协调及Pulse的锁费用。Scheduler的提交/资源表锁沿各自请求链单独计算；此处的普通入口预算对应WorldJobs的使用计数。
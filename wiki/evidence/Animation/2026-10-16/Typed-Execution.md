# 类型化动画执行证据

## 设备与方法

本记录对应 #48、#83、#49、#50、#51、#52 的共同公开执行路径。机器为 Intel Xeon E5-2698B v3（16 核、32 线程），Windows 11 专业工作站版 10.0.26200，.NET 10.0.13，X64。设备记录见 [machine.json](machine.json)。

全部构建、发布、执行与测量通过 `F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1` 进入共同 FIFO。Release 测量设置 `DOTNET_TieredCompilation=0`。完整可复现命令、每类计时边界和结果字段见 [消费者说明](../../../../tools/SomeEngine.Animation.Checks/README.md)。

纯批次使用 64 次预热、96 次采样，1,000 / 10,000 / 100,000 条轨道。直接循环与公共批次入口使用相同参数、状态、目标和时间；内置 Spring 与消费者 Spring 使用相同质量为一的积分公式。完整路径使用 12 次预热、48 次采样，固定 `1/120` 秒增量，目标值为 `1,000,000`，持续活动状态与最终字段值由公开 API 验证。

`executableBytes` 表示进程映像文件大小：Release 的 apphost 与 NativeAOT 的本机映像分别记录。算法方法和具体字段写入方法的机器码字节另列。峰值工作集为同一次进程的累积峰值；保留堆在完整 GC 后观察，首次声明、激活与预热分配分别记录。

## 纯算法 Release

队列 `e419456cab7546afb0e858d1696d99d7`，退出码 0。[标准输出](batch-release.stdout.txt) · [标准错误](batch-release.stderr.txt) · [队列结果](batch-release.result.json)。

100,000 条轨道，单位 ns/轨道：

| 算法 | 公共批次中位数 | P95 | P99 | 直接循环中位数 | 实例字节 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Tween | 4.927 | 5.829 | 6.026 | 3.833 | 40 |
| Spring | 9.563 | 11.291 | 14.089 | 6.507 | 52 |
| 消费者 Spring | 9.937 | 11.828 | 13.238 | 6.567 | 52 |
| 消费者 History | 5.535 | 6.343 | 6.859 | 4.757 | 80 |
| 消费者专用 History 批次 | 5.679 | 6.328 | 6.591 | 5.208 | 80 |

全部 30 组稳态分配为 0 B/批。实例字节包含参数、算法状态、端点、时间、采样值与活动标记。消费者的托管参数和历史通过普通 C# 泛型存储保留，公共批次入口处理三种实际布局。该结果提供算法成本参照；完整字段路径还包含系统、查询、来源维护、合成、字段写入和版本提交。

同公式静态缓动与标量函数适配追加队列 `0620ee4248c242349747d5c9e98812d3`，退出码 0。[标准输出](function-release.stdout.txt) · [标准错误](function-release.stderr.txt) · [队列结果](function-release.result.json)。两种路径都计算 `SquareEasing.Sample`，追加 12 组全部为 0 B/批。

| 100,000 条轨道 | 批次中位数 | P95 | P99 | 直接循环中位数 | 实例字节 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 静态 SquareEasing | 5.353 | 6.011 | 6.196 | 3.581 | 40 |
| 标量函数 SquareEasing | 6.444 | 8.114 | 12.961 | 4.621 | 56 |

标量适配每个未完成的活动样本调用一次参数中的函数地址，批次中位数增加 1.091 ns/轨道；该差值同时包含调用和实例步长差异。公共批次包含活动位与批次视图选择，直接循环遍历同一组持续活动数据。

## Release 机器码

队列 `70d8117777824c32b06c8ccead43d794`，退出码 0，`DOTNET_TieredCompilation=0`，`DOTNET_JitDisasm` 选取具体入口、求值和根写回方法。[完整机器码与输出](codegen-jit.stdout.txt) · [标准错误](codegen-jit.stderr.txt) · [队列结果](codegen-jit.result.json)。全部方法标为 `FullOpts`。

| 具体组合 | 分发跳板字节 | 批内循环字节 | 循环调用的算法体字节 |
| --- | ---: | ---: | ---: |
| Tween / FloatOps / SquareEasing | 6 | 457 | 0（已内联） |
| Spring / FloatOps | 6 | 501 | 252 |
| 消费者 History / FloatOps | 6 | 434 | 0（已内联） |
| FunctionTween / FloatOps | 6 | 458 | 标量缓动 5 |

公共 `MotionDispatcher` 在批次边界通过保存的 managed 地址执行一次 `call rax`，6 字节跳板再跳入确定的具体循环。Tween 的缓动与 FloatOps、History 的采样和历史递增均在循环内形成具体指令。Spring 的批次循环调用固定类型的 `Evaluate` 方法，其积分体中的值运算形成浮点指令。标量函数适配在循环内的未完成样本分支额外执行一次 `call rax`；类型化 `SquareEasing` 的平方形成乘法指令。

引用根的 `World.ExecuteComponentWrite` 为 714 字节，根循环 `Channel.WriteRoots<FieldRoot, FieldAccess>` 为 1,102 字节，包含 table/sparse 分支与 ECS 当前行解析、版本维护的共同代码。批次回调在 World 边界执行一次 `call rsi`。根循环在取得实际根后，具体访问及属性 setter 内联为对象引用读取和常量字段偏移写入；行定位、COW 与版本维护使用既有 ECS 方法。此处的字节统计包含该方法的检查和异常分支，热调用描述对应正常执行路径。

补充字段边界队列 `f69a85438aaf4b58960a400215b3c1b5`，退出码 0。[机器码与输出](codegen-field-jit.stdout.txt) · [队列结果](codegen-field-jit.result.json)。类型化 `Channel.WriteBatch` 为 242 字节，在批次边界从字段描述取得具体函数地址并执行一次 `call rax`；具体 `WriteFields<FieldRoot, FieldAccess>` 为 76 字节，将 span 和 Channel 放入栈上后调用具体根批次。加上 World 边界的一次回调，共两次应用层间接调用服务整个字段批次；每项的具体访问和 setter 继续内联到根循环。727 字节的 `ReferenceFieldEntry` 包含世界、对象、实体、输入数组创建及最终值检查，作为公开入口的对应证据。

### 系统调度的实际调用

队列 `af5d141657a0452ebb965fba03d7e70b` 同时执行全部公开消费者并输出系统机器码，退出码 0。[机器码与公开结果](systems-jit.stdout.txt) · [队列结果](systems-jit.result.json)。`SystemGroup<ImmediateSystemContext>.UpdateCore` 为 1,580 字节，持续更新每个启用系统时执行一次节点 managed 函数地址调用，以及驱动的四次接口调用：取得版本、创建上下文、`BeforeUpdate`、`AfterUpdate`。首次创建另经生命周期虚调用执行 `OnCreate`。这些调用及提交作用域均计入完整路径测量。

完整 `SystemFunctions<TSystem,TContext>` 地址表在具体系统和上下文类型上初始化。struct `TimelineSystem` 更新跳板为 58 字节，节点类型检查后取得内嵌系统地址，尾跳至确定的 `OnUpdate` 方法。class 系统采用共享泛型跳板，110 字节包含泛型字典解析、节点类型检查和系统引用读取，最终通过 `ISystem<ImmediateSystemContext>.OnUpdate` 的接口地址尾跳。`ViewRebuildSystem` 等引用类型系统沿这个共同形状执行，动态扩展成本据此计入系统入口。

NativeAOT 的同类跳板分别为 54 / 68 字节，系统组主体 1,787 字节。具体 Immediate 驱动的版本取得与上下文创建被编译成主体内指令，保留两次 `BeforeUpdate` / `AfterUpdate` 接口调用、一次节点函数地址调用，class 系统内部再经过一次接口尾跳。Native managed 地址根据标签位选择直接地址或携带泛型上下文的入口，每次分发实际执行其中一个分支。静态图或函数表的符号、主体与实际指令见下述 NativeAOT 证据。

## ECS 公开行为

Debug 独立消费者通过 `FieldAccessChecks.VerifyAll` 运行 13 条共享公开行为，另执行包含引用的泛型根及缓冲元素操作。最新根批次运行队列 `9615540e72d340fe83141c7abec6bf37`，退出码 0。[标准输出](ecs-root-batch.stdout.txt) · [标准错误](ecs-root-batch.stderr.txt) · [队列结果](ecs-root-batch.result.json)。

检查覆盖 table 与 sparse 的普通、值嵌套、引用嵌套和可写属性；行、列与任意实体批次写版本；当前对象身份；包含托管引用的泛型缓冲；managed 查询和系统回调的公共结果。任意实体批次共享一次根存储写权限和一个提交版本，批内解析当前行并调用具体访问类型。

既有 ECS 与 Systems 测试工程的规则诊断保存在 [ECS 输出](ecs-rules.stdout.txt)、[ECS 结果](ecs-rules.result.json)、[Systems 输出](systems-rules.stdout.txt) 和 [Systems 结果](systems-rules.result.json)。独立消费者共用公开行为源文件，提供各能力的运行证据。

## 算法与图公开行为

核心与类型化图的 Release 定向验证通过 27 项，行为规则检查为 0 项违规。队列 `01c002753d7a438b89c8ff24354c7e7e`，退出码 0。[标准输出](core-typed-graph.stdout.txt) · [标准错误](core-typed-graph.stderr.txt) · [队列结果](core-typed-graph.result.json)。覆盖完整类型参数、managed 状态与消费者算法、阶段和完成、共享图定义、实例参数更新、固定截止时间重定目标、自定义条件及自定义叶生命周期。

兼容图、销毁清理与输出卸载的补充执行合计形成 46 个不同公开检查，命令与原始三件集中在 [图行为验收](Graph-Checks.md)。

## 完整 Release 矩阵

队列 `6253c51d9b414f8e8d2c50d523a7dfe3`，退出码 0，UTC 14:54:25–15:16:25。[全部 CSV 与存储布局](full-release.stdout.txt) · [标准错误](full-release.stderr.txt) · [队列结果](full-release.result.json)。构建队列 `08eb147626f940a09937ba300076544f`，0 警告、0 错误，[构建输出](full-release-build.stdout.txt) 与 [结果](full-release-build.result.json)。该矩阵包含后继阶段保护、幂等声明、输出卸载及实际根批次写入。

系统调用中位数为直接入口 1.855 ns、完整 `SystemGroup` 403.711 ns；公共空查询委托与 managed 函数入口分别为 231.250 / 227.441 ns。系统组数字包含上下文、驱动、提交观察作用域、版本与回调。100,000 项组件访问中位数如下，单位 ns/字段，全部为 0 B/样本：

| 字段路径 | 根列读取 | 类型化整列写入 | 逐实体 World.WriteField |
| --- | ---: | ---: | ---: |
| 普通 | 2.757 | 0.613 | 308.683 |
| 值嵌套 | 2.678 | 0.610 | 301.154 |
| 引用属性 | 2.727 | 1.809 | 307.829 |

完整字段管线中位数单位为 ns/目标，尾部分位数对应 100,000 项：

| 算法与字段 | 1,000 | 10,000 | 100,000 | P95 | P99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Tween 普通 | 3576.300 | 4059.950 | 4112.990 | 4210.167 | 4246.526 |
| Spring 普通 | 3769.200 | 4123.600 | 4296.501 | 4495.658 | 4559.580 |
| History 普通 | 3508.300 | 3999.660 | 4169.314 | 4502.110 | 4588.002 |
| 静态 Square 普通 | 3595.100 | 4090.640 | 4057.199 | 4244.945 | 4307.851 |
| 函数 Square 普通 | 3594.300 | 3967.530 | 4092.501 | 4262.438 | 4315.831 |
| Tween 值嵌套 | 3603.500 | 4052.990 | 4073.586 | 4183.830 | 4253.375 |
| Spring 值嵌套 | 3752.000 | 4070.120 | 4190.725 | 4305.314 | 4340.033 |
| History 值嵌套 | 3678.600 | 4035.360 | 4067.214 | 4166.411 | 4195.361 |
| 静态 Square 值嵌套 | 3570.500 | 3930.650 | 4057.301 | 4180.781 | 4211.979 |
| 函数 Square 值嵌套 | 3531.600 | 3942.270 | 4097.979 | 4362.978 | 4429.472 |
| Tween 引用属性 | 3649.300 | 4055.190 | 4124.250 | 4320.365 | 4430.141 |
| Spring 引用属性 | 3782.900 | 4071.190 | 4182.135 | 4346.242 | 4452.965 |
| History 引用属性 | 3611.300 | 4078.450 | 4110.786 | 4424.997 | 4490.970 |
| 静态 Square 引用属性 | 3585.300 | 3963.050 | 4066.159 | 4236.522 | 4434.651 |
| 函数 Square 引用属性 | 3728.900 | 4081.480 | 4079.833 | 4283.054 | 4574.051 |
| Tween 普通 WorldJobs | 4422.100 | 4749.810 | 5037.891 | 6149.127 | 6198.011 |
| Tween 值嵌套 WorldJobs | 4386.800 | 4695.950 | 4768.332 | 4896.954 | 4927.262 |
| Tween 引用属性 WorldJobs | 4388.400 | 4777.430 | 4818.287 | 4948.779 | 5139.978 |

完整管线包含 Timeline、类型化轨道、来源、合成、根写入、完成和版本维护。每个实体的具体 Motion 缓冲形成求值批次；本场景各实体有一条轨道。字段输出归并后按 Channel 提交一次根批次。54 组均保留强制完整 GC 后 48 个样本合计 208 B（4.333 B/样本）的原始分配计数；GC 后恢复与持续帧的分解通过同一公开路径单列记录。

### 控制、来源与图

高频控制单位为 ns/目标，48 个采样批次均为 0 B：

| 操作 | 1,000 | 10,000 | 100,000 | P95 | P99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Retarget | 3482.500 | 3941.210 | 3955.377 | 4441.165 | 4727.817 |
| 已声明算法切换 | 7098.900 | 7361.800 | 7482.165 | 7690.496 | 7781.511 |
| Stop 后 To | 10967.100 | 11319.750 | 11437.036 | 12050.607 | 12967.346 |

来源单位为 ns/来源，每个字段包含两个来源，规模为 2,000 / 20,000 / 200,000 来源。动态项计入逐目标 `Contribute`、排序与合成；固定项运行完整动画管线。所有持续帧为 0 B：

| 来源处理 | 2,000 | 20,000 | 200,000 | P95 | P99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 固定双来源 | 2566.250 | 2956.260 | 3004.464 | 3156.931 | 3270.660 |
| 动态提交、排序与合成 | 620.600 | 609.830 | 659.307 | 683.609 | 703.554 |
| 完成保持、输入相同 | 3429.000 | 3714.815 | 3786.077 | 3981.053 | 4055.063 |
| 保持、层权重变化 | 4541.100 | 4675.530 | 4721.644 | 4816.275 | 4842.020 |
| 保持、基准值变化 | 3855.100 | 4246.605 | 4224.558 | 4475.587 | 4950.541 |

图共享一份编译定义。100,000 实例，单位 ns/实例，推进包含当前参数输入、Timeline 与 GraphSystem，求值包含类型化轨道、图输出、合成、图收尾和 Timeline 完成；所有 36 组图阶段记录为 0 B/帧：

| 图 | 推进中位数 | 求值中位数 | 完整中位数 | 完整 P95 | 完整 P99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 固定序列 | 80.692 | 5516.406 | 5600.384 | 6044.539 | 6074.558 |
| 收敛序列 | 78.279 | 5448.174 | 5526.419 | 5669.635 | 5724.240 |
| 状态机和输入 | 1844.945 | 5327.139 | 7184.810 | 9492.371 | 9857.646 |
| Blend 和输入 | 1362.561 | 5163.885 | 6521.533 | 6704.178 | 6709.996 |

100,000 项首次声明为 28,152.146 ns/目标、分配 312,995,232 B；已声明后的首次播放为 7,437.165 ns/目标、0 B。首次完成帧为 4,379.284 ns/目标、分配 7,439,208 B，包含首次工作容量增长；随后完成并复用同一实例为 10,292.452 ns/目标，P95 10,606.803、P99 10,773.838，持续批次 0 B。每个实例累计完成 61 次，由公开 Timeline 结果确认。

### 声明容量与保留内存

以下数值包含 World、目标数组、声明结构、组件块与执行数据。声明与激活单位为 ms；保留内存为完整 GC 后的字节/目标。各项原始总分配、声明保留堆、激活保留堆和累积峰值工作集保存在 CSV 的 `memory` 行。

| 声明 | 目标数 | 声明 ms | 激活 ms | 激活后 B/目标 |
| --- | ---: | ---: | ---: | ---: |
| 单 Tween | 1,000 | 25.514 | 10.817 | 3302.912 |
| Tween 和 Spring | 1,000 | 35.493 | 24.580 | 3970.088 |
| 大历史状态 | 1,000 | 79.398 | 80.780 | 5701.168 |
| 单 Tween | 10,000 | 301.055 | 119.752 | 3182.503 |
| Tween 和 Spring | 10,000 | 382.476 | 278.432 | 3839.846 |
| 大历史状态 | 10,000 | 326.952 | 117.242 | 5547.315 |
| 单 Tween | 100,000 | 2904.140 | 1193.300 | 3166.463 |
| Tween 和 Spring | 100,000 | 3797.170 | 2706.676 | 3815.488 |
| 大历史状态 | 100,000 | 3224.663 | 1154.709 | 5533.495 |

实际公开缓冲容量：`Track<float>` 元素 160 B、容量 4；Tween Motion 40 B、容量 1；Spring Motion 52 B、容量 1；大历史状态 272 B，其 Motion 元素 328 B、容量 1。大状态包含 64 个 float、托管字符串引用与计数。具体算法默认内联容量采用 1，混用时仅增加声明过的算法存储。全进程累积峰值工作集为 853,454,848 B。

首次类型使用包含相应 JIT 和共享描述初始化：首组 1,000 普通 Tween 的世界、目标、声明、启动和系统初始化合计 541.872 ms；后续专门声明内存组的 1,000 Tween 为 25.514 ms。共享代码地址由具体类型持有：每个 MotionDispatcher 一个 8 B 地址载荷，MotionAccess 每种实际算法两个地址共 16 B；字段描述的实际步长为 48 B，字段身份为 8 B，按叶值类型共享的初始 16 项数组载荷共 896 B。实例保存紧凑 Channel、实体与具体数据，公开字段访问代码按路径共享。

## NativeAOT 代表性扩展

独立消费者以 `win-x64`、self-contained、`PublishAot=true`、`OptimizationPreference=Speed` 发布，生成同一次映像的方法映射。首个有效发布队列 `51e5545ba01744f993609d4508e68fcc` 退出码 0，[发布输出](native-publish.stdout.txt) · [结果](native-publish.result.json)。该映像为 9,671,680 B。公开验证队列 `df0ea10629eb42c18ea96b329e6d650b` 退出码 0，[完整结果](public-native-baseline.stdout.txt) · [队列结果](public-native-baseline.result.json)。

消费者运行了包含引用的组件根和缓冲、生成字段、字节兼容入口、managed 查询与系统、强类型 `.Animate`、引用 `State.Value`、完整泛型自定义算法、managed 历史、批次算法、自定义值运算与缓动、时钟与完成，以及共享图定义、独立实例状态、自定义条件、公开叶和输出卸载。成功标记为 `GRAPH_CONSUMERS_PASSED` 与 `ANIMATION_CHECKS_PASSED`。相同公开行为的 Release 执行及系统反汇编也保存在上述 `af5d1416…` 任务中。

系统函数地址采用完整 `SystemFunctions<TSystem,TContext>` 共享表，实际 Native 发布与运行确认节点恢复到对应系统类型。原先构造器在派生类型与泛型基类构造签名交汇处直接执行 `ldftn`，ILC 输出曾出现错误的双上下文类型实例；将 `ldftn` 放在完整二参数静态表中后，公开系统与 Views 运行通过。原始诊断保存在 [地址绑定诊断](native-address-diagnostic.stdout.txt) 和 [原始结果](native-address-diagnostic.result.json)，实际机器码中的每帧函数地址调用保留为一次。

发布诊断记录 `TraceCapture.JsonSerializer.Serialize` 的 IL2026 / IL3050，以及 `JobStorageTypeShape.GetFields` 的 IL2070；三个位置均列于原始发布输出，代表性消费路径以实际执行结果验收。

### 纯算法 NativeAOT

队列 `183b46a312284166b1fc9f82326c450a`，退出码 0。[42 组结果](batch-native.stdout.txt) · [队列结果](batch-native.result.json)。全部为 0 B/批，数组首次分配为 `元素字节 × 条数 + 24`，保留数组载荷为 `元素字节 × 条数`。

| 100,000 条轨道 | 批次中位数 ns | P95 | P99 | 直接循环中位数 ns |
| --- | ---: | ---: | ---: | ---: |
| Tween | 5.978 | 9.383 | 10.410 | 4.063 |
| Spring | 11.034 | 12.326 | 12.696 | 6.871 |
| 消费者 Spring | 11.427 | 12.480 | 15.359 | 6.666 |
| 消费者 History | 6.693 | 7.274 | 7.951 | 5.067 |
| 消费者 History 专用批次 | 6.760 | 7.340 | 8.203 | 5.296 |
| 静态 Square | 5.375 | 6.125 | 6.533 | 3.564 |
| 函数 Square | 6.335 | 7.058 | 7.413 | 4.975 |

### NativeAOT 实际机器码

公开 `--codegen` 队列 `7a774a171e2e41868812d5854ce9e89c` 退出码 0，[执行结果](codegen-native-run.stdout.txt)。映射与地址采集 `43fd2925d6db422cae14c2d7d0e786a8`、最终 PE 指令采集 `36e897c38d9043fc9fca32129f4b9733` 均退出码 0：[方法字节与 PDB 地址](codegen-native-name-symbols.stdout.txt) · [实际指令](codegen-native-instructions.stdout.txt) · [指令队列结果](codegen-native-instructions.result.json)。`.text` 地址由 PE 分节取得，方法起点由同一次 PDB 的 section 1 十进制偏移计算；方法字节来自对应映射。

| 具体组合或入口 | NativeAOT 字节 | 正常批内行为 |
| --- | ---: | --- |
| MotionDispatcher 跳板 | 5 | 固定跳入具体批次 |
| Tween / SquareEasing 批次 | 451 | 缓动平方与 FloatOps 内联 |
| Spring 批次 | 503 | 调用固定类型的 254 B 积分体 |
| 消费者 History 批次 | 433 | 历史更新与采样内联 |
| FunctionTween 批次 | 468 | 每个未完成样本额外调用一次标量函数地址 |
| ScalarEasing | 5 | 标量平方指令 |
| Channel.WriteBatch | 275 | 按字段描述调用一次具体批次地址 |
| 具体引用字段 WriteFields | 66 | 建立栈上批次并调用根存储入口 |
| World.ExecuteComponentWrite | 776 | 一次权限、版本及具体根循环回调 |
| 具体引用字段 WriteRoots | 1,232 | 解析当前行，setter 内联为 `[对象 + 8]` 浮点写入 |
| ReferenceFieldEntry | 660 | 创建公开输入并验证引用对象结果 |

Tween、Spring、History 的应用层动态算法分发位于批次边界一次；FunctionTween 的额外动态调用发生在每个未完成活动样本。NativeAOT 的 managed 函数地址带标签检查，实际选择普通入口或携带上下文的入口后执行一次调用。引用字段批次共享 Channel 和 World 两个应用层动态入口；每项具体访问形成对象引用读取与常量偏移写入。表中根循环主体包含 table/sparse、行解析、COW、版本及异常分支，具体方法字节覆盖整个主体。映射记录的每个 MotionDispatcher / SystemFunctions 非 GC 静态块为 16 B，其中函数地址载荷为 8 B；共享静态块与实例存储分别计量。

### GC 后恢复与诊断字符串

Release 单组公开路径队列 `023ce9e248dc432ea1a729cc75953cd6` 退出码 0，[原始分解](allocation-release-baseline.stdout.txt) · [结果](allocation-release-baseline.result.json)。1,000 普通 Tween 的 48 帧总分配为 208 B：完整 GC 后首帧 208 B，随后 47 帧合计 0 B。这与完整 Release 矩阵每组 208 B 的原始计数一致。

NativeAOT 基线的 1,000 目标全路径队列 `041c17fc2c1046a78f39fbc78cdcd66a` 退出码 0，[全部结果](full-native-baseline.stdout.txt) · [结果](full-native-baseline.result.json)。全部 18 组字段持续帧出现 80,000 B/帧；普通 Tween 的完整 GC 后首帧为 80,296 B，随后 47 帧合计 3,760,000 B。固定来源、图和控制中的 Timeline 访问也按调用次数产生相应费用，根列与直接字段写入仍为 0 B。

实际 Native 指令将原因定位到 `World.IsEnabled<Timeline>` 正常路径：`0x14045B7F0` 在进入 `Owners.Components.IsEnabled` 前调用 RuntimeType 名称接口。泛型启用读写原先把 `typeof(T).Name` 作为通用实现的诊断参数，Native 每次取得 `Timeline` 名称形成 40 B 字符串，两次访问构成每目标 80 B。实现现传递 `Type`，在既有缺组件诊断分支创建名称；组件身份、存储权限、启用读写和诊断文本沿共同入口保持一致。Release 的 GC 后缓存恢复与 Native 的逐帧字符串费用由最终同一公开路径采样确认。

9,660,928 B 基准映像的指令队列 `b8c4de8ca5da4aa98d327662fe9d2159` 退出码 0：[实际指令](codegen-final-native-instructions.stdout.txt) · [队列结果](codegen-final-native-instructions.result.json)。`World.IsEnabled<Timeline>` 为 412 B，在 `0x140455B87` 取得 Type 地址后直接传入共同实现。共同实现于 `0x14004A5E6` 按缺组件结果跳到诊断分支，正常结果在 `0x14004A616` 返回，名称调用位于诊断分支的 `0x14004A626`。该主体由原 516 B 增至 882 B，World 包装由 469 B 缩至 412 B；具体四类算法批次、引用根写入与系统跳板的字节数保持前表的实际映射值。

## 字段与 GC 基准发布（9,660,928 B）

该基准版本包含空算法存储早退、当前行来源更新、Views 元数据与 FieldMotion 复用，以及上述诊断名称延后创建。Native 发布 `457279ad74ab4167b661fa98df3b20e7` 与 Release 构建 `f06ff6974d5f4262a7a2cede14ab5734` 均退出码 0：[Native 发布](native-final-publish.stdout.txt) · [Native 结果](native-final-publish.result.json) · [Release 构建](public-final-release-build.stdout.txt) · [Release 结果](public-final-release-build.result.json)。Native 映像为 9,660,928 B，Release apphost 为 162,816 B；Native 发布保留前述三处诊断，Release 构建为 0 警告、0 错误。

本节的十八组字段、GC 分解、控制、来源、图与三组声明内存均对应这份 9,660,928 B 基准映像。最终修复源码的公开验收对应末节 `e55ebbe8…` 发布，映像为 9,787,392 B；两组发布各自保留原始命令、输出和任务结果。

该基准公开消费者在 Native 队列 `8d8c20af24bc472a9dc00358a47b6b3c` 和 Release 队列 `2776232742dd43388072164f3ce44d34` 均退出码 0，包含全部十项 Views 公开行为和 ECS、算法、图的共同检查：[Native 输出](public-final-native.stdout.txt) · [Native 结果](public-final-native.result.json) · [Release 输出](public-final-release.stdout.txt) · [Release 结果](public-final-release.result.json)。

### 字段与 GC 分配

Native 最小完整矩阵使用 1,000 目标，队列 `5b22bd78e1034420a4f51dca4249f68d`，退出码 0，UTC 16:12:53–16:13:02：[全部 CSV](full-final-native.stdout.txt) · [队列结果](full-final-native.result.json)。下表单位为 ns/目标，所有 18 组每个样本分配 0 B，完整 GC 后首帧 0 B、后续 47 帧合计 0 B。

| 算法与字段 | 中位数 | P95 | P99 |
| --- | ---: | ---: | ---: |
| Tween 普通 | 2125.900 | 2380.400 | 2448.900 |
| Spring 普通 | 2019.900 | 2279.800 | 2585.000 |
| History 普通 | 1982.500 | 2240.400 | 2280.700 |
| 静态 Square 普通 | 1968.800 | 2296.000 | 2507.700 |
| 函数 Square 普通 | 1930.900 | 2131.900 | 2309.500 |
| Tween 值嵌套 | 2121.700 | 3765.900 | 3931.900 |
| Spring 值嵌套 | 1960.900 | 2479.400 | 2922.400 |
| History 值嵌套 | 2054.300 | 2819.700 | 3792.000 |
| 静态 Square 值嵌套 | 1914.100 | 2250.600 | 2616.700 |
| 函数 Square 值嵌套 | 1997.200 | 2181.400 | 2774.100 |
| Tween 引用属性 | 2036.900 | 2497.200 | 2605.400 |
| Spring 引用属性 | 1968.400 | 2793.000 | 3738.000 |
| History 引用属性 | 1921.500 | 2383.300 | 2588.700 |
| 静态 Square 引用属性 | 1875.500 | 2086.400 | 2349.900 |
| 函数 Square 引用属性 | 1940.000 | 2082.900 | 2349.000 |
| Tween 普通 WorldJobs | 2413.200 | 2748.000 | 2889.900 |
| Tween 值嵌套 WorldJobs | 2391.400 | 2721.100 | 3401.800 |
| Tween 引用属性 WorldJobs | 2487.600 | 2846.800 | 3362.200 |

Release 单组 `fccb0678e1db4a94a053723cc3ac9e6a` 复用相同 1,000 普通 Tween，退出码 0：[原始输出](allocation-final-release.stdout.txt) · [队列结果](allocation-final-release.result.json)。中位数 2292.900、P95 2503.000、P99 2542.300 ns/目标；完整 GC 后首帧为 0 B，随后 47 帧合计 0 B。原有 54 组 Release 全规模记录与 Native 原始 80 B/目标记录保留为定位证据，最终共同入口的分配结果由这组最小补充和 Native 18 组实跑确认。

### 控制、来源与图

该基准 Native 的声明后重定目标、算法切换、停止并播放、固定双来源、动态提交、三类保持输出，以及十二组图阶段持续帧均为 0 B。下列中位数按控制和图的目标数、来源的实际来源数计量：

| 操作 | ns/项 |
| --- | ---: |
| Retarget | 2950.700 |
| 已声明算法切换 | 6377.200 |
| Stop 后 To | 10281.400 |
| 固定双来源 | 2391.450 |
| 动态提交、排序与合成 | 543.050 |
| 保持且输入相同 | 2952.050 |
| 保持、层权重变化 | 3873.050 |
| 保持、基准值变化 | 3289.050 |
| 固定序列完整执行 | 4258.400 |
| 收敛序列完整执行 | 4363.300 |
| 状态机和输入完整执行 | 5599.500 |
| Blend 和输入完整执行 | 5151.100 |

1,000 项首次声明为 28.772 ms、分配 4,117,896 B；已声明后首次播放为 6.787 ms、0 B；首次完成帧为 2.588 ms、分配 93,120 B，包含首次工作容量增长。后续完成并复用为 7445.900 ns/目标，P95 7828.600、P99 8286.000，0 B/批。公共空查询委托和 managed 函数入口中位数为 201.953 / 219.043 ns/次，完整系统组为 364.062 ns/次，这三项的持续样本为 0 B。

### 三组 Native 声明内存

1,000 目标，实际缓冲大小与容量沿用前述公开布局。总保留字节包含 World、目标、声明与激活后的执行数据。

| 声明 | 声明 ms | 声明分配 B | 激活 ms | 激活分配 B | 激活后保留 B | B/目标 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 单 Tween | 25.440 | 4212728 | 8.638 | 98752 | 3298592 | 3298.592 |
| Tween 和 Spring | 32.094 | 4971304 | 20.296 | 165720 | 3970560 | 3970.560 |
| 大历史状态 | 28.331 | 6595192 | 9.002 | 131448 | 5683616 | 5683.616 |

该进程累积峰值工作集为 50,831,360 B。首次类型及共同结构初始化后的专门声明采样提供以上三组；整个测量的首组普通 Tween 创建、声明、启动与系统初始化合计为 40.383 ms。

## 声明持有与算法重选

`AnimatedField` 持有各实际泛型组合的 `FieldMotion` 声明集合，以及当前选择的声明。每个具体对象保留完整 Spec 和 Channel，提交函数地址保持完整类型；反复选择已经声明的 Linear、CubicOut 与便利 Spec 时复用各自对象。便利声明在首次 `.Animate` 时建立所需算法容量，类型化声明登记它自己的具体组合。

这组源码包含 Animation `3616801b`、Views `85e4a4a9`、声明集合 `6e3a6639` 与首次便利容量 `7c66724e`。Native 发布队列 `e5dfe6fd7d544d0fa961da690c01d6c6`，退出码 0，UTC 17:16:25–17:18:39：[发布输出](native-declarations-publish.stdout.txt) · [队列结果](native-declarations-publish.result.json)。映像为 9,824,256 B，发布输出保留前述三处诊断。

带发布门槛的执行队列 `1d8a86e2c9344ec1add242dd62218e00` 读取该发布结果，成功后运行同一映像，退出码 0：[实际输出](native-declarations-public.stdout.txt) · [队列结果](native-declarations-public.result.json)。默认消费者覆盖十一项 Views 公开行为，以及 ECS、算法和图的共同检查；新增的公开行为从首次帧之后观察实体及组件结构，在九次算法重选中验证当前呈现连续、具体插值结果、实体与存储复用及引用字段身份。

同一脚本随后执行 `--view-switch-benchmark`，128 帧预热后测量 1,000 次完整帧的当前线程分配，包含状态修改、Views 构建和动画。该次原始输出为 `VIEWS_DECLARED_SWITCHES samples=1000 bytes=96000`，即 96 B/完整帧；该诊断沿独立场景记录，字段、来源与图的既有 0 B 矩阵保留各自的实际输入与结果。脚本源文件见 [Run-NativeChecks.ps1](../../../../tools/SomeEngine.Animation.Checks/Run-NativeChecks.ps1)。

## 最终修复源码公开验收（9,787,392 B）

最终源码包含启用状态的查询批次读取 `4144742b`、轨道 span 复用 `11da31ce`、公开阶段诊断 `12866aaa` 和构建回滚记录复用 `3d952a4f`。类型化轨道通过 Enabled / Disabled 查询取得本批次的启用状态，求值内复用已取得的轨道 span。具体算法、字段访问与函数地址继续沿共同类型化入口执行。

声明重选场景的 96 B 来自呈现字段写版本触发的视图参数重建，每帧创建一个 `ChildOrder` 和一个 `ChildComputations` 回滚记录。外层构建现在复用作用域持有的两份记录；嵌套及异步构建沿已有共享保持与完成边界处理。最后一份保持完成时清空子实体快照、World 和旧计算集合引用，后续捕获继续使用同一对象。

Native 发布队列 `e55ebbe8af4148b9b361fea9b4a61225` 退出码 0，UTC 17:46:23–17:48:28：[发布输出](native-complete-publish.stdout.txt) · [标准错误](native-complete-publish.stderr.txt) · [完整命令与结果](native-complete-publish.result.json)。实际映像为 **9,787,392 B**，发布诊断为前述三个位置。

门槛执行队列 `6e6e2e62b91b431f8494e374e5cadf66` 读取发布成功结果后运行同一映像，退出码 0，UTC 17:48:28–17:48:30：[实际输出](native-complete-public.stdout.txt) · [标准错误](native-complete-public.stderr.txt) · [完整命令与结果](native-complete-public.result.json)。默认十一项 Views 与 ECS、算法、图的共同公开检查全部通过，输出两项公开消费者成功标记。随后执行 `--view-switch-allocations`，合计 1,000 次完整帧分配为 **0 B**：

| 选择 | 完整帧数 | State B | Views B | Animation B | Declare B | Set B | 构建次数 | 计算次数 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Linear | 333 | 0 | 0 | 0 | 0 | 0 | 333 | 333 |
| CubicOut | 333 | 0 | 0 | 0 | 0 | 0 | 333 | 333 |
| 便利 Spec | 334 | 0 | 0 | 0 | 0 | 0 | 334 | 334 |

实际构建与计算每个采样帧均执行一次。静止帧为 0 B、构建 0 次、计算 0 次。完整字段与 GC 基准的十八组及全规模 Release 矩阵继续以此前各自发布的原始结果计量；本节提供最终源码的 Native 公开能力与声明切换分配验收。

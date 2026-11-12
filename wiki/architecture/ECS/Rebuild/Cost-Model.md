# 操作计数口径

分析时间：2026-10-22 22:50 +08:00。状态：本组实现规范使用的源级计数规则。

分析方法：固定源码版本，展开公开入口、数据定位、实际操作、缓存维护、回收和容量变化；设计侧给出具体字段与伪代码预算。本文记录算法与设计约束，计数单位是源级操作。

## 标识与比较边界

| 标识 | 含义 |
|---|---|
| S-E | [EasyEcs 0eadbc43a4a5973286fa352a19711968b2852b87](https://github.com/JasonXuDeveloper/EasyEcs/tree/0eadbc43a4a5973286fa352a19711968b2852b87) 已逐语句读取的路径 |
| S-O | [SomeEngine 80f446207d2968cf0c0a0636b0c1429e44f8fe37](https://github.com/ODtian/SomeEngine.Next/tree/80f446207d2968cf0c0a0636b0c1429e44f8fe37) 完整旧实现 |
| S-W | 本次读取的工作树文件及具体方法；源码链接指向该文件 |
| S-B | [.NET v10.0.0](https://github.com/dotnet/runtime/tree/v10.0.0) 的集合与 I/O 实现 |
| D | 拟议布局、伪代码及设计预算 |
| E* | 在 EasyEcs 上提供相同能力的适配预算；实际执行端使用 S-E 方法 |
| R-E | 前序源聚合的历史标识；本组基础路径已用逐步 S-E 计数替换 |

每项比较固定相同的结果、身份、观察时点、顺序、启用位、追踪、缓冲独立性、错误与并发能力。普通组件迁移、Enableable 位变化、Sparse 侧存储分别配对。首次、命中、增长与回收各有一行。适配预算作为设计列参与比较。

当前方案工作树以origin/main 13864df6为基线。S-O继续表达上表80f44620的固定旧实现计数；隔离原型的共享Job后端采用本轮main合并结果。每项实测记录实际构建及main提交，设计预算、固定源码计数和该项实测分别沿自己的依据解释。

## 向量、调用与字节

`C=〈P,A,B,W,L〉`：P 为引用/原生基址字段读取；A 为数组或原生元素定位；B 为条件分支；W 为状态或输出写；L 为堆分配。相同 ref 的后续字段操作只增加相应 W。局部赋值、局部循环计数和地址算术单列为算术，源级 B 按短路项展开。循环 n 次的条件为 n+1。

数组引用元素的读取同时计 P/A；结构元素取得 ref 计 A1。整项赋值记 W1 并注明宽度。字段逐写按各字段计 W。字段合并的收益同时用字节比较。闭合 T 的静态类型判断按所选能力归约。BC 表示交给 JIT 的数组边界检查位置；GC 表示实际引用写屏障。原子写同时计 W，并另列 CAS/Add/Exchange、Volatile 和屏障次数。I 表示实际间接分派。

源码计数记录代码明确读取的字段，JIT 的合并与内联属于实现阶段。数组字段用于 Length 和用于元素定位时按源码各计一次 P；绑定为同一局部后复用计一次。闭合类型的空追踪/索引/分组调用及其空接收者准备归约为0。局部构造和循环累加属于算术，状态、返回 out 和输出数组的写入计 W。表中的整数比较 B 与 comparer/codec 内部控制分开。

批量数据基元：`Copy_T(n)` 的数据项为 A2n/Wn、读写各 n·sizeof(T) B；`Zero_T(n)` 为 A n/W n、写 n·sizeof(T) B；`Fill_T(n)` 为 A n/W n、写同样字节。其控制项分别记 `Kcopy(n,T,overlap)`、`Kzero(n,T)`、`Kfill(n,T)`，包括实际选择的 Span/Array/原生实现的分支、调用和屏障。双方调用同一实现、同一类型、同一范围时可以逐项消去；参数不同的调用完整保留。单元素原生分块复制使用 `ByteCopy_T(1)`，其实际宽度写次数按对应复制实现计。

任意用户 key、比较器、codec、Job 的工作分别记 Hash_T、Eq_T、Compare_T、Encode_T、Execute_T；各页给出精确调用数、处理项数和字节数。编码的控制由具体 codec 决定；一次 codec 调用始终作为实际计算计入。

## 公共 D 内核

以下是普通值、当前World身份、已存在列、容量充足的设计预算；可选功能由专题叠加。World.Execution是可选执行连接，公开入口读取一次并选择同步/Job范围，记为E。连接Job后再加入Jobs页的A(k,d)；内部传递已取得的访问，复用R/G和实际存储定位。

| 内核及输入 | 〈P,A,B,W,L〉 |
|---|---|
| E：公开入口读取执行连接并选择范围 | 〈1,0,1,0,0〉 |
| R：实体记录数组、范围、代数，返回 record ref | 〈1,1,2,0,0〉 |
| Life：record已取得，读取Chunk/Archetype的活动生命周期 | 〈2,0,1,0,0〉；Chunk已绑定时为〈1,0,1,0,0〉 |
| T：类型目录范围、元素和存在判断，返回 store | 〈2,1,2,0,0〉 |
| V：record 已取得，Chunk→列目录→列→行 | 〈3,2,2,0,0〉 |
| G：结构入口检查、进入、退出 | 〈0,0,1,2,0〉 |
| Read/Get主体 | R+V=〈4,3,4,0,0〉 |
| 公开Read/Get | E+R+V=〈5,3,5,0,0〉 |
| SetCore：已在结构范围中 | R+V+值写=〈4,3,4,1,0〉 |
| 结构写内的Set组合，记作S | G+SetCore=〈4,3,5,3,0〉 |
| 公开Set，普通已有值 | E+SetCore=〈5,3,5,1,0〉 |
| 公开Set，维护索引/布局的结构路径 | E+S=〈5,3,6,3,0〉，实际能力另加 |
| CreateKnown(k)：目标 Chunk 已绑定，k≥1 个普通值 | 〈3+k,2+2k,4,8+k,0〉 |
| Create(k)：含组合缓存绑定 | 〈6+k,3+2k,6,8+k,0〉 |
| X：普通尾行 Destroy，所有附加能力关闭 | 〈5,1,7,7,0〉 |

Create的record写为16 B。CreateKnown/Create/X/M是可组合主体，公开调用再加E一次；批次内部各行沿用外层连接和访问。X包含Sparse首项目录和销毁事实的开启判断；已开启时追加相应内核。中间删除增加实际列填洞、身份回填和被移动记录修正。已建立Lookup的字节偏移/代数或typed位置更新、已注册关系的局部销毁、索引与缓冲清理分别按实际能力加入，目录宽度由[[Storage-and-Queries]]持有。

`M(k)` 表示含G与普通方向边定位的迁移主体，公开入口加E。默认大 Chunk、k 个普通 POD 共有列、源尾行时，D 为 `〈11+2k,3+3k,9+k,7,0〉 + Σ ByteCopy_T(1)`。逐步算法由 [[Storage-and-Queries]] 持有，其中 MoveKnown 为 `〈5+2k,1+3k,k+4,5,0〉`。Shared/GroupBy 已取得目标时直接调用 MoveKnown，复用身份/结构范围/目标。源中间行加入 [[Bulk-and-Copy]] 的 FillHole；实际新增、释放、启用、追踪和托管操作另加。

## 容量与池

`Grow(X,c,c',u)`：取旧数组 P1，容量判断 B1，新数组 L1，`Zero_X(c')`，`Copy_X(u)`，发布引用 W1。u 是实际复制范围。调用点已经计容量判断时使用 Grow−B1；旧数组基址已经绑定时再扣 P1。引用数组的 GC 按实际引用数计。对象分配另写 L1 和实际字段。POD 固定地址列发布 Values/Rows 两项；新数组的类型前缀、页内偏移与对齐余量沿 [[Construction]] 的实际布局计入。

线程局部数组池的设计方法：Rent 绑定 free 数组；检查数组与 Count；读末项并清空，Count 减一。命中 `〈2,2,2,2,0〉`；空池分支分配实际对象/数组。Return 绑定 free 数组、容量判断、写末项并增加 Count，为 `〈1,1,2,2,0〉`；目录增长加 Grow。每个池列出归还前的值清理、保留上限判断与该类型首次目录分配。

内存预算按实际数据持有者展开：主列、实体记录、行身份、调用方输入、缓存目录、池、事实与检查点分别列出元素宽度、有效数量、实际容量和结束寿命的入口。保留字节按容量乘元素宽度计算，对象头、数组头与对齐余量单列。基础组的设计数据为三列各8 B组件、16 B实体记录、8 B行身份和8 B调用方身份，合计56N B；N=100,000时为5.6 MB，预留容量和实际附加能力分别加入。Buffer按其实际数组容量计，活跃元素数量另列。

累计分配记录一个测量区间内产生的字节总量，保留容量记录同一完成点仍由具体持有者持有的空间，进程私有提交与峰值工作集记录进程级资源。周期次数扩大累计工作量；同一World的保留容量按其实际峰值与完成点分别核算。实例释放入口及池的保留规则共同解释跨次保留；单库内存归因使用该库独立实例与相同操作序列，进程级指标同时标明进程内驻留的库及World数量。测量字段与采样边界由[[Acceptance]]持有。

存档分别记录有效输出Z、输出后端实际容量C及余量C−Z。ArrayBufferWriter的每次增长按实际GetSpan提示与旧容量计算，累计分配包含被替换的全部数组和数组头，动作完成后的保留只包含最后仍被持有的数组。输出对象归调用方/action持有，World主数据与输出容量沿各自完成点分别入账；后台或文件输出沿自己的实际缓冲与文件字节计。

ArrayPool<T>.Shared归还后的数组由线程缓存及共享桶继续持有。Shared成员、ValueIndex成员、Children与关系邻接的实际容量均按元素宽度乘容量计入相应持有者；增长旧数组和读回替换数组归还后进入池保留账本。World.Dispose结束主数据和目录的持有，池容量沿进程级池寿命记录。释放后的托管存活增量同时包含这些池及初始化类型缓存；动作后的快照先于读回校验，校验分配单列，随后释放点记录校验产生的实际池状态。

## 字典的实际内核

普通 value key、默认 comparer、已分配桶、c 个候选 entry、e 次相等测试。依据 S-B [FindValue:397–486](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs#L397)：

| 路径 | 源级固定部分 | 实际 key 工作 |
|---|---|---|
| Hhit(c,e) | 〈4,c+1,3c+e+1,0,0〉 | Hash_T 一次、Eq_T e 次 |
| Hmiss(c,e) | 〈4,c+1,3c+e+3,0,0〉 | Hash_T 一次、Eq_T e 次 |

P4 是桶存在读取、comparer、GetBucket 的桶、entries；A 是桶和 c 个 entry。命中分支含前置两项、c 次范围、c 次 hash、e 次相等结果、c−1 次链循环。引用 key 增加入口空值判断；自定义 comparer 按源码选择对应分支和实际分派。

S-B [GetValueRefOrAddDefault:1071–1197](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs#L1071) 的命中固定部分为 `〈4,c+e+2,3c+e+2,1,0〉`，W1 是 exists 输出。新增且使用新 entry、容量足时为 `〈5,2c+e+2,3c+e+6,8,0〉`：count、entry 的 hash/next/key/value、桶、version、exists；实际 value 赋值再 W1。复用 free entry 增加一次 entry 读取、freeList/freeCount 两写，以新 count 写作替代。调用方取得 Dictionary 字段另计 P1。

S-B [Remove:1292–1363](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs#L1292) 命中时 `Hremove(c,e,r)=〈4,1+c+[c>1],3c+2e+2,4+r,0〉 + Hash_T + e·Eq_T`，r∈[0,2] 是 key/value 两个结构中实际需要清引用的结构数；GC 屏障按结构内引用数另计。四次固定写是桶/前驱 next、entry.next、freeList、freeCount；c>1 时访问前驱一次。字典首次 Initialize 为 buckets+entries L2、两次清零及四个目录字段写；Resize 为 L2、复制 entries、逐实际 entry 重新链接桶。字符串切换 comparer 的额外路径按 S-B 原方法计。

## 完整成本与选择

`C完整 = E + 本次访问A(k,d) + 定位/结构主体 + 数据操作 + 已开启功能 + 缓存维护 + 增长 + 归还/完成`。同步World的额外A为0，引用/结构范围已在主体计数；Job连接的A是资源表登记/依赖/释放。各专题整数向量表示其已列输入下的主体，公开调用统一加E一次，写明含E的总式直接使用；嵌套内核复用外层范围。每个候选按相同向量、字节、分配、同步和保留内存比较。

表中向量按明确输入计算；闭合类型的实际key/codec/执行体另带精确调用数，Source与Design各列的同名子过程使用相同实现、类型、范围和容量。缓存增长、首次分配与派生重建按实际触发次数加到整条公共路径。

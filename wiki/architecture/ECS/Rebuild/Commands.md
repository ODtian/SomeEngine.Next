# 命令记录与回放预算

分析时间：2026-10-22 22:50 +08:00。状态：S-O 为源码依据；默认顺序回放与显式原子选项已确认；结果寿命见本页末节。

分析方法：展开 S-O CommandBuffer、类型化载荷与回放调用链，EasyEcs 用相同顺序的原生操作组合比较；按 [[Cost-Model]] 计入命令记录、临时实体映射、工作入口、实际操作、完成清理与容量变化。

## 数据结构与执行方法

CommandBuffer持有Command[]、类型化载荷桶目录、实际使用桶类型列表及本批创建结果。20 B命令头为Kind、内部目标两个int、TypeId、DataIndex。真实目标沿Entity编码；临时目标内部编码为负局部Id及producer编号+1，记录入口先从公开DeferredEntity验证并转换。

每个 CommandBuffer 由单一生产者记录；并行生产者各持有私有分段。以下记录预算采用该数据归属，合并在依赖完成后执行。

每种载荷桶持有 `T[]` 与 Count。缓冲载荷另持有紧密元素数组和 `{Start,Length}` 8 B 范围数组。关系命令同样按具体值类型入桶。payload 与命令头分别持有实际值和顺序；实际使用类型列表只为 Clear 访问本批桶。

类型化桶的执行方法完成对应的 Core/Relationships 操作。类型擦除处一次接口分派，随后进入已闭合 T 的方法。这里承担命令计算；API 使用固定操作种类和数据。命令分派自身直接调用实际操作，实体销毁消费者等费用沿对应操作计入。采用该入口是 C# 中运行时类型集合与泛型载荷的连接点；全类型生成 switch 要求完整的封闭类型目录，属于另一种能力配置。

核心 API：Create、Destroy、Set/Add/Remove、Enable/Disable、缓冲操作、Sparse 操作、关系/Hierarchy 操作、Playback、Resolve、Clear。回放按记录顺序执行，每批一次进入结构操作范围。命令执行直接更新主数据，批次结果通过 Created 数组读取。普通 Playback 保留已完成前缀；显式原子入口在独立 World 上执行并返回成功结果。该规则已在本轮确认。

关系最终集合校验通过显式复合命令记录：EditParents<D>(ReadOnlySpan<ParentCommandEdit>)、EditEndpoints<T>(ReadOnlySpan<EndpointCommandEdit>)。一个复合命令包含一个完整拓扑批次，使用[[Relations]]的最终父链/占用算法。它之前的已完成命令构成普通Playback前缀；该批次完成校验后一次提交自己的主值与派生数据，再执行下一条命令。

Job捕获Before的重载接收[[Relations]]定义的CapturedParentEdit/CapturedEndpointEdit及DataGeneration。查询来源同时传入实际Query与MembershipVersion；显式实体来源保存Before与DataGeneration。typed bucket与Composite头继续由本producer持有。捕获条件和最终图校验位于同一FIFO位置，结果区间、失败前缀和载荷转交沿本页流程，完整签名与完成链由Relations持有。

该命令沿用20 B顺序头：TypeId选择关系/domain，DataIndex指向typed编辑数组起点。两个目标int与紧凑记录行按命令种类解释：

| 复合命令 | 第一个目标int | 第二个目标int | 紧凑记录行 | 行宽 |
|---|---|---|---|---|
| Parent编辑 | 编辑行数 | deferred为0/1 | Child、Parent、InsertAt | 20 B |
| 端点纯Retarget | 编辑行数 | deferred为0/1 | Edge、Source、Target | 24 B |
| 端点含Create/Destroy | 本producer创建结果区间起点 | 低31位为编辑行数，高位为deferred | Edge、Source、Target、Kind | 28 B |

临时身份沿本页producer映射解析。记录复制实际r行并发布typed Count，顺序头与命令Count各写一次。回放复用对应的解码数组，再由[[Relations]]持有最终编辑字典与实际Before读取。Job暂存输入同时保留其捕获Before以供完成方核对，行布局由该页持有。容量、类型绑定、解码与批次校验分别计一次。

## 顺序表示的同口径候选

固定 M 条混合类型命令、值 T 的实际宽度 s、相同 FIFO、临时实体与托管载荷能力。表中记录入口已取得 buffer，容量足，类型桶已存在；执行内核在后表逐种相加。

| 候选 D | 一条值命令记录 〈P,A,B,W,L〉 | 一条值命令解码 | 每条实际存储 |
|---|---|---|---|
| 20 B 固定头 + T[] | 〈4,3,5,4,0〉 | 〈2,3,2,0,0〉，I1 | 20+s B；顺序头与值各一次写 |
| 8 B 顺序项 + TypedCommand<T>[] | 〈4,3,5,4,0〉 | 〈2,3,2,0,0〉，I1 | 8+align(12+s,align(T)) B；目标/Kind 随 typed 值存储 |
| 压缩头字节流 + T[] | 固定头记录替换为 HeaderBytes(b)：P1、A b、B(b+1)、W(b+1) | 头读取 A b、varint 分支 B b，其余桶/载荷 P2/A2、Kind/临时 B2、I1 | b+s B；b 为五个整数的实际编码总字节 |

typed 候选与固定头相同的三次定位为目录、typed 项、顺序项；载荷与目标合成一次结构写，仍有两个 Count 写和一个顺序项写。20 B 固定头的五个字段全部为 int，行宽按 4 B 对齐；typed 记录受到 T 对齐影响。压缩头首次建立 byte[] L1，增长按实际已用 b_total 复制，托管 T 继续保存在 typed 数组；记录/解码的 varint 控制逐字节入账。

选定固定 20 B 头：混合类型 FIFO 解码为一次结构读取，任意 T 由 typed 数组持有，头大小与 T 对齐独立。相同 typed 方案在单类型输入上可以省顺序表，但混合输入仍需上述完整顺序项；该特化按连续段另外计。

## 回放安全语义的候选

| 候选 D | 执行前工作 | 执行与成功结束 | 失败时数据语义 |
|---|---|---|---|
| 顺序 Playback | G 一次、绑定头/桶/Created、预留已知容量 | M 次解码与实际操作、Clear、G 退出 | 前面已完成的命令可见 |
| 完整复制后 PlaybackAtomic | [[Serialization]] 的完整 Capture/独立 World 构建，主值、Buffer、Sparse、Shared、拓扑均计复制与派生恢复 | 相同 M 条命令回放；成功写 World 引用 W1；释放旧 World 按实际数据 | 候选 World 释放，调用者继续持有原 World |
| 触及数据预演后提交 | q 个实体修改项、各变长类型/启用位、实际触及的关系和 key；q 次完整身份映射字典插入；按涉及索引构建变更集合 | 预留全部最终容量、计算全部可失败用户值操作，再提交每项值/索引/邻接写与版本 | 预演覆盖全部实际失败点时成立 |

预演候选的实体项 `{Entity,Archetype,SharedIds,SparseTypeBits,LastCommand,Flags}` 为 40 B，q 项数组 L1；变长目录各自 L1，关系/索引的实际闭包项另外存储。每项构建至少整项 W1、字典 Hget-new+value W1，之后每条命令仍执行其状态检查与变更写。完整失败原子性还包含所有用户 key 计算与托管复制。选定普通顺序 Playback，完整原子模式显式使用独立 World 复制；两种 API 各自承担所请求的结果语义。

## 源码依据与参照

S-O [CommandBuffer.cs:202–277](https://github.com/ODtian/SomeEngine.Next/blob/80f446207d2968cf0c0a0636b0c1429e44f8fe37/src/SomeEngine.ECS/Commands/CommandBuffer.cs#L202) 的 CommandEntity 含 8 B Entity 与对象引用，在 x64 为 16 B；CommandHeader 含对齐后共 32 B。临时实体每次 Create 创建 DeferredEntityCell；PlaybackContext 每次回放创建对象及两个 Dictionary。

S-O [CommandBuffer.cs:389–535、657–843](https://github.com/ODtian/SomeEngine.Next/blob/80f446207d2968cf0c0a0636b0c1429e44f8fe37/src/SomeEngine.ECS/Commands/CommandBuffer.cs#L389) 记录入口取得锁，回放依次执行 public reservation、Job topology write、锁、结构候选、CommandPlaybackContext、关系 batch、命令循环、提交。临时 Resolve 查字典，Create 完成时还锁 cell 并检查发布代数。

S-O [CommandBuffer.Payloads.cs:29–118](https://github.com/ODtian/SomeEngine.Next/blob/80f446207d2968cf0c0a0636b0c1429e44f8fe37/src/SomeEngine.ECS/Commands/CommandBuffer.Payloads.cs#L29) 已经使用类型化值桶，这是保留的有效机制。缓冲原有 Starts/Lengths 两数组可以合并为范围结构数组。

EasyEcs 固定版本的 [Core/生成器目录](https://github.com/JasonXuDeveloper/EasyEcs/tree/0eadbc43a4a5973286fa352a19711968b2852b87/src) 提供即时结构操作。等价延迟命令能力使用 D-E：同样的顺序头、类型化值桶与临时 ID 映射，执行其原生 Entity/World 方法。即时调用列表作为“实际数据操作”子项单独计入。

## 记录内核

计数使用已实例化 CommandBuffer、有效输入与足够容量；值结构体写 W1，按 sizeof(T) 标注。P/A/B/W/L 与 [[Cost-Model]] 一致。

| 记录操作 | P | A | B | W | L |
|---|---:|---:|---:|---:|---:|
| Set/Add 一个值，桶已存在且本批已使用 | 4 | 3 | 5 | 4 | 0 |
| 本批首次使用该值桶的额外增量 | 1 | 1 | 1 | 2 | 0 |
| 仅 Destroy/Create 的头追加 | 1 | 1 | 1 | 2 | 0 |
| Create 的临时 ID 计数 | 0 | 0 | 0 | 1 | 0 |
| 类型化 Remove/Enable/Disable，执行桶已存在 | 3 | 2 | 3 | 2 | 0 |
| n 个缓冲元素，桶已使用 | 5 | 3+2n | 6 | 5+n | 0 |

Set/Add 五个判断为桶目录范围、桶存在、本批首次使用、载荷容量、命令容量；四次写为载荷、载荷 Count、命令头、命令 Count。P 包括桶目录、桶对象、载荷数组、命令数组；A 为桶取得、载荷写、命令头写。

首次使用桶：在 Count 从 0 变为 1 的路径追加 TypeId，写类型 ID 和实际使用数各一次。容量判断与数组基址/元素访问计入表内增量。只含无载荷操作的桶持有空共享数组，Clear 只处理有实际载荷的桶。

类型化记录入口用TypeId直接取得Bucket<T>。类型注册保证该编号对应同一个T，已有桶沿该内部不变量转为闭合桶；首次桶建立进入独立冷方法，统一增长目录并发布。头追加与桶取得的热方法内联，命令数组增长和首次类型登记分别留在冷方法。

缓冲记录在上述三类目录/数组之外持有范围数组与元素数组：复制 n 个元素 A2n/Wn，写范围 8 B、范围 Count、元素 Count、头、命令 Count，W5。SourceGen 的 Entity 字段处理在回放时执行；记录阶段只复制值。

## 回放完整路径

批次前取得 Command[] 与桶目录两个基址 P2；进入结构范围 B1/W2；循环 M 条命令的条件 B(M+1)。下面的增量为每命令解码，实际数据操作另加对应专题的完整内核，扣除已经由批次承担的入口。

回放将有效头范围绑定为ReadOnlySpan<CommandHeader>，循环以ref readonly取得当前头，复用批次外绑定的桶目录。每条命令成功执行后写CompletedCommands；失败时该字段仍指向已完成前缀，结果发布沿本页的批次结果对象完成。

| 命令 | D 解码与映射开销 | 实际执行路径 | D-E 相同能力的解码与执行 |
|---|---|---|---|
| Create | 头 A1；Kind 分派 B1；批次绑定 Created 后 A1/W1 | 已知组合 Create，写 Created[局部ID] | 同头和数组映射；EasyEcs Create |
| Destroy，直接实体 | 头 A1；Kind B1；目标临时判断 B1 | 基础销毁含资源/Sparse/事实 | 同解码；EasyEcs Destroy 与选定的目标清理策略 |
| Set/Add 值，直接实体 | 〈2,3,2,0,0〉，一次接口分派 | 桶内闭合 T → 实体定位一次 → 已有值写或迁移 → 开启功能 | 同头/桶/载荷解码；Entity.Set/GetOrAdd |
| Remove/Enable/Disable | 〈1,2,2,0,0〉，一次接口分派 | 桶内闭合 T → 相应操作 | 同解码；语义相同的 Remove 或 Disable/Enable |
| 缓冲替换/追加 n 元素 | 值解码 + 范围数组 P1/A1；复制成本按 n | 解析缓冲一次 → Reserve → copy/append → 追踪一次 | 同范围与原生 DynamicBuffer 操作 |
| Sparse | 值/无载荷解码 | [Sparse 完整操作](Sparse.md) | 同能力的侧存储适配器 |
| 关系/Hierarchy | 值/无载荷解码，端点逐一解析 | [相应完整操作](Relations.md) | 关系身份与维护语义相同的适配组合 |

值命令的 P2 是桶对象和其 T[]；A3 是头、桶、载荷。两个 B 是 Kind 分派与目标是否临时；接口分派单列一次。批次外将 Created 基址绑定一次 P1；单 producer 的每个临时引用再 A1，多 producer 再读一次 Offsets[producer] A1。直接实体路径只有身份选择判断。编译后 switch 的实际指令结构属于实现验证项，D 将它预算为一次分派。

带临时Entity字段的载荷采用本页末节的显式修正项；实际t项按记录、解析、赋值完整计费。普通Entity字段随T复制。

M 条普通已存在列的 Set，q 个实际临时目标：解码加批次入口合计 P(2+2M+[q>0])、A(3M+q)、B(3M+2)、W2、L0，另加 M 次已进入结构范围的 Set 内核、实际载荷内临时字段处理与开启功能。D-E 使用完全同一命令表示时解码同式，实际操作换成其 Set 内核；EasyEcs 每次原生操作自己的结构入口按源码计入。能够使用同语义原生批量 API 的连续同类命令段，以该批量 API 对照。

顺序依赖按命令流执行。连续同类段可复用类型桶与迁移边引用，第一次取得后，后续每命令省桶 P1/A1；能够预知连续段容量时，容量检查移到段外一次。任意跨命令合并的正确性取决于中间可观察行为，预算使用原始顺序流。

## Producer 合并、结束与清理

每个 Job producer 持有私有分段。完整过程为：Job 完成依赖 → 按 producer key 形成确定顺序 → 遍历分段原命令数组 → 分段局部临时 ID 加 Created 区间起点 → 执行 → 清理。p 个已按索引提交的 producer 直接按 p 个目录项读取，A p、B(p+1)；任意 key 输入首次 sort(p)，重用 key 顺序时只读取既有目录。Created 区间前缀一次扫描 p 项：producer 目录 P1、各 buffer 引用 Pp/A p，Offsets 基址 P1/A p，循环 B(p+1)、W p，合计〈p+2,2p,p+1,p,0〉；各 buffer 的 CreateCount 为字段读取。

回放直接遍历分段，命令头额外复制字节为 0；临时映射每次为一次 producer 起点和一次 Created 项读取；单 producer 省起点读取。完整依赖调度成本见 [[Jobs]]。生产者失败时的成功性结果在进入回放前取得一次，数据执行阶段按已具备的状态运行。

成功后的载荷清理（k个实际使用值桶）：命令Count、实际使用类型数、完成命令数各W1；完成创建数release发布到本批结果对象W1，Created供Resolve。绑定 UsedTypes/桶目录 P2，读取 k 个实际使用 ID 和 k 个桶 Pk/A2k，循环 B(k+1)，每桶 Count W1、一次类型化清理分派。含引用的载荷清除实际使用 n 项；POD 载荷以 Count 表达有效范围。缓冲桶额外重置 RangeCount/ElementCount 两项。显式 Clear 重置临时身份范围，归还池在显式归还或 Dispose 时执行。

值桶按固定的HasValueOperations选择记录分支。具有值操作的分支完成Clone后，把对应transferred项写为false；容量增长沿同一类型条件同时扩充值数组与transferred数组。普通值分支直接写入T[]。回放按实际转交结果更新transferred，Clear释放仍由值桶持有的值。

记录入口以closed表达本分段的当前记录状态。Seal置closed，Release同时置closed与disposed；Clear先检查disposed，再通过Reset打开下一次记录。因此记录入口读取closed一次，生命周期入口沿disposed处理释放状态。typed Create沿既有内联记录内核写入载荷、顺序头与局部创建编号。

S-O Clear 还逐个失效 DeferredEntityCell、取消 typed relationship 对象、清空含引用的 32M B 命令头区域，并扫描整个载荷目录容量 U。D 命令头为纯值，结束只更新有效计数；数据桶按实际使用 k 项清理。

## 全生命周期相加与可合并项

M条已有列Set、q个临时目标、k个值桶，单producer、POD载荷、容量充分且采用普通值载荷：记录M·〈4,3,5,4,0〉+k·〈1,1,1,2,0〉；回放〈2+2M+[q>0],3M+q,3M+2,2,0〉+M·SetCore，I=M；成功清理/结果发布〈k+2,2k,k+1,k+4,0〉，I=k。公开DeferredEntity目标的批次验证、结果对象批次费用、实际Create、临时字段修正和多producer前缀分别按本页对应项加入。

上述三阶段均包含：载荷存储、20 B 头、真实实体定位/写、临时转换、清计数。记录与回放合计的实际值写是记录 sizeof(T) 一次、执行 sizeof(T) 一次；对象 T 含引用时 Clear 再 Zero_T(used)。

连续同类型段长 m：桶对象绑定外提一次，后续省 (m−1) 次 P/A；payload 数组也能外提，后续再省 (m−1) 次 P。该段预留容量一次，可省其余 m−1 次载荷容量判断。SetCore 接收已绑定类型信息，实体记录仍逐实体定位。结构 G 在批次外一次。功能实际更改与缓存首次维护按原命令顺序执行。

## 首次与扩容账

命令头、实际使用类型列表和类型化值数组继续由各自记录器持有；容量增长独立为冷入口，记录热路径只检查剩余容量、写当前项并推进计数。临时目标编码和单producer结果解码沿同一结果对象直接索引，按闭合类型内联到记录与回放调用链。

| 事件 | S-O | D / D-E 等价表示 |
|---|---|---|
| 一个临时Create | 每实体DeferredEntityCell L1，另追加cell/头列表 | 本批首个Create建立共享结果对象L1及Created数组L1；后续容量足时L0 |
| 一批 Playback 映射 | Context L1 + 两 Dictionary L2；首个键再分配各自桶/entry 数组 | 本批结果数组容量足时L0；新批首次L1，清零8c B，外部handle持有至其生命期结束 |
| 首个组件载荷桶 | 桶对象和 T[8]，L2；发布目录项 | 同类型化机制，桶对象 L1 + 首个 T[] L1；发布 A1/W1 |
| 首个无载荷类型 | 原 DataList<T> 也创建 T[8] | 桶对象 L1，T[] 使用共享空数组 |
| 缓冲元数据 | Starts/Lengths 各 L1 | Range[] L1；复制字节总量同为 8c |
| 关系命令 | 各具体 command 对象，通常每命令 L1 | struct 值桶；容量足时每命令 L0 |
| Command 容量 c→c' | L1，copy(32c)，新数组含引用，清理亦维护引用 | L1，copy(20c)，纯值头，发布 W1 |
| Payload 容量 c→c' | L1，copy(c·sizeof(T))，发布 W1 | L1，copy(n·sizeof(T))，发布 W1 |
| 桶目录增长 | L1，copy(8c)，发布 W1 | L1，copy(8c)，发布 W1 |
| producer 增长 | 私有段、命令/载荷/临时映射分别建容量 | 同样逐个列出实际数组；前缀目录 4p B、Created 8·CreateCount B |

所有新容量清零按类型宽度计。类型首次注册还包含一次静态元数据与实际操作入口建立；运行时重复记录使用 int 类型 ID 数组。采用理由分别是：顺序头承担 FIFO、类型化数组承担值存储、Created 承担局部到真实身份转换、实际使用类型表承担批次回收；每份数据都有独立消费者。

## 创建结果与失败位置

公开DeferredEntity为16 B值：创建结果对象引用、producer编号int、局部Create编号int。结果对象按记录批次建立，持有Created Entity[]、多producer的Offsets int[]、CompletedCount和CompletedCompositeCreates；记录阶段由CommandBuffer持有。两个int填满引用后的8 B，x64对象共40 B。每个创建共享本批结果对象，实际结果按数组项保存。CompletedCompositeCreates只累计已经完整提交的复合创建数，回放finally把普通创建局部计数与它相加，一次release发布CompletedCount；普通命令循环沿原有计数执行。

Create返回handle，记录Add/Set/Destroy等命令时验证handle的结果对象等于当前批次且CommandBuffer仍处记录阶段，再把producer/局部编号编码入20 B命令头。跨buffer与旧批次的handle由这一处检查识别。内部回放使用已验证的紧凑编码，保持原有数组索引内核。

Playback开始封闭记录；按确定producer顺序建立Offsets。成功/失败出口把已完成命令数写回buffer，并release发布CompletedCount。TryResolve先acquire读CompletedCount，按Offsets/局部编号取得已成功发布的Entity；Resolve沿相同路径返回或报告当前状态。创建后又销毁的结果仍返回曾经创建的身份，存活状态由World查询。

Clear/Dispose结束当前记录与载荷寿命，已发布的结果对象继续由外部handle持有；新批建立新对象。结果数组由该对象随GC回收，复用容量发生在同一记录批次的增长中。由此保留CommandBufferTests中的ClearAfterSuccessfulPlayback_PreservesPublishedHandleAndLiveEntity、DisposeAfterSuccessfulPlayback_PreservesPublishedHandleAndLiveEntity及跨buffer记录约束。

失败出口保留原命令、载荷、已完成位置与成功创建映射；调用者读取结果后通过Clear开始新批。普通Playback保留已完成前缀，失败命令沿该公开操作的失败语义。

显式原子模式由应用组合现有WorldCheckpoint和CommandBuffer。应用先建立独立候选World及绑定它的系统，沿[[Serialization]]的Preserve模式Capture源World，再Restore候选World及一一对应的系统进度。随后新建绑定候选World的CommandBuffer并记录、回放；成功后应用接收候选World及其系统，释放旧World。记录或回放失败时释放候选World，继续使用源World。复制包括实际主值、独立Buffer、Sparse、Shared、派生数据和延期维护进度，托管值沿注册的克隆规则。

下面的PlaybackAtomic是该组合流程的设计函数；candidate由调用方新建，两个系统数组按同一系统顺序给出。record向候选World的buffer记录本次命令，结果句柄与成功候选World一起交接。Core继续负责顺序命令，完整数据复制和系统进度由存档层提供。

```csharp
static void PlaybackAtomic(World source, World candidate,
    ISystemVersions[] sourceSystems, ISystemVersions[] candidateSystems,
    Action<CommandBuffer> record)
{
    try
    {
        using var data = new WorldCheckpoint();
        data.Capture(source, CheckpointProgress.Preserve, sourceSystems);
        data.Restore(candidate, candidateSystems);
        using var commands = new CommandBuffer(candidate);
        record(commands);
        commands.Playback();
    }
    catch
    {
        candidate.Dispose();
        throw;
    }
}
```

该完整模式的首次与周期成本包含候选World/系统建立、Capture、Restore、候选命令记录、回放、成功交接和被替换World释放；四个参照沿各自完整数据复制与同一交接流程比较。

结果成本：每个含Create的批次一个对象L1、Created数组首次L1、p>1时Offsets首次L1；数据容量8c+4p B。回放外层绑定结果数组，内部每个临时目标仍为单producer A1、多producer A2。外部handle的Resolve读取结果对象、可空Offsets与Created，共P3；单producer A1，多producer A2。判断依次为CompletedCount是否为0、Offsets是否存在、最终编号是否已发布，共B3；CompletedCount再计一次acquire读。TryResolve沿同一流程输出Entity，成功再W1。首次结果对象构造、封闭记录、结果发布与Clear解除当前引用的字段写按批次加入。

连续段优化保持相同producer、操作种类、闭合T和原始顺序，失败位置使用原命令编号。E*使用相同批次结果语义，内部解码后调用各库实际原生入口。

## 载荷中的临时实体引用

普通T载荷中的Entity保存真实身份。临时引用通过生成器提供的字段编号与DeferredEntity显式绑定；记录入口验证每个handle所属批次，然后保存紧凑修正项。闭合的TFields实现IEntityFieldWriter<T>.Set(ref T value,int field,Entity entity)，字段路径在生成期确定。

| 载荷 | 公开修正输入 | 内部修正项 | 每命令范围 | 记录入口 |
|---|---|---|---|---|
| 标量T | DeferredEntityField(Field,DeferredEntity)，24 B | Field、编码目标Entity，12 B | FieldStart、FieldCount，8 B | Create/Set/Add<T,TFields>，fields为ReadOnlySpan<DeferredEntityField> |
| Buffer元素T | BufferEntityField(Element,Field,DeferredEntity)，24 B | Element、Field、编码目标Entity，16 B | ValueStart、ValueCount、FieldStart、FieldCount，16 B | SetBuffer/AppendBuffer<T,TFields>，fields为ReadOnlySpan<BufferEntityField> |

各入口沿普通值或Buffer签名接收实际载荷；Set/Add及Buffer入口同时提供Entity和DeferredEntity目标重载。记录时把每个临时引用沿本页编码保存为负局部Id与producer+1。带修正的命令由闭合T/TFields桶持有载荷、范围和修正数组，顺序头继续使用相应Create/Set/Add/Buffer的Kind。标量回放复制T到局部值再逐字段修正；Buffer回放在私有载荷数组中修正指定元素，再把有效值范围写入目标Buffer。两者都通过Created与实际Offsets解码并调用TFields.Set，随后进入原有Core操作。

设标量与Buffer实际临时字段数分别为s和b，t=s+b。记录费用包含t次批次验证、12s+16b B修正写、每标量命令8 B范围写、每Buffer命令16 B范围写，以及原有载荷复制。回放包含t次修正读取、t次Created读取、多producer时t次Offsets读取及t次字段分派/赋值。数组首次与增长按各自实际容量和行宽计算；普通Buffer的8 B值范围已包含在这里的16 B范围内。跨批身份由公开handle在记录阶段校验。

复合拓扑命令的公开输入采用CommandEntity目标，支持真实Entity与DeferredEntity两种来源。CommandEntity为16 B：结果对象引用及两个int；真实身份的结果引用为空，临时身份携带对应批次结果。记录时统一验证并编码成内部8 B目标。

ParentCommandEdit包含两个CommandEntity及InsertAt，x64为40 B；EndpointCommandEdit包含三个CommandEntity及Kind，为56 B。它们由调用者的输入span持有，记录后转换为本页的20/24/28 B紧凑行，回放解码为对应的实际身份编辑数组。[[Relations]]的最终编辑字典和Job捕获Before行各按自己的布局计费，费用包含各阶段实际转换和字节复制。

带新edge的复合入口为EditEndpoints<T>(ReadOnlySpan<EndpointCommandEdit>,Span<DeferredEntity> created,bool deferred=false)。EndpointCommandEdit的Kind为Create、Retarget、Destroy，Create/Destroy提供对应命名构造入口。输入用CommandEntity.CreatedInBatch(i)引用本复合批的第i个新edge；该表示的内部Id为−i−1、第二整数为0。外部DeferredEntity编码的第二整数为producer+1，因此回放先解析前序命令引用，再把批内引用交给Relations<T>.Edit的NewEntities映射。只含已有edge重定向的输入继续复用原来的类型化回放桶。

记录一次复合命令时，为q个新edge连续分配本producer的q个结果编号并填写created输出，然后按本页复合头布局写入一个顺序头；q由创建编辑项确定。回放把相应Created区间直接作为身份预选表，Core最终集合提交后累计q到CompletedCompositeCreates，整个复合命令作为一个成功位置推进。普通Create与复合Create的结果编号均按原命令顺序分配。成功/失败出口统一发布已经完整提交的创建前缀，Resolve沿CompletedCount观察结果。只修改已有edge的重载使用空created输出。

公开普通记录方法按真实Entity和DeferredEntity分别提供重载：真实目标先检查记录阶段B1；Deferred目标另读取handle结果引用/当前结果引用P2并比较B1。内核表在这些入口验证之后使用。复合输入的CommandEntity逐项分派真实、前序临时、批内临时三种目标，批内编号按已声明创建数校验；实际选择、转换和输出各自计费。

# 存储、迁移与查询实现规范

分析时间：2026-10-22 22:50 +08:00。状态：D 设计预算；下列S-E内核按固定源码计数，公开入口按E/访问/实际能力组合。

分析方法：读取 EasyEcs Entity、World、World.Components、World.Archetypes、Archetype、Query、StructuralGate 和 ArityGenerator；按 [[Cost-Model]] 将字段引用、元素定位及分支对应到具体步骤。[[Execution]] 持有引用与提交顺序，[[Construction]] 持有首次和扩容。

## 主数据与布局

Component ID、组合ID、列号均为int。Archetype持有类型位图、紧密TypeIds、列描述和方向边；Chunk持有实体行及按TypeId直接索引的32 B ColumnEntry。ColumnPlusOne表达布局中的列及紧密元数据下标，GroupPlusOne表达分组下标；Shared编号保存在该行的int数组中。完整字段见[[Data-and-Dependencies]]。

默认每表一个可增长Chunk。World按POD类型持有固定地址字节缓冲区，各Chunk保存其中的行段；Rows缓存行段地址，Values引用主缓冲区。含引用值使用各Chunk的T[]。容量、对齐、增长重绑和释放由[[Construction]]持有。ColumnPlusOne表达布局中的列，Presence表达各行的实际存在性；Tag由类型标记表达，Shared主值由SharedValues持有，行内保存共享编号。

类型位图保留 256 bit 内联部分与按实际高位增长的 ulong[]。相同泛型组合在进程内建立一次 ID/类型序列/位图；World 数组保存该组合的实际表绑定。普通列目录、查询目录和边目录均按实际编号增长。

## 身份与普通值

以下为有效身份、已有普通列、追踪/索引关闭、容量充足。D 的 Entity 是当前 World 内的 8 B 身份；公开入口接收 World。S-E Read/Has/Set 从原生 Entity 进入；IsAlive 使用已有 World 的 EntityId 入口。局部构造及闭合类型归约规则见 Cost-Model。

```csharp
// D 设计伪代码：每次读取数组字段先绑定局部。
var records = world.Entities.Records;                // P1
if ((uint)(entity.Id - 1) >= (uint)usedIds) Fail();     // B1，Used为已分配过的正Id数
ref var record = ref records[entity.Id];              // A1
if (record.Generation != entity.Generation) Fail();  // B1
var chunk = record.Chunk;                            // P1
var columns = chunk.Columns;                        // P1
if ((uint)typeId >= columns.Length) Missing();        // B1
ref var column = ref columns[typeId];                // A1
if (column.ColumnPlusOne == 0) Missing();             // B1
return ref Row<T>(column, record.Row);               // Rows/Values P1，行 A1
```

Id从1开始，0为Entity.Null，Used为已分配过的正Id数量，记录数组预留索引0。死亡记录在释放时推进Generation，复用时发出该记录的新身份；Has在列项处返回，TryGet沿同一入口输出值。有效空闲数量为Used−EntityCount。

| 等价入口 | S-E 〈P,A,B,W,L〉 | D 〈P,A,B,W,L〉 |
|---|---|---|
| World.IsAlive，当前代有效 | 〈2,2,3,0,0〉 | E+R=〈2,1,3,0,0〉 |
| 普通 Read / 关闭追踪的 Get | 〈7,3,5,0,0〉 | 〈5,3,5,0,0〉，含E |
| 普通 Has | 〈6,2,6,0,0〉 | 〈4,2,5,0,0〉，含E |
| TryGet 成功并输出值 | 〈7,3,8,1,0〉 | 〈5,3,5,1,0〉，含E |
| Set已有普通值 | 〈12,6,10,3,0〉 | E+SetCore=〈5,3,5,1,0〉；实际追踪另计 |
| 空实体 Create，复用 ID | 〈7,4,4,10,0〉 | 〈4,2,4,8,0〉，含E |
| K≥1 列 Create，复用 ID/组合命中 | 〈11+3K,5+3K,6+K,10+K,0〉 | 〈7+K,3+2K,7,8+K,0〉，含E |
| 尾行 Destroy，POD/基础能力 | 〈12,4,18,11,0〉 | E+X=〈6,1,8,7,0〉 |

S-E 的重复字段读取在本轮保留：IsAlive 的 meta[id] 两处定位；Create 的 Slots.Length 与 Slots 行基址两次读取；缓存边的 _addEdges.Length 与 _addEdges[id] 两次读取。JIT 合并属于实现阶段；上述源级数字直接替换前序 R 聚合。

S-E Set 的分段和：handle World/空值〈1,0,1,0,0〉 + G + Live〈3,1,3,0,0〉 + AddBit 已有列〈4,2,4,0,0〉 + Row 定位/写〈4,3,1,1,0〉。空追踪/索引/分组调用按闭合类型归约。D 一次定位后直接写行，G 每公开操作一次。

S-E 空 Create 的分段和：G + AllocateId 复用〈1,1,1,2,0〉 + 空表 P1 + Place〈4,2,2,6,0〉 + 返回代数〈1,1,0,0,0〉。K 列版本增加边获取〈4,1,2,0,0〉与 K·〈3,3,1,1,0〉。D 绑定记录与行数组，整项写 16 B record；复用死亡记录的 Row 连接空闲链。CreateKnown(K≥1) 在已绑定目标时为〈3+K,2+2K,4,8+K,0〉；其四个判断为结构范围、空闲链选择、目标容量、已分配启用状态。

S-E 尾 Destroy 分段：Entity 包装〈1,0,1,0,0〉、PrepareCascade B1、G、当前 record 检查〈2,1,2,0,0〉、HasCleanup〈1,0,1,0,0〉、Free〈7,3,9,9,0〉、Orphaned/FollowLinks 关闭路径〈1,0,3,0,0〉。D X 的七个判断为 G、身份两项、表 ReleaseKinds、Sparse 首项目录、销毁观察者、尾行选择；写入为 record/源 Count/EntityCount/freeHead/结构版本五项及 G 两项。

源码：[Entity](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/Entity.cs#L179)、[World.IsAlive/AllocateId](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.cs#L373)、[SetRow](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Components.cs#L345)、[创建生成器](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Generator/ArityGenerator.cs#L167)、[Destroy/Free](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.cs#L213)。

## 方向边与实际搬移

边按源Archetype和操作组合Id索引，保存目标Archetype、最近可写TargetChunk、共有POD描述与附加转移数据。Add/Remove/混合组合各指向最终表；反向操作首次发生时建立自己的边。热路径使用TargetChunk，满块时由目标表选择可写块并回写缓存。

| 已给源表/操作 ID，容量足、边命中 | S-E | D |
|---|---|---|
| 取得边及目标 | 〈4,1,2,0,0〉 | 〈3,1,2,0,0〉 |
| 首次发布边，数组已取得 | 〈0,1,0,1,0〉 | 〈0,1,0,1,0〉 |

D 的三个引用是目录、边、目标 Chunk；两个判断是范围与空边。POD 步骤为 {TypeId,Size} 8 B，纯 Add/Remove 复用共有一侧的只读描述，混合增删建立交集。

MoveKnown(k) 输入为已验证 record ref、源/目标 Chunk、实际边，结构范围已经打开。普通 POD、源尾行：

| 阶段 | D 〈P,A,B,W,L〉 |
|---|---|
| 目标容量、写身份、Count | 〈1,1,1,2,0〉 |
| 两端列目录/步骤绑定、k 列复制定位 | 〈3+2k,3k,k+1,0,0〉 + ΣByteCopy_T(1) |
| 附加转移入口检查 | 〈1,0,1,0,0〉；有内容时加入实际启用/追踪/托管步骤 |
| 源 Count、尾行选择、record 与结构版本 | 〈0,0,1,3,0〉 |
| MoveKnown 合计 | 〈5+2k,1+3k,k+4,5,0〉 + ΣByteCopy_T(1) |

迁移主体M(k)=G+R+源Chunk/Archetype P2+边定位+MoveKnown，得到 **〈11+2k,3+3k,k+9,7,0〉+ΣByteCopy_T(1)**；公开入口E+M为〈12+2k,3+3k,k+10,7,0〉+复制。源中间行再加入[[Bulk-and-Copy]]的FillHole；实际新增值、删除值与索引另加。Shared/GroupBy直接把已取得的目标传入MoveKnown，已有局部基址沿用。

Remove的公开完成点为Has=false、退出相应查询，并释放值或将其转交Removed<T>。现有retainRemovedColumns选项持有两种物理策略：保留列时复用已有列和Presence，清除该行存在位，后续Add/Set恢复该位并按新增登记；迁移策略沿Remove边进入实际最终布局，并保留共有值、版本、启用位、身份与缓存更新。普通Comparison World统一选择保留列策略，两种规模及四阶段沿同一配置；Core的迁移选项继续提供原能力。批次已确定最终布局时合并到一次迁移。两侧按相同公开结果比较，迁移、填洞与各自保留的内部列容量进入时间及内存账。启用位与存在位的共同子内核、保值禁用的完整入口分别按下一节配对。

## Enableable

组件存在性由 Archetype 类型集合和 ColumnPlusOne 表达；已声明 Enableable 的列再用一位表达启用/禁用。这是两个已有层次各自的状态：缺列走普通布局路径，列内只有两态。已有测试 `EnableableComponentTests.Disable_AndEnable_DoNotAffectComponentExistence` 对应 Has=true、禁用后 Read 保留原值。

Enabled=null 表示该列全部活跃行启用；第一次 Disable 时分配位数组并填有效位，同时加入 Chunk.ActiveEnabledTypes。DisabledCounts 按紧密列号访问，首次需要时分配 K 项。每列额外空间为 ceil(c/64)×8 B，加该 Chunk 首次建立的 4K B 计数目录。

| 操作 | 列布局 | 值与启用位 | 查询与索引 |
|---|---|---|---|
| Add / 缺列时 Set | 迁移到包含 T 的表 | 写值，初始启用 | 建立成员，登记 Added/Written |
| 已有列 Set | 保持原表 | 替换值，沿用原启用位 | 启用时维护值索引，登记 Written |
| Disable / Enable | 首次需要时加入EnableGroup<T>并迁移；后续复用该布局 | 保留值，置0/置1 | 摘除/加入活动索引成员，登记 Written |
| Remove | 清存在位，或合并迁移到最终表 | 释放值或转交Removed<T> | 摘成员，保存实际开启的移除事实 |
| Has / Read | 读取列存在性/值 | 禁用值仍可读 | 读取存储事实 |

All/Any/Without 对 Enableable 使用活动成员资格；Disabled<T> 要求列存在且位为0。根节点可由 Without<Parent<D>> 选择。Optional 返回列存在与启用状态，调用者据此读取已存值。频繁加入/离开查询集合使用 Enable/Disable；值寿命终止使用 Remove。

| 位切换子内核：已知列号/行、位数组已存在、状态实际改变 | S-E Present | D Enabled |
|---|---|---|
| 置1并减缺失/禁用计数 | 〈3,3,2,2,0〉 | 〈2,2,2,2,0〉 |
| POD 置0并加缺失/禁用计数 | 〈3,3,3,2,0〉 | 〈2,2,2,2,0〉 |

Source的Present二级引用/定位与Absent数组分别计费；D使用已取得的Chunk.Enabled、ColumnPlusOne和DisabledCounts。单行状态改变执行一位读改写与计数加减；整词批量调用PopCount。状态保持时在位判断处返回。已给同样“位数组存在”的输入，上表保留通用入口中的空值分支，两侧一致。

公开Enable/Disable已有位数组、状态确实改变、追踪/索引关闭时：E+G+R+Chunk/Columns/列检查〈2,1,2,0,0〉+位切换，合计 **〈6,4,8,4,0〉**。Set已有启用列的位保持原值；关闭索引时沿普通值写路径。首次Disable在当前布局缺少EnableGroup<T>时，通过既有AddKnown进入最终组合，计入目标表/容量建立、共有列迁移和行位置更新，再加[[Construction]]的位数组/计数目录构造。该内部组标记保持后续启用状态切换的布局，Enable/Disable返回void，状态由IsEnabled读取；重复同状态在位判断后返回。

普通、独立追踪关闭、共享与索引关闭的类型，在身份、列及首次EnableGroup准备后直接进入同一SetEnabled位内核，由它完成一次状态判断、位更新与DisabledCounts修改。带追踪、索引或共享成员的类型继续在实际状态改变时执行对应维护，再调用该位内核。

迁移共有 Enableable 列时读源位，null 视为1；目标首次接收0时建立位数组，然后写目标位并调整目标禁用数。移出源行时按其位减源禁用数，填洞复制末行位，尾位清0。新增行显式置1；整表 Count=0 时清禁用数并复用位数组。Clone/存档复制列布局、保留值和位数组；恢复以有效行 PopCount 重算禁用数。

S-E的Disable/Enable维护T存在位及Disabled<T>标签，首次标签可能迁移；每次还有标签位、原生身份/结构入口及实际索引/版本。上表配对相同掩码子操作。完整Has/Read保值访问采用[[Adaptation]]的Stored<T>加禁用标签：T始终从原生Stored列读取，活动查询按标签筛选，状态切换计实际原生标签操作。普通Remove沿前节的相同公开完成点配对。

## 查询描述与首次匹配

固定泛型 Query 描述为静态组合 ID，重复构造 L0。动态 With/Without/Any/Added/Changed 描述持有内联位图；高类型编号使用不可变长尾。首次动态描述去重使用内容键，重复 World 绑定使用查询 ID 数组。长尾按 w 个实际词复制、比较；静态组合在首次注册完成这些工作。

表级匹配处理类型集合：With 普通列与普通排除条件用位图；Enableable 的实际存在、排除、Any 和变化条件编入行过滤。Optional 记录列是否存在。Any 已由常在普通列满足时，匹配项选择整表路径。实际词读取、短路条件与过滤绑定见 [[Construction]]。

## 查询缓存：增量检查、容量追加

每个QueryData保存CheckedArchetypes、CheckedMatches、FilterVersion、Match[]、Count与AllRows。同一对象的QueryCoverage基类字段区集中保存多列缓冲区引用、请求类型标识和AllRows，热入口直接读取这些状态。Match为{Archetype,Filter}16 B，每表一项；普通整表Filter为空。World的int QueryMatchVersion在新表追加、首次存在位/启用位建立及恢复后递增；QueryVersion表示需要重建既有行过滤描述的变化。ArchetypeStore保存All/Count与HasMultipleChunks，任一表增加第二块时将HasMultipleChunks设为true。

Query值直接持有World、共享QueryData、单类型缓冲区借用引用及独立Since，共32 B。QueryData由该World的查询目录持有，刷新接收Query给出的World；多列热入口直接从QueryData取得覆盖状态和缓冲区引用。首次单列连续值绑定把引用写入QueryData和当前Query，后续构造的Query从共享绑定取得同一引用。复制Query继续共享匹配结果、类型绑定与主缓冲区，各自推进Since。

热路径比较CheckedMatches与QueryMatchVersion。冷路径锁住QueryData并重读版本：过滤版本变化时从首表重建，否则只扫描新增ΔA张表并追加命中。刷新结束计算AllRows=全部表均命中且每个Filter为空，最后release发布CheckedMatches。范围取得结构读后读取HasMultipleChunks：单块路径取得PrimaryChunk，多块路径读取每表当前Chunks/Count。已有表新增块随下一次范围可见。

已建立连续多列绑定或单Chunk连续值绑定的QueryData在World.RefreshQueryMatches中同步刷新。新表登记、首次存在位/启用位建立和恢复在发布相应布局后推进QueryMatchVersion，再刷新这些查询；其余查询继续在下次读取时刷新。连续多列的ValueTypes仅在AllRows验证成立时发布，刷新为部分匹配时清零。热入口核对该标识后，由首列缓冲区核对实际值行段完整性。两条路径共享同一份匹配项、过滤描述和覆盖状态，字段及容量保持原布局。

```csharp
// D设计伪代码；访问范围与冷路径锁由各自入口持有。
if (Volatile.Read(ref query.CheckedMatches) != world.QueryMatchVersion)
    query.Refresh(); // 重建或增量匹配，计算AllRows，最后发布CheckedMatches。
```

热路径先acquire读取CheckedMatches；相等后取得Matches/Count与AllRows。查询目录首次建立由目录锁处理。整个绑定与遍历共享同一个结构读取范围，具体访问顺序见[[Jobs]]。

| 相同事件 | S-E 源路径 | D 设计路径 |
|---|---|---|
| 热固定查询绑定 | 四路 memo 首路读取、过滤相等 | 目录〈2,1,2,0,0〉 + 新表判断 B1 |
| 新表登记 | 新数组，复制全部 A 个表引用 | Append〈1,1,1,2,0〉；满时 Grow |
| 已有 r 项的匹配结果追加 | L3；复制两数组各 r 项，共16r B；写两末项与对象引用 | Append〈1,1,1,2,0〉；满时 Grow(16 B Match) |
| 新表到查询可见 | 扫全部已绑定查询、逐个匹配、立即复制命中结果 | 各实际使用的查询首次扫描 ΔA；累计相同匹配对，每查询进度 W1 |
| Chunk值数组增长/固定块增加 | 原表提供当前列/块入口 | Match指向Archetype；范围开始读取当前块和列基址 |

源码：[QueryMatch.Append](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/Query.cs#L444)、[新表发布](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Archetypes.cs#L142)。D 新增 r 次时，倍增数组累计复制 O(r) 项；逐次精确长度复制累计 O(r²) 项。完整观察点为“新表建立后，相关查询全部打开一次”。

## 已绑定后的遍历、Count、Any

R个Match为默认单Chunk的普通整表条件，合计覆盖部分World行；M行、K个输出值列。HasMultipleChunks在入口判断一次，ArchetypeStore沿用查询绑定的引用。查询生成器按回调是否消费Entity选择值列路径或带身份路径。查询绑定/刷新由上节承担，用户计算单列，两侧持有相同读取范围。

| D 步骤 | 〈P,A,B,W,L〉 |
|---|---|
| 打开/结束读取范围 | 〈0,0,0,2,0〉；并行范围按实际两次原子登记 |
| Match基址、R表及PrimaryChunk | 〈1+2R,R,R+2,0,0〉；含一次HasMultipleChunks判断 |
| 每 Chunk 绑定 K 个值与 Entity | 〈2+K,K,0,0,0〉；Columns、Entities、K 个 Rows/Values |
| 每 Chunk 行循环 | 〈0,N_t(K+1),N_t+1,0,0〉 |
| 仅值列遍历主体 | **〈1+R(3+K),R(K+1)+MK,M+2R+2,2,0〉** |
| 带Entity遍历主体 | **〈1+R(4+K),R(K+1)+M(K+1),M+2R+2,2,0〉** |
| Count，普通整表主体 | **〈1+2R,R,R+2,0,0〉**；累加PrimaryChunk.Count |
| Any，第j项首次非空主体 | **〈1+2j,j,2j+1,0,0〉** |

本表将 K 个组件行定位计入循环。S-E 对照使用 ForEachBatch 固定结构路径；实际行项同为 M(K+1)，外层的 Mode/AliveCount、列号映射和两平行数组分别保留。源位置：[ForEachBatch](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Generator/ArityGenerator.cs#L318)。

Enableable 按词合成有效行掩码；Changed/Added 先读列/块/词摘要，再检验实际行版本；Count 对掩码 PopCount，Any 在首个非零词结束，行遍历按置位读取身份和值。Optional 每 Chunk 绑定一次存在性；Enableable Optional 逐行读取对应位。固定 Chunk 每表先读 Chunk 目录，再对实际 C 个 Chunk 使用相同内核。完整过滤操作表见 [[Construction]]。

同步Core的Count/Any先刷新查询；AllRows成立时分别读取World.EntityCount和其非零状态。其余情况进入上表的匹配/过滤路径。短入口与持有访问、try/finally的通用主体分别闭合，使全体行查询直接消费已建立的覆盖状态。连接执行时沿统一访问范围读取。

## 连续值视图

ForEach按实际Chunk或过滤后行段调用，Open提供带身份与列访问的范围。调用方需要自行组织普通值上的连续计算时，使用TryGetValues取得span；签名、首列可写规则和结构寿命由[[Implementation]]持有。

该入口首次完成同步Core选择、类型写入能力选择与查询刷新。多列POD绑定保存实际缓冲区，AllRows和首列完整性同时成立时，按共同Chunk行段取得相同长度的连续列。通用单列路径按World.EntityCount核对保留行段长度。单个匹配表且该表只有一个Chunk、Filter为空时，直接从该Chunk取得实际Count行的列视图。单列成功后把Chunk与请求TypeId保存到QueryData；后续调用读取当前Count和Columns，并核对该表仍有一个Chunk。匹配刷新同步更新这个借用引用。其余情况返回false，调用方继续使用ForEach/Open。

纯With<T>的单类型普通POD查询另复用该类型缓冲区的完整性状态。首次绑定把缓冲区登记到现有CachedComponent维护列表，并扫描各行段：沿原Presence规则检查，从0开始累加活行长度，每个非空段的Start须等于当前长度；成功时记为Full并缓存FilledRows，其他情况记为Partial。空尾与部分尾的保留容量沿主缓冲区继续持有，视图使用实际FilledRows。Bind/BindRange/Remove/RemoveRange、容量变化、存在位建立与Clear把状态改为Unchecked；下一次取得视图按当前行段重算一次。新增Archetype登记已观察类型。状态与4 B FilledRows由POD主缓冲区唯一持有，成功扫描写长度W1，最终释放归零W1；热视图复用该长度。混合World中其他类型实体沿自己的主存储存在，T视图包含完整的T成员。

World的执行连接和组件存储/修改种类在绑定后固定。首次单类型绑定完成这些选择，后续相同类型直接读取缓冲区完整性。两列、三列POD绑定要求请求类型均为查询必需列，在QueryData保存对应缓冲区引用，并观察首列的完整性；匹配在布局变化时刷新，全行有效性随类型标识发布，热调用核对该标识和首列完整性。AllRows保证每个Archetype均包含请求列，各列按相同Chunk容量建立和增长，因此共享保留行段的起点、容量和实际行数；首列完整时，以其长度生成对应span。请求类型及顺序由ValueTuple<T1,T2>或ValueTuple<T1,T2,T3>的RuntimeTypeHandle标识，每次调用先核对当前标识。绑定保存缓冲区对象，增长后的Rows由对象提供；实际span在每次调用时重新生成。span生成入口读取当前Rows和实际长度，普通连续访问由调用方的相同值计算内核消费。连续范围达到256 KiB时，SSE平台在交给调用方前预取头部一条缓存线；较小范围直接返回span。首次绑定、完整性扫描、匹配刷新、结构失效回调及增长重绑均进入所属阶段。

首列使用普通可写类型；受控值、索引/分组值和追踪写由相应修改入口处理。后续列使用只读值视图，共享值沿其编号读取入口处理。连接执行、存在位、启用/变化过滤按ForEach/Open取得实际访问与行段。调用方每次使用时重新取得视图；下一次结构修改包含缓冲区增长、迁移与销毁，按同一结构寿命结束借用。

完整链路为World.ReserveRows/创建→PodColumnBuffer建立行段→QueryData刷新并发布AllRows→TryGetValues绑定缓冲区或单Chunk→调用方消费span。已有ForEach仍保留逐Chunk回调与相应版本登记；连续视图将跨Chunk聚合的选择交给调用方。查询比较的绑定、增长后观察及回收后观察统一先尝试该连续入口，再沿原ForEach处理其余范围；256行用户切片与相同计算内核保持原定义。

Tag通过Count、Any和带身份的查询范围提供成员信息；连续值接口处理具备实际值存储的列。纯单类型选择同时要求Required只有该类型，Excluded、Any、Disabled、Changed、Added、Shared和Groups均为空；OptionalReads沿访问声明处理。

QueryData保存单类型缓冲区引用，以及当前多类型绑定的三个缓冲区引用和一个nint类型标识；两列绑定把第三个引用设为空。匹配刷新和列绑定由同一对象持有。类型/执行选择只在首次绑定或请求类型组合改变时发生；每次公开调用重新核对缓冲区完整性。完整性以FilledType保存：−1表示待扫描，−2表示存在未填满的范围，其余值为完整缓冲区的TypeId。单列热读取核对请求类型；多列ValueTuple标识命中后复用已绑定类型，以FilledType非负确认Full，沿原完整性扫描处理负值。随后生成当前Rows/FilledRows的span并沿原规则预取；结构缓存回调把FilledType置为−1。该复用保持字段、容量与分配原状。

Systems构造范围时已通过Query.GetMatches刷新匹配，同一结构读取范围中的CoversMatchedTypeLayouts复用这份观察。类型位检查及非匹配非空Chunk扫描沿原算法执行；typed2登记按两个类型复用该前提，省去两次重复匹配版本读取与比较。结构寿命、权限粒度、过滤与范围边界保持同一调用链。

已绑定POD查询的ForEach在同步Core、AllRows成立且请求类型与当前绑定一致时，复用首列缓冲区的行段目录。结构读取范围保持各列基址有效，循环一次绑定各列Rows，按行段StartRow和当前Chunk.Count生成本次span，再调用原有IComponentAction。块容量、实际行数、回调边界和用户值写入沿现有ForEach语义处理；部分填满的块继续通过各自的有效行范围参与遍历。两列、三列沿共同StartRow分别乘各自sizeof(T)，单列沿相同目录处理。

连接执行的两列、三列ForEach先取得现有查询资源访问，再进入Core读取范围。AllRows成立、首列为普通非追踪写POD且各只读列为实际POD值列时，从World已有类型缓冲区取得相同行段目录，沿上述逐Chunk内核调用用户动作。查询过滤、追踪版本和受控值沿对应的匹配行段路径处理；公开连续值借用继续采用自己的访问条件。

单、双、三列ForEach在OpenQuery成功后，沿WorldExecution的内部默认false判定检查本次结构读取保护。WorldJobs当前frame属于同World、同JobDepth且StructureWrite=false时，复用该frame的结构Read寿命；Core本次reader计数由一项局部bool配对借用，节省BeginRead/EndRead的两次原子操作。外层结构Write及其他执行连接沿原reader计数保护回调行范围。资源登记、等待、值权限、逐Chunk回调和最终访问释放沿原顺序，新增持久字段与容量0、热分配L0。

上述连接执行的两列、三列POD分支复用QueryData已有FirstValues/SecondValues/ThirdValues引用：各列先读局部，按缓冲区readonly TypeId核对请求类型，缺失或类型改变时沿原PodBuffer目录取得并写回。ValueTypes保持原连续借用发布规则；本次遍历只消费已核对的局部引用与当前Rows。增长、Clear、Restore沿同一缓冲区对象更新，查询Release清引用。首次绑定写入两项或三项引用，热调用省相应目录定位；字段、容量、分配及维护回调增量均为0。

连接执行的快段直接位于持有WorldAccess及reader保护的ForEachWithExecution内：OpenQuery、类型能力检查、借用/BeginRead、匹配刷新、已绑定POD逐Chunk回调、reader finally及访问Dispose沿同一次调用完成。冷匹配继续使用原NoInlining入口，同步Core及单列路径保持原分派。合资格两列/三列热调用省一次NoInlining匹配函数边界；字段、容量、分配、数据读写、回调及锁/原子次数保持原值。

## 选型结论

直接类型目录服务随机定位；方向边服务重复结构操作；位图服务首次集合判断；紧密行和容量匹配数组服务已确认的遍历规则。默认 Chunk、固定 Chunk、Enableable、变化筛选与 Sparse 混合查询分别在各自能力组核算。目录清零、首次列建立、冷去重与过滤绑定见 [[Construction]]；资源、索引和事实在 [[Execution]] 的实际阶段相加。

## 已绑定类型的随机访问

World.Read/Get沿实体记录与列目录定位。OpenRead<T>/OpenWrite<T>为批量随机访问绑定该类型的位置缓存和访问范围，首次使用时建立目录，后续范围复用；主值继续由列缓冲区或Chunk持有。OpenRead对Shared绑定共享主值数组及行编号，OpenWrite按普通可写类型规则开放。两个入口复用同一份CachedComponent<T>。

下面两列完成同一个含q次读取的批次。点入口逐次定位；类型范围在段外绑定结构/值访问和目录，段内复用。首次范围构造与后续维护进入完整周期。

| 相同读取批次 | World.Read/Get点入口 | 已绑定类型范围 |
|---|---|---|
| 首次数据准备 | 使用已有EntityRecord与列目录 | 扫实际T行，建立字节偏移/代数或typed位置目录 |
| 单次定位 | R+V，见本页身份路径 | POD目录已绑定时，检查读取为A3/B2，ReadKnown/GetKnown为A2/B0，均含实际值读取 |
| 保留位置数据 | 既有EntityRecord | POD为8I B，含引用类型为16I B，按实际使用类型建立 |
| 行移动/移除维护 | 更新EntityRecord与实际列 | 同时更新地址/位置，移除时清代数 |
| 全局POD缓冲区扩容 | 复制UsedRows并发布新基址，重绑各Chunk列地址 | ByteOffsets保持相对偏移；下一范围从同一缓冲区取得新基址 |
| Chunk行段或typed列增长 | 发布该块新行段/数组 | BindRange重绑该块实际n行的位置项 |
| 清理与恢复 | 按主存储处理 | Restore刷新已建立目录，World释放时回收；范围结束释放访问 |

含引用T使用{T[] Values,int Row,int Generation}数组，每项16 B。POD采用分离的int[] ByteOffsets和int[] Generations，总计8I B；字节偏移相对同一类型PodColumnBuffer.Rows，按实际活跃行填充，代数数组首次清零。缓冲区容量以int字节数表示，偏移沿同一容量空间保存。缺失项Generation为0；Read/Get检查正Id范围及代数后取得值，ReadKnown/GetKnown消费调用方已经建立的身份与组件存在性。Has在已绑定代数目录上执行A1/B2。范围开始一次绑定类型缓冲区基址及两个目录的首项ref，段内按Id读取字节偏移并与基址相加；结构读取范围保持这些数据有效。

两份POD目录数组由CachedComponent<T>从ArrayPool<int>.Shared取得，实际保留容量按租用桶长度计入。首次清整个Generation桶；扩容复制旧有效目录并清新Generation尾部，随后归还旧数组。World.Clear沿原Clear保留数组并重置Generation，World.Dispose沿按类型目录调用内部ReleaseData，归还两数组并结束其字段引用；含引用T的目录同样在最终释放时结束数组持有。PodColumnBuffer的该内部入口复用原有Release。借用范围继续持有World访问至Dispose，池归还发生在既有最终完成点。

缓存首次建立在BeginValueRead取得的结构/值范围内使用World的查询绑定锁，并在锁内重查类型目录、分配位置数组、登记实际表的紧密缓存列表、填充全部活跃行，最后写入类型目录。首次查询注册复用同一个World锁；后续范围直接取得已建立目录。结构读取范围固定表和行，结构写入沿统一访问规则完成迁移、填洞、新增、移除和列增长，并维护这些表实际登记的紧密缓存项。新增Archetype登记已建立的类型。

QueryStages已持有本次任务的Core读取范围，具体回调执行时同World的HasRangeWrite上下文同时有效。此时内部的缓存读取保留RequireValue<T>(false)，并在这份外层保护内取得或惰性建立缓存，借用其reader及访问frame寿命；普通读取入口和写入继续沿BeginValueRead/EndValueRead配对。CachedReadLookup以release标记配对Dispose，x64沿World 8 B、Used 4 B、两bool与2 B对齐、四byref 32 B保持48 B，新增标记赋值W1，解除时B1，L0。范围内读取的reader增减及frame建立由外层任务承担一次，逐身份代数/组件存在性检查沿原读取内核执行。

完整周期按B+q·C_cached+m·C_update与q·C_direct比较：B是构建/初始化/发布，q是实际随机访问数，m包含各次行移动及列增长影响的行数。选择类型范围的条件为q·(C_direct−C_cached)>B+m·C_update，各C同时保留源码向量与实测时间。点入口服务逐项定位，类型范围服务重复随机访问。首次构造及后续维护费用分别进入[[Acceptance]]。

两种范围均使用[[Jobs]]相同类型值/摘要资源；普通值写通过对应WriteLookup，索引及受控关系通过其修改入口。范围与方法签名见[[Implementation]]。

普通WriteLookup直接组合已有CachedWriteLookup，复用其位置绑定、写入版本、Has与借用结束。写范围持有所需的缓存写数据；ReadLookup持有共享值读取所需的只读分派数据。公开Get、GetKnown、Set、Has和Dispose沿各自原有签名调用同一缓存写内核。

两种写范围均提供`SetKnown(ReadOnlySpan<Entity> entities, in T value)`。调用方提供本范围内已确认身份及组件存在性的实体集合，入口复制共同值一次，绑定目录首项及类型缓冲区基址，按输入顺序写入；追踪类型沿同一写版本逐项登记，含引用类型沿实际typed位置写入。POD批次每次展开四项，剩余项沿同一定位处理。

CachedComponent<T>在绑定实际行时同时维护RowsFollowIds：每项字节偏移等于`(Entity.Id-1)*sizeof(T)`时保持连续编号状态，出现实际行迁移产生的不同偏移时转为一般目录定位。World.Clear沿现有缓存Clear重置该状态，后续绑定重新形成事实。读取范围在结构访问保持期间取得这一状态；普通POD的SetKnown批次在段外选择连续编号路径或字节偏移目录路径。连续编号路径按Id直接定位，目录路径沿既有ByteOffsets定位，两者共用实际主值、代数与迁移回调。输入创建顺序与已预留类型行段顺序对齐时，自然形成连续编号布局。

BindRange在RowsFollowIds为true时逐项核对对应关系，首次出现不同偏移时写入false，剩余行沿统一的ByteOffsets/Generations绑定循环完成。已经进入一般目录定位的类型直接执行该循环；单行绑定沿同一状态转换维护。状态由实际行绑定形成，每次转变写入一次，范围中每行继续绑定自己的偏移与代数。

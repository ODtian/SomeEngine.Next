# 批量、填洞、复制与模板规范

分析时间：2026-10-22 18:57 +08:00。状态：D 批次算法；S-E 创建/整表释放和复制源码已复核。

分析方法：把批次前的容量/类型绑定、逐实体身份和逐列数据工作分开；完整入口由 [[Execution]] 组合，计数使用 [[Cost-Model]]。

## 批量创建

单实体组合Create沿用同一类型化入口，7至10类型的重载接收完整组件值，Reserve重载接收相应完整类型组合及容量。每个闭合TypeSet缓存自己的类型编号。入口先执行各类型的创建验证，再沿Find(TypeSet)和CreateKnown取得身份与行；随后逐列WriteCreatedValue、维护已激活索引、安排实际分组并完成各类型初始化。普通值、独立克隆、Shared编号、Buffer及Tag继续消费各自既有类型操作。

调用方已经给出完整组合时，在该次Create中写齐这些值。混合World的八个基础组件及两个可选Tag分别沿8/9/10类型入口建立；首次建立与增长均计入实际列分配、Shared归并、索引及分组维护。分组目标的容量由实际组建立过程准备。

输入为 M>0 个输出身份、K≥1 个普通 POD 列、同一值或已给出的逐行值，目标组合已缓存、ID 全部复用、容量充足、追踪/索引关闭。

D 在批次外完成 G、目标绑定、实体/行 Reserve、列基址绑定。通过 Used−EntityCount 得到可复用 ID 数，先遍历这一段空闲链，再顺序分配新增 ID；使用两个循环把“空闲还是新增”的选择移到段外。记录数组在 Reserve 后取得，空闲链头和各 Count 在批次末发布。

| 阶段 | S-E 〈P,A,B,W,L〉 | D 〈P,A,B,W,L〉 |
|---|---|---|
| 完整同值 CreateEntities | 〈7+6M+3K,1+5M+K(M+2),4M+7+K,9M+2+MK,0〉 | E+〈6+K,1+3M+K(M+1),M+8,3M+7+MK,0〉 |
| 数据填充 | Fill_T(M)，K 次 | Fill_T(M)，K 次 |
| 填充控制 | ΣKfill(M,T) | ΣKfill(M,T) |
| 输出身份宽度 | 原生 Entity 含 World，16M B | 已给 World 的 Entity 身份，8M B |
| 逐行不同值 | 使用实际逐项输入/对应批量入口，另计 M 个源值读取/列 | 将 Fill 换为 Copy_T(M)，每列增加 A M |

D 五个批次字段发布是 FreeHead、Used、EntityCount、Chunk.Count、结构版本，加 G 两写；每实体写 record/行身份/输出身份三项。K 列的目录读取 K 次、基址读取 K 次，值写 MK 次。表内8个固定判断为 G/组合两项/两个容量/启用状态/两个循环结束；复用 ID 循环另有 M 次条件。

S-E 外层是 G、空表/边/目标 P5、两个 Reserve P2、IsEmpty 和边/容量判断；每实体 AllocateId/Place/返回身份的 P6/A5/B3/W9；每列 Base 的 P3/A2/B1，再加 Fill。源码：[ArityGenerator:192](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Generator/ArityGenerator.cs#L192)、[PlaceRows](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.cs#L171)。

新 ID 段使用同一记录/行数组，写初始代数。首次组合、目录增长和 Chunk Reserve 分别追加 [[Construction]]。Buffer、Shared、索引、Sparse 和追踪按实际能力加入一次。

批量Create的三、四、五类型重载共用AllocateCreatedRows与CompleteCreatedRows：前者预留记录/行、分配身份并写record/行/输出；闭合类型方法对各实际值列执行Fill；后者集中发布计数、创建版本、存在位和已建立的位置缓存。索引与分组在值完整后接续建立。固定块按每块可写余量切分输出span，重复使用同一内核。

四/五类型重载用于直接建立带Tag的最终组合。调用方给出完整类型和值，值列按实际类型填充，Tag由列元数据表达。Shared、独立值复制和初始化声明沿已有逐项Create语义处理。该扩展沿用批量Create的输入/输出与生命周期，使不同最终组合共同消费同一行分配内核。

逐行不同的首列使用ReadOnlySpan<T1>重载，其余列继续接收共同值；泛型列顺序由调用方选择。该入口复用上述身份分配与完成内核，首列按输出行数Copy，其余实际值列Fill。固定块按相同offset/count切分输出与首列输入，保证行和值逐项对应。首列的索引按每个输入值建立，其他索引与版本沿共同值批量路径完成。

该重载覆盖一个变化列配多个共同值的批次；输入数组由调用方提供，批次直接把给定行值写入最终列。签名如下，四/五类型继续追加相应共同值参数：

```csharp
void Create<T1, T2, T3>(Span<Entity> entities, ReadOnlySpan<T1> first,
    in T2 second, in T3 third)
    where T1 : struct where T2 : struct where T3 : struct;
```

## 批量修改与删除

双列变化输入使用同一家族的四至六类型Create重载；四类型签名为`Create<T1,T2,T3,T4>(Span<Entity> entities, ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, in T3 third, in T4 fourth)`，五/六类型依次追加共同值`in T5 fifth`及`in T6 sixth`，各T约束为struct。两个输入span按同一行号与输出身份对应。入口绑定目标组合一次，沿AllocateCreatedRows分配行，前两列分别Copy，其余实际值列Fill；具有IComponentOperations<T>的输入逐值调用同一ComponentType<T>.CloneValue，共同值也为每个目标独立克隆，再由CompleteCreatedRows完成身份、版本与位置缓存发布。实际索引在段外绑定一次，按对应输入值执行Added；分组在值完整后建立。Shared和初始化声明复用既有逐项Create链，独立值沿上述批量列准备中的逐值克隆完成。固定块按同一offset/count切分两个输入span及输出span。

变化值批量修改的签名为`Set<T>(ReadOnlySpan<Entity> entities, ReadOnlySpan<T> values) where T : struct`。GroupBy处于激活状态且ActiveIndex为空时，复用[[Indexes]]的分组批次；其余类型按输入顺序消费既有Set入口。该重载表达同一批身份与逐项值，各项现有写入、投影与事实规则由实际类型操作持有。

对应的直接顺序移除使用同名重载`Remove<T>(ReadOnlySpan<Entity> entities) where T : struct`。入口一次取得结构写范围、验证结构与类型移除规则，然后按输入顺序逐项Resolve并调用既有RemoveValueKnown。每项继续产生自己的Removed旧值与版本，维护索引、Shared、存在/启用位、缓存、拥有值和Cleanup；前项迁移后的身份按下一项当前记录重新定位。入口L0，持久字段与容量增量0。调用方已给出一批身份时，该入口把重复的范围及结构准入合并到批次；公开单实体入口沿原调用链处理。

同值批量Set在验证每项身份后复用已定位的列项。该列已保留而该行缺席时，在原输入顺序中完成结构权限核对、RestorePresence、实际值写入、缓存Bind与本批版本Stamp；列尚待建立时沿AddKnown完成迁移。已有值的拥有资源沿原替换入口释放，Added与Written继续沿同一批版本登记。恢复路径省去重复进入AddKnown后的Chunk、类型与列定位，字段、容量、分配及复制增量均为0。

| 操作 | 段外工作 | 逐行/逐词工作 | 批次完成 |
|---|---|---|---|
| 已有列 SetAll | 查询绑定、列/变化/索引绑定 | Fill/Copy；实际 key 变化维护 | 版本/摘要按实际词合并 |
| Add/RemoveAll，普通列 | 按源表绑定最终边与目标容量 | MoveKnown；源位置随填洞更新 | 每段发布 Count/结构版本 |
| Enable/DisableAll | 绑定位/禁用计数/实际索引 | 一词 AND/OR；PopCount 实际改变位；按改变行维护资源与索引 | 每列禁用计数集中写回 |
| Destroy 任意身份集合 | G 一次，绑定实体目录 | 每个身份读取当前 record，再执行 X−G | 统一发布批次结构版本、回收日志前缀 |
| Destroy 完整 Chunk | 绑定实际释放/索引/Sparse/事实能力各一次 | 遍历行身份与 record；实际附加项分别清理 | Count 清零、空闲链头和 EntityCount 一次发布 |

普通实体集合的批量销毁先逐项解析输入身份，再以Row符号位标记成员。后续记录访问复用已验证ID和Chunk成员不变量；每行实际移除时清Chunk引用并推进Generation，批次末按原输入顺序把Row连接到空闲链一次。容器、索引和Sparse沿原行释放顺序完成，Count与链头在同一完成点发布。

整 Chunk 的普通立即释放，D 为〈5,2M,M+5,M+6,0〉：Archetype/Sparse 首项/销毁日志/实体记录/行身份五个基址，循环每行读取身份和记录，16 B 记录整项写入一次。完成写 FreeHead、EntityCount、Chunk.Count、结构版本及 G 两项。S-E 同能力整表释放的写次数为5M+6：逐行写代数、表引用、free ID、freeCount、尾部身份，再写 Count/AliveCount/EntityCount/结构版本和 G。源码：[FreeTable](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.cs#L332)。

批量查询删除采用已完成选择的身份，或者同 Chunk 降序行段。连续删除尾段直接缩 Count；任意洞位使用当前 EntityRecord。命令回放的逐条可见顺序按 [[Commands]] 保持。

批量Destroy先验证输入身份并在独占结构范围内用record.Row高位标记本批成员。每Chunk连续标记尾段复用RemoveMarkedTail：独立值先沿已有列释放接口处理实际start/count，托管值范围清空，追踪列沿ClearTail清理行版本与成员位，位置缓存沿实际范围移除，Sparse沿该段每个身份的First清理实际附着链，记录写空Chunk并增加代数，Chunk.Count一次缩减；最后按输入顺序建立空闲链并统一减少实体数。CanReleaseTail按Managed、位置缓存、Tracked及独立值的列释放声明选择实际能力，普通POD与GroupBy表共用尾段处理；分组键由表布局持有，成员数量沿Chunk.Count消费。Sparse的保留值规则沿批次入口的实际成员选择完成。Shared、索引、存在位及采用单值释放的独立值沿相应逐项删除路径完成。

连接生命周期或销毁reader后，CanReleaseObservedBatch先沿既有IsAliveCore检查输入身份，按连续相同Archetype复用纯POD能力与[[Relations]]的表级生命周期参与判断。参与项允许批量回收时，Core按原输入序发布Destroyed事实，复用已经校验的记录完成标记、尾段回收、空闲链与Count发布。逐实体路径沿PrepareDestroyColumns和DestroyObserved完成相应回调、主值释放与事实发布。纯POD批量路径的附加工作为M项身份读取、实际表变化次数对应的能力检查及实际reader启用时M项16 B事实写；首次/增长的事实数组容量仍按实际触发过程计入。

批量Destroyed发布先以现有Count加M准备环形事实数组容量，再把输入按环形末端分为至多两段，逐项写原身份与递增Sequence，最后各发布一次Count及最终Sequence。扩容取得未初始化的实际16 B值数组，只复制现有有效事实的两段；有效范围由Count持有。单实体发布复用同一ReserveDestroyed容量内核，沿其原有逐条发布时点更新reader可见事实。

### 整Chunk的最终列准备

拓扑批次已经得到每个主体的最终所需列后，按源Chunk累计选择行数与列组合。默认增长模式下，源Chunk全部有效行采用同一列组合时，调用内部EnsureColumns(Chunk,TypeSet)；其余项复用逐实体EnsureColumns。两条路径共用AdditionByTypes方向边和同一最终表。

整Chunk路径先取得目标并Reserve(start+count)，随后每个共有POD列复制一段count×sizeof(T)字节，每个含引用列和Shared编号列执行对应连续数组复制；行身份复制到相同目标范围，每个EntityRecord更新Chunk/Row一次。Added/Written行版本及摘要、存在位、启用位随同一批行转移；新预备列先清该批的存在位与版本，实际写入时才登记新增。

含引用主值转交目标后清空源引用；源版本、位与禁用计数清零，源Count归零。最后对目标实际范围重绑已建立的位置缓存。批次后续逐项写值重新读取EntityRecord，已经取得所需列的主体沿当前布局继续。该入口复用Core的列准备职责，把逐行共有列复制合并为逐列连续复制；公开拓扑校验和事实发布继续由关系批次完成。

## 共有列复制与填洞

POD 描述为 TypeId/Size 共8 B。纯 Add 复用源表描述；纯 Remove 复用目标表共有描述；混合操作首次建立交集。含引用列走闭合类型赋值；Buffer 使用移交或独立复制两种明确入口。

ByteCopy_T(1) 使用与 S-E 相同的按宽度复制：1 B 一次；2–3 B 两次2 B；4–7 B 两次4 B；8–16 B 两次8 B；>16 B 按16 B块加实际尾块。两侧相同宽度/实现时该子过程逐项相同。源码：[CopyBytes](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/Archetype.cs#L1075)。

输入已经取得 Chunk、record 数组和洞位/末行，K 个普通 POD 列：

| 中间行 FillHole | S-E | D |
|---|---|---|
| 框架定位与身份回填 | 〈4+3K,3+3K,2K+3,2,0〉 | 〈4+K,3+2K,K+2,2,0〉 |
| 实际共有值 | ΣByteCopy_T(1) | ΣByteCopy_T(1) |
| 状态附加项 | 实际存在位、追踪与托管清理 | 实际启用位、追踪与托管清理 |

D 读取末行 Entity、写洞、修被移动 record.Row，A3/W2；Columns/步骤/实体数组 P3；每列读描述和列项 A2、Rows P1；循环 K+1。附加转移目录空值检查 P1/B1。尾行选择属于调用方 X/MoveKnown。含引用尾项清空逐列 A1/W1，Buffer 移交后的源尾引用同样清空。

## Clone、Prefab 与跨 World

Clone 的完整路径为源身份 R → 绑定源表最终目标 → CreateKnown/批量创建身份 → 普通值复制 → Buffer 独立复制 → 启用状态 → 实际 Sparse → 引用修正 → 已开启索引/追踪 → 所选拓扑维护。复制入口接收已取得源/目标行，内部直接访问具体列。

单实体Clone沿每个实际存在的列处理：普通POD值复用Chunk.CopyBytes，含引用值、自定义值操作及Shared沿类型化Clone入口。Tag保留成员与启用状态。目标创建、实际索引、Sparse和初始化沿同一完成链执行，Buffer由值操作取得独立数据与元素数组。

ComponentColumns首次分类在既有bool中保存CanCopyPodAndBuffers：computed与Shared关闭，拥有值及托管列均为内部Buffer列操作。Archetype的既有CanClonePodAndBuffers继续叠加索引、分组和初始化关闭的条件，保持单实体Clone原准入与一次bool判断。该单实体布局且源TransferKinds的presence/enable位关闭时，直接遍历已有Pod目录按宽度复制，再按Managed类型顺序调用Buffer.Clone。Entity引用继续先进入映射路径，其余布局沿上述逐列处理。

集合CopyValuesTo复用值复制分类，并要求源presence/enable位关闭。该分支沿原Components顺序执行POD字节复制、实际Buffer.Clone及Entity引用Remap，省去每类型重复的computed、存在性、Shared和启用状态选择。用户拥有值操作沿原逐列路径；Sparse仍在每行末尾处理，CompleteCopies在全部目标值及引用就绪后建立索引、分组与初始化。两个既有bool分担值复制与单实体完成准入，字段、缓存、容量、分配及业务复制量增量均为0；实际复制与独立数组费用沿原链计入。

集合Clone和Prefab沿同一列分类执行普通POD字节复制；声明Entity字段的POD列随后执行实际引用修正，Buffer继续沿独立复制与元素修正入口。同World复制中，源组合与最终类型相同、实际分组值可直接复用时，目标绑定直接取得源Archetype。计算列筛选、跨World类型绑定和含Entity引用的分组列继续沿对应目标绑定步骤完成。

目标容量准备沿已有Dictionary<Archetype,int>累计实际份数。每个模板项使用一次GetValueRefOrAddDefault取得计数项，再将copiesCount加到该值；字典随后按实际目标表预留行数。计数目录由World的CopyData复用，首次桶/entry及实际增长入账，热容量充分时L0；取得的ref在本次值累加后结束。

单Chunk表已知本批新增行数added，且2×added至少达到该Chunk现有Capacity时，复用ReserveRows(count+added)准备实际目标行数；追加批次承担与自身规模相称的容量准备。较小批次沿Reserve的几何增长，固定块沿原整块准备。身份分配、实际复制、引用修正、索引与初始化保持原次序。该入口同时服务EntityPrefab模板World构造及目标World实例化，两者的值、行身份、Buffer行头与容量分别计入各自持有者；POD字节数组继续沿[[Construction]]的几何规则建立。

| 复制内容 | 一份目标的工作 | M 份同模板目标 |
|---|---|---|
| 普通 POD/托管值 | 每实际列 Copy_T(1) | 同值 Fill 或连续 Copy，M 份实际值写 |
| Buffer | 16 B行内头与独立元素；首个元素数组L1，Copy_T(n)，池/目录首次费用见[[Containers]] | M份独立元素；每份真实增长 |
| 启用状态 | 读取源位并写目标位，维护禁用计数 | 按连续目标词 Fill/掩码合并 |
| Added/Written | 创建目标使用本次新版本 | 整批共享版本，摘要按实际词/块 |
| Sparse | 沿实际附着项复制 | 每类型/目标实际新增内核 |
| Entity 字段 | 完整映射已建立后每字段 A1/W1 | 先生成全部目标身份，再处理全部实际字段 |
| Shared | 同 World 复用编号；跨 World 每实际共享值归并一次 | 一次模板绑定后复用目标 Shared 编号 |
| 索引/邻接 | 从最终主值建立一次 | 按实际 key/端点先预留再填充 |

Prefab 持有自己的值、Buffer 元素和类型序列。每份跨 World 绑定保存 World 身份、数据代数与目标表 ID；使用时验证这三个输入，再从当前 World 表目录取得目标。Clear 保留表时继续复用；Restore 推进数据代数后重建绑定。类型目录是进程注册数据，World 内的可变绑定由模板缓存持有。

引用规则显式由调用者选择“复制集合内重映射、集合外保留或使用 resolver”。映射从真实源 Entity 身份到全部目标身份；完整 World 用直接数组并检查代数，任意稀疏集合按实际编号范围选择数组或完整 Entity key。每个实际引用都计一次修正，包含 Buffer 元素和 Sparse 值。对应保存/恢复的相同规则见 [[Serialization]]。

Prefab在捕获时为第i个源身份生成私有引用Entity(-i-1,1)，并在复制主值、Buffer元素、Shared及Sparse值时通过同一EntityMap修正集合内引用。该私有引用连续编号；源World实际身份使用正编号，集合外引用继续保存原身份。实例化先建立该份的全部目标身份，再以私有引用到目标身份的映射完成修正，最后从主值建立索引、分组与实际拓扑。捕获与每份实例化各在自己的复制阶段调用实际引用修正一次。

任意Clone集合继续按真实源身份范围选择带Generation/Epoch的直接数组或Dictionary。Prefab的私有编号固定为Entity(-i-1,1)，每份实例化沿EntityMap的内部同名构造直接绑定本份targets与external；查找以-1-Id取得下标，核对范围和Generation=1后读取对应目标身份。Null沿入口返回，其余集合外身份沿原resolver或保留规则处理。一般dense映射优先使用自己的目录，随后分派固定前缀，保持普通Clone及读回的既有映射路径。

捕获阶段的私有引用数组由构造局部持有，8N B元素加数组头计构造分配，构造返回后可回收。Prefab继续持有_templates身份数组及独立值World；每份实例化直接消费已有targets span，新增持久字段、缓存与容量0，省去目标World的16N B映射项数组及对应惰性对象。Prefab对象同时减少8 B字段；多份实例化分别消费各自的targets切片。捕获主值与Buffer由Prefab自己的World持有，源World的后续Clear沿独立生命周期执行。

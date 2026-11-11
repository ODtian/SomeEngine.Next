# 关系与 Hierarchy：候选、维护和完整成本

分析时间：2026-10-22 22:50 +08:00。状态：S-E 为参照源码；D/E* 为保留完整能力后的布局与预算。

分析方法：固定独立 edge 身份、端点组件查询、payload/附加组件、双向遍历、基数、局部顺序、实体代数与显式维护时点，再对每个操作展开主值、邻接、已应用值、变化消费及回收。计数见 [[Cost-Model]]。

## 主值、派生数据和入口

Parent、Children与edge endpoints均以Core组件承载，payload/附加组件也在edge行。Hierarchy修改受控Children列中的唯一子数组，Relationships持有edge邻接与Applied。普通查询直接读取这些组件。直接拓扑入口即时应用，SetParentDeferred/RetargetDeferred提交主值后由Maintain应用差额；普通Destroy按本页的局部销毁协议完成关系清理。

| D 数据 | 字段 | 大小与消费者 |
|---|---|---|
| edge每端Head | Items引用、CountFlags、Generation | 16 B；CountFlags高位记录本端ordered |
| Children<D> | Items引用、CountFlags，8 B对齐 | 16 B/Core行；只读公开成员，Hierarchy写入 |
| 邻接项 | Other Entity、Edge Entity | 16 B；遍历/pair 查询直接返回目标与 edge |
| AppliedEdge | Source、Target、SourcePlace、TargetPlace、Generation | 28 B；原端点和位置用于 Retarget/Destroy/Maintain |
| AppliedParent | Parent Entity、Place、Generation | 16 B；旧 Parent 用于普通组件写后的维护 |
| 子节点项 | Child Entity | 8 B；Hierarchy 连续遍历 |

AppliedEdge/AppliedParent 是上次已建立索引的值，主组件是当前值。维护期间二者可以处于不同版本。edge关系持有Heads/Applied数组；Hierarchy持有AppliedParent数组，Children成员由Core列持有。Core持有销毁事实和消费进度。edge Head与Applied保存代数，Children沿实体记录取得身份。

核心 API：CreateEdge、DestroyEdge、Retarget、Outgoing/Incoming/Incident、FindPair、MaintainRelations；GetParent、SetParent、Detach、Children、Descendants、DestroySubtree、Hierarchy.Maintain。直接拓扑入口修改主值并应用邻接；受控延迟入口先修改主值，显式Maintain应用差额。Parent/Children/endpoints的普通查询提供只读访问；类型化拓扑入口承担其修改。

Parent/端点的类型化编辑入口负责森林和基数条件；查询读取已提交的Core主列。Job或批量编辑使用实际受影响项的写集，最终集合验证通过后提交主值，再按所选即时/延迟时点应用邻接。这里的临时写集服务多项约束验证，其成本在本页批次段单列。

基数约束读取当前主值的占用索引，邻接读取上次已应用集合。UniqueSource/UniqueTarget各建立一个按端点Id访问的Claim数组，项为{Generation int, Edge Entity}共12 B；有向OneToOne建立两份，无向OneToOne共用一份端点数组。UniquePair建立完整端点对→Edge字典，无向pair按完整Entity顺序规范化查找键；主端点和局部顺序保留声明的角色。Parallel关系沿双向邻接路径。占用索引由类型化端点提交维护，服务延迟邻接期间的即时约束校验。

占用查询先匹配端点代数，再检查占用Edge的完整身份及活动生命周期；已进入Destroyed保留清理或已释放的edge作为空占用处理，维护清理时以Edge身份相等为条件释放该项。其余活edge继续占用当前主端点，端点普通销毁即时清理edge，显式延迟销毁在Maintain完成。UniquePair查找复用该占用索引；Parallel的多edge pair索引按实际声明另建。

## 候选：相同独立 edge 与双向能力

以下绑定 Head/Applied 目录完成，容量足，非空头；移动成员的位置修正使用已经绑定的 Applied 数组。

| D 邻接候选 | 追加一端 | 删除一端非尾项 | 枚举 n 条有效邻接 | 内存 |
|---|---|---|---|---|
| 紧密邻接 | 〈1,1,1,2,0〉 | 〈1,3,1,3,0〉 | Items P1、A n、B(n+1) | 头 16 B；每端项 16 B |
| 留洞邻接 | 尾〈1,1,3,3,0〉；洞〈1,2,1,3,0〉 | 〈1,1,1,3,0〉 | Items P1、A u、B(u+1)+u 次占用判断 | 头 24 B；每端项 16 B；u≥n |
| 双向数组链 | 新项/旧尾 A2、数组 P1、空尾 B1，链接/头尾/Count W5 | 当前/前后 A3、数组 P1、头尾 B2，链接/头尾/Count/free W5 | 节点 A n、B(n+1)，下一地址依赖 next | 头 16 B；每端项 24 B |

选定紧密邻接，读取处于结构范围，拓扑修改在范围外执行或进入 CommandBuffer；需要保留顺序的 Head 使用保持顺序的搬移。值索引采用 [[Indexes]] 的同一固定成员读取规则。关系以明确维护点构造可遍历集合。

| 按 Entity 定位的候选 D | 热定位 | 首次/扩容 | 选择 |
|---|---|---|---|
| 平坦 Head/Applied 数组 | Head〈1,1,2,0,0〉；Applied〈1,1,1,0,0〉 | 每关系类型两个 Head 数组各 16I B、Applied 28I B | 默认，热读取直接按实体索引 |
| 分页 Head/Applied | 每次额外 P1/A1/B1 | 按实际触及页分配，页目录各 8p B | 大 ID 空间且极少关系的显式配置 |
| 完整 Entity 键字典 | Dictionary P1+Hhit | 桶/entry 按实际条目分配 | 用于旧代冲突冷路径 |

平坦edge目录为60I B/关系类型。Hierarchy为16I B AppliedParent，加实际含Children的Core行各16 B与成员数组；延迟目标索引按实际差额建立。分页配置降低空目录内存，承担每次额外页定位。

## 源码依据与公平配对

S-E [Entity.Relations.cs:12–47](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/Entity.Relations.cs#L12) 使用源实体的值缓冲；[Relations.cs:96–191](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/Relations.cs#L96) 按目标建立索引；[ValueIndex.cs:35–91](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/ValueIndex.cs#L35) 为留洞头；[World.Hierarchy.cs](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Hierarchy.cs) 使用 ChildOf 组件和目标索引。

| 配对能力 | S-E / E* 完整输入 | D 完整输入 |
|---|---|---|
| 单向邻接值 | S-E Buffer 定位/追踪 + Buffer.Add/Remove + 关系索引 | 单端邻接内核；身份与反向端另列 |
| 独立 edge 与两端遍历 | E* 普通实体存 endpoints/payload；两端缓存 Other/Edge；保存已应用端点供显式维护 | Core edge + 双端 Head + AppliedEdge |
| Parent 原生组件查询 | S-E ChildOf 与目标索引；森林检查用 E* 相同祖先遍历 | Parent 主列与子数组；森林检查实际 h 层 |
| Destroy后观察点 | 普通Destroy即时完成；DestroyDeferred加Maintain完成 | 普通Destroy即时完成；DestroyDeferred加Maintain完成 |

原生单向关系的实体/列创建费用和完整 edge 创建分别入账。E* 的共同邻接算法使用下列相同向量，Core 操作替换成 S-E 对应入口；每一次原生结构门按该调用实际发生次数计。

## 已应用邻接的 D 内核

| 内核，默认无序 | 〈P,A,B,W,L〉 | 步骤 |
|---|---|---|
| HeadResolve | 〈1,1,2,0,0〉 | 目录、项、范围/代数；初始化旧代差额另计 |
| AppliedResolve | 〈1,1,1,0,0〉 | 目录、项、代数；返回 ref 与数组基址 |
| Append 一端 | 〈1,1,1,2,0〉 | Items、容量、项写、Count |
| Drop 一端中间，Applied 数组已绑定 | 〈1,3,1,3,0〉 | 读末项、写洞、取移动 edge 的 Applied；写洞、移动 place、Count |
| Drop 一端末项 | 〈0,0,1,1,0〉 | 比较位置并减 Count；POD 项由 Count 表达有效范围 |
| 两端创建缓存 | 〈5,5,6,5,0〉 | 两 HeadResolve、两 Append、Applied 数组 P1/A1、28 B Applied 写 W1 |
| 两端中间删除缓存 | 〈5,9,7,7,0〉 | AppliedResolve、两 HeadResolve、两 Drop、清 Applied W1 |

局部策略在每次 Head 操作前检查高位一次 B1，整段循环外分派；两端操作再 B2。保持顺序删除移动 d 项：Copy_Item(d) + 修 d 个被移动 edge 的 place（A2d/Wd）+ Count W1，数据部分合计 A4d/W(2d+1)，再加 Kcopy；Applied 数组在段外绑定一次。Children 使用 8 B 项，修 AppliedParent.Place。

## 完整创建、修改、删除和读取

k包含endpoints和实际普通payload/附加列，R/G/S/CreateKnown/X/M见Cost-Model。下表整数向量采用主值已应用、两个不同端点的普通热输入，表示主体；公开调用加E一次。共有列、启用位、缓冲、约束和追踪按实际声明追加。

| 操作与输入 | D 完整预算 | E* 完整预算构成 |
|---|---|---|
| CreateEdge，两端有效，普通列、已建目标 | 2R+CreateKnown(k)+〈5,5,6,5,0〉=〈10+k,9+2k,14,13+k,0〉；局部策略再 B2 | 两次原生 IsAlive + 原生创建 k 列 + 同样两端缓存〈5,5,6,5,0〉+策略 B2 |
| DestroyEdge，直接维护、两端中间、Core 尾行 | X+〈5,9,7,7,0〉=〈10,10,14,14,0〉；策略再 B2；事实开启时再 AppendFact | 原生 Destroy + 相同两端删除/Applied 清理 + 相同事实能力 |
| DestroyDeferred后Maintain | X+AppendFact+ReadFact+两端删除缓存+Ack；结果与直接入口相同 | 原生 Destroy + E* 事实队列 + 相同缓存清理；按同观察点比较 |
| Retarget，c 个端点变化，中间摘除，c∈{1,2} | S+AppliedResolve+cR+c·〈4,6,6,5,0〉+相等/方向B4+Applied W1=〈5+5c,4+7c,10+8c,4+5c,0〉；策略再B2c | 原生Set+原生c次新端点验证+相同已应用端点/旧摘/新挂 |
| Outgoing/Incoming n 条 | HeadResolve + Items P1 + A n/B(n+1)，输出为值视图 L0 | E* 相同两端邻接；原生单端关系只在单端能力组比较 |
| Incident | 两个 HeadResolve、两个 Items P；按两方向枚举实际 n_out+n_in 项；局部方向选择移到循环外 | E* 同样两端、方向与项数 |
| FindPair，扫描 d 条 | HeadResolve + Items P1 + A d/B(d+1)，Eq_Entity d 次；命中立即结束 | 原生缓冲扫描或 E* 相同 Other 缓存扫描 |

Retarget单端的〈4,6,6,5,0〉为旧/新各一次HeadResolve、旧Drop、目标Append，Applied引用在外层复用。S取得和写主endpoints；当前Before负责约束摘除，Applied负责旧邻接摘除。延迟写入把S留在提交时，邻接差额在Maintain应用；两者各按真实改变端数计费。

上表Create/Retarget是Parallel关系的主存储与邻接预算。新端点在R后加入Cost-Model的Life：Create为2次，Retarget为实际新端数次；占用者的活动检查按同一内核计。声明基数后，UniqueSource/UniqueTarget每项增加Claim目录P1/A1、代数/空项判断B2，非空项再R与身份相等判断；提交写Claim整项W1。OneToOne按两个角色各执行一次。UniquePair每项为Dictionary P1+Hhit/miss，实际占用Edge再R；提交使用Hget/已取得entry写。释放旧占用先确认其Edge相等再清，防止已销毁旧edge的延迟维护清掉新占用。批次允许合法交换，具体算法见下节。

## Hierarchy的组件生命周期与完整路径

Parent<D>采用普通组件。SetParent建立或替换Parent，Detach移除Parent并产生Removed<Parent>。Children<D>的首次非空集合建立组件，成员变化登记Written，最后一个孩子离开时移除组件并产生Removed<Children>。Parent、Children声明Added/Written/RemovedValue。该行为对应HierarchyModelTests.ParentAndChildren_UseNativeAddedChangedRemovedFacts。

Children的Items由Core列唯一持有；公开Read/查询返回只读目标视图。迁移转交数组，Clone按所选拓扑复制策略建立独立目标数组，Removed转交旧数组并在ClearRemoved释放。最后一个成员离开时，旧Children整体进入事实，保留移除时的成员内容。一般成员变化原地修改实际数组与Count。

| 路径与输入 | 主值和组件生命周期 | 邻接与实际附加工作 |
|---|---|---|
| GetParent | TryRead Parent；缺列返回Entity.Null | 主列读取R+V，按实际短路计；公开加E |
| SetParent，已有Parent，旧父仍有孩子，新父已有Children | 写现有Parent，登记Written | 校验新父与森林；旧Drop、新Append、Applied写；两父Children各Stamp |
| 首次SetParent | 一次最终布局迁移建立Parent并登记Added/Written | 首次目标Children按需建列；Append、Applied与Children版本 |
| Detach | 一次最终迁移去Parent/添Removed<Parent> | 旧父仍有孩子时Drop+Stamp；最后一个孩子离开时去Children/添Removed<Children> |
| 重新SetParent | 按新增Parent语义建立列，保留现有Removed事实至清理 | 新父Children按实际存在性建立或追加 |
| Children(n) | R+V取得Core Children；缺列返回空视图 | Items P1、A n、循环B(n+1)，使用期持结构读 |
| Descendants(v,e) | 每访问父按上述Children入口取得成员 | e次成员读取与实际栈入出；Grow按实际容量 |
| DestroySubtree(v) | 先取得完整当前子树，再按叶到根的次序批量Destroy | 合并结构范围、实际局部清理与事实回收 |

普通热SetParent的已知输入为旧父至少两个孩子、新父已有Children、主值等于Applied、容量足。共用records与Applied基址：旧父已知Children定位H_old=〈3,3,0,0,0〉；新父通过R后定位Children为H_new=〈3,2,2,0,0〉。主体为S+R_new+AppliedResolve+H_old+H_new+Drop+Append+父相等/旧父判断B2+Applied整项W1，合计〈14,14,14,9,0〉。完整入口再加E、新父Life（复用H_new的Chunk）、ForestCheck、Parent及两父Children的Stamp、两次局部顺序判断。首次/末项组件迁移按上表另计，edge HeadResolve只用于edge邻接。

ForestCheck从新父沿当前Parent访问到根，比较child身份；每次TryReadParent使用本页的R+V短路路径，循环与相等各按实际访问次数计。第一步复用已校验的新父record，后续完整身份读取按实际路径计入。批量校验使用下文FinalParent算法。

提交准备先合并每个实体的最终组件集合，包括孩子的Parent与各父的Children/Removed变化；完成所有容量准备和布局迁移后，依据最新EntityRecord重新绑定实际行，再执行值/成员修改和版本登记。每个实体向最终表迁移一次。旧Children要转交事实时先取得完整旧值，其成员数组由事实接管。

## 变化消费、代数冲突、首次和增长

AppendFact：绑定日志P1、写Entity A1/W1、Count W1、容量B1，为〈1,1,1,2,0〉，增长另计。读取f条事实为日志P1、A f、循环B(f+1)，reader的Ack W1/批。Core X已包含日志开启判断。旧Parent/edge先AppliedResolve再逐项摘邻接；父销毁时每个仍指向它的孩子执行Parent移除事实、Applied清理及Children维护，各记一次。

普通组件变化消费按 [[Changes]] 的实际词/行数量扫描，读每个变更行的主 endpoints/Parent，取得 Applied，再比较字段。直接拓扑入口已经应用的行，在后续 Maintain 承担主值读取 + AppliedResolve + 实际相等检查，邻接 W0；该确认成本计入一次完整“直接写 + Maintain”。变化读取进度由该消费服务持有，读写共用标准变化存储。

实体 ID 在维护前复用：新代首次取得旧代非空 Head/Applied 时，将旧项转入以完整 Entity 为键的旧代字典，Hget-new + value W1，再写新代项 W1；正常当前代继续直接数组访问。处理旧代事实先比较数组 Generation B1，失败时 Dictionary P1+Hhit，旧代清空后 Hremove。旧代池/字典首次 L、增长与完整性清理按真实触发数相加。该冷路径同时覆盖 edge 与端点的提前复用。

| 首次/增长 | D 完整维护 | E* 同能力 |
|---|---|---|
| 关系类型注册 | 两端 Head[] 与 Applied[]，L3；Zero_Head(2I)+Zero_Applied(I)；声明的Claim数组/Pair字典分别建立；类型目录发布 A1/W1；开启 endpoints 变化观察 | 完整edge与延迟邻接期间即时约束需要同样数据 |
| 第一个端点 Head | 项数组 L1，Zero_Item(c)，Head 引用/Generation W2；容量检查/代数分支按实际路径 | 相同邻接能力同样建立数组 |
| Head 成员增长 | Grow(Item,c,c',n)；两端独立计 | 相同 n 与成员宽度 |
| 实体空间增长 | 两 Head 各 Grow(Head,I,I',I)，Applied Grow(Applied,I,I',I) | 相同身份目录或分页的实际成本 |
| 首个销毁事实消费者 | reader 进度目录与日志分别首次 L1；注册项 A1/W1 | 显式维护适配建立相同事实流 |
| 最后邻接项离开 | Count 已写；归还数组使用池 Return；Head.Items W1；Generation 保留 | 按相同保留/归还策略 |
| 保序修改 | 实际 d 项移动与 d 个 place 修正 | E* 相同局部顺序能力 |
| 完整读取 | 由主 endpoints/Parent 重建 Head、Applied；局部顺序单独读回 | 成本由 [[Serialization]] 合并 |

完整选型是 Core 主列 + 明确的已应用端点 + 紧密双向邻接 + 显式维护。Source 原生单向缓冲提供部分内核依据；28 B AppliedEdge、代数冲突集合、森林检查、完整 edge 与变化消费为本方案逐项保留的功能成本。

## 主值、维护与批次语义设计

直接 CreateEdge/Retarget/DestroyEdge/SetParent 在入口读取受影响的旧主值和 Applied，完成端点/森林/基数检查与目标容量准备，再修改主值和邻接。普通实体Destroy先完成下文局部关系清理，再发布一次原身份事实；显式DestroyDeferred在发布事实后由Maintain完成相同清理。Parent 与 endpoints 的真实值各由 Core 保存一份。

Hierarchy/Relations 首次建立时，在同一结构边界注册销毁 reader 并扫描已有主列。该扫描完成全部 Head/Applied 后设置初始组件变化基线。每次 Maintain 先处理销毁事实，再按组件变化读取当前主值并比较 Applied，实际摘挂完成后推进自己的成功基线和 reader 进度。销毁日志统一回收由 [[Changes]] 持有。

类型化批次由下面的最终集合算法提交；普通顺序CommandBuffer把一个显式拓扑批次作为一条复合命令。该命令验证通过后提交，前后命令沿已确认的顺序前缀规则。完整World的原子回放由[[Commands]]承担。

关系方向、基数、局部顺序分别来自关系声明和实际 Head。无向关系的一个edge在各端incident Head登记；同端自环按一个incident成员处理并在两端位置字段共用该位置。保序Head的移动量d与place回写进入本页公式。UniquePair复用约束字典，Parallel的通用FindPair使用实际邻接扫描。

端点 ID 复用仍使用完整 Entity 身份及旧代集合。旧代 Head/Applied 在新代占用数组项之前移入旧代集合，其成员保持可由旧身份清理；移动成员的 place 更新同样按其完整 Edge 身份选择当前/旧代 Applied。每次这类旧代查找和 Hremove 均进入实际维护账。

## 最终集合校验：具体算法

这是批次设计的一部分。输入按producer/记录顺序展开为r项，对同一主体取最后一个After，得到m项及EditByEntity字典。Parent的最终编辑使用Dictionary<Entity,Entity>，entry包含hash/next、Child、After，共24 B，另有每容量4 B桶项；Before在提交前沿Core主值及Applied读取。端点批次使用下面的Before/After两个字典保存首个Before和最后一个After，各按自己的实际容量计费。新增edge使用批内编号，结构写范围内沿空闲链/新增Id预选最终身份，临时保存NewEntities和最终空闲头/Used，提交时发布；删除项的After为空。批次持有实际修改项。

Parent、Children和RelationEndpoints由受控拓扑入口维护存在性与主值。payload和附加组件沿各自类型声明使用[[Storage-and-Queries]]的Enableable位、Enable/Disable和行过滤；邻接成员资格沿有效edge身份与端点主值维护。端点编辑输入的Kind表达创建、重定向或销毁，归并后的Before/After空值表达前后存在性；普通Enableable列沿自己的元数据保存启用位。维护读取完整拓扑主值及其变化，再按对应Applied状态建立成员。

公开拓扑入口为Relations<T>.Edit(world,ReadOnlySpan<EndpointEdit>,Span<RelationEdge<T>> created,bool deferred=false)。EndpointEdit为Edge/Source/Target三个Entity及Kind，共28 B；Kind为Create、Retarget、Destroy。Create按输入中的出现顺序分配新edge编号，EndpointEdit.CreatedInBatch(i)可在该批主体或端点字段引用第i个新edge。created按相同顺序返回身份。这个入口建立或修改RelationEndpoints<T>；payload和附加组件沿普通类型化值入口处理，命令场景在该复合命令后的原顺序位置写入。

Core的PreviewEntityIds沿自己的空闲链和Used填写预选身份；ReserveEntities及Archetype.Reserve准备实际容量；CommitCreatedEntities写最终行、身份与创建版本，最后各发布一次空闲头、Used及实体数。关系层沿现有Before/After字典校验新身份、最终存活端点和占用；两个字典各存32 B entry及4 B桶项/容量，After全空表达删除，Before全空表达批内创建。它们分别供占用释放、主值发布和顺序维护消费。新edge使用最终端点列布局，批内两个新edge作为彼此端点时共享同一身份预选表。删除沿普通或延迟Destroy完成原生Removed/Destroyed事实与注册生命周期消费者，整个提交沿同一个BatchVersion执行。

Job暂存阶段保存Before/After和查询基线：Parent暂存行包含Child/Before/After/InsertAt，共28 B；端点暂存行包含Edge/BeforePair/AfterPair/Kind，共44 B。串行完成方取得结构写访问后验证主体身份与捕获的Before，再把After交给同一最终编辑算法；这些行随该次Job暂存寿命保存。直接调用在自己的结构写范围读取Before。版本在有实际提交时取得一次，所有值写和事实共用该版本。

公开输入类型为`CapturedParentEdit(CommandEntity Child, Entity Before, CommandEntity After, int InsertAt)`和`CapturedEndpointEdit(CommandEntity Edge, Entity BeforeSource, Entity BeforeTarget, CommandEntity AfterSource, CommandEntity AfterTarget, EndpointEditKind Kind)`。主体及After复用现有CommandEntity携带producer/批次结果身份，Before保存读取时的实际Entity。记录入口先沿现有Encode校验并转为Entity编码，typed bucket内部行分别为28 B和44 B；公开输入读取和转换按自身实际布局计费。SystemRunner/生成器在行工作的结构及类型Read范围内捕获Before、World.DataGeneration，以及实际Query的MembershipVersion；DataGeneration为公开只读的数据恢复代数，MembershipVersion对应本次捕获的成员集合。显式实体来源只保存Before与DataGeneration。

复用CommandBuffer的EditParents<D>/EditEndpoints<T>名字，增加`EditParents<D>(Query query, ReadOnlySpan<CapturedParentEdit> edits, int dataGeneration, ulong membershipVersion, bool deferred=false)`和`EditEndpoints<T>(Query query, ReadOnlySpan<CapturedEndpointEdit> edits, Span<DeferredEntity> created, int dataGeneration, ulong membershipVersion, bool deferred=false)`；显式实体来源使用相同参数中的 edits、created、dataGeneration 与 deferred。每producer的typed bucket复制输入，segment保存两个捕获值，一次调用追加一个原Composite头。Create的Before为空，批内身份沿CreatedInBatch和现有producer映射解析，创建结果沿原区间发布，payload在后续FIFO位置写入。

每次Captured复合调用在该typed bucket的segment目录追加一项`{int RowStart, int RowCount, int DataGeneration, ulong MembershipVersion, QueryData Query}`，x64含对齐32 B。Composite头的DataIndex指向这一segment，回放据此取得该次调用的行范围和捕获条件；同producer后续调用各占自己的项。记录、segment目录首次/增长与32 B项发布沿实际调用计费，头部和行数组继续使用既有容量及FIFO次序。

回放到该头时核对DataGeneration；查询来源同时核对持有QueryData的MembershipVersion；随后核对主体完整身份及首个Before，再按原顺序归并最终After和局部顺序，复用最终森林/Claim校验、Reserve与提交。QueryData持有查询成员状态，MembershipVersion验证捕获与回放之间的集合一致性，Before承担主值前置条件；前序命令改变Before时该条件提交失败，成功前缀继续可见。After-only重载沿原顺序规则处理。公开完成链为行工作→摘要→行访问释放→结构Write→FIFO Playback及拓扑提交→规定Maintain→成功Last更新→handle完成，失败沿原异常链结束并保留最近成功Last。

### Parent最终父链

FinalParent(x)先查EditByEntity：命中取After的父或空，否则读取Core当前Parent。缺列和已经失效的父身份是父链终点。按Changes页活动身份规则校验所有新父后，从每个修改的child出发走FinalParent。

```text
Seen : Entity -> 本次起点编号
for each edited child with ordinal i:
    x = child
    while x has a live identity:
        if Seen contains x:
            if Seen[x] == i: report cycle
            break
        Seen.add(x, i)
        x = FinalParent(x)
```

Seen中的旧起点编号代表已经完整通过的路径，可直接结束；同一起点再次遇到同一身份表示环。最终图相对原森林只改变m条父边，每个新增环都经过其中至少一条，因此以这些child为起点覆盖所需检查。这个算法同时处理批内交换、跨多级祖先和共享祖先；每个实际访问身份写Seen一次。

设v为实际到达的不同身份，a为其中需要读Core而非After的身份。费用为r次归并+m次新输入检查+v次Seen插入+(v至v+m)次Seen查找+v次EditByEntity查找+a次TryReadParent；各hash操作使用实际c/e。平均hash查找下时间O(r+v)，临时空间O(m+v)。单次SetParent沿前节h祖先路径，复用相同FinalParent语义。

### 关系最终占用

关系声明生成约束键：UniquePair取(src,dst)，UniqueSource取src，UniqueTarget取dst，OneToOne分别取两个角色键；无向OneToOne使用统一端点键。每个After最多产生两个约束键。

1. 主体和新端点通过身份校验；已解析的批内新增身份查NewEntities，其余身份走R。预选q个新身份读取q项空闲/新增记录并写q项临时身份，allocator的最终头/Used在提交时各发布一次。为m个最终编辑建立EditByEntity。
2. 建立批内FinalClaims。遍历每个After键，查当前Claim/Pair索引。占用者属于本批编辑集合时，交由其After决定最终占用；本批之外的活占用者形成冲突。
3. 将After键加入FinalClaims；同键属于不同最终edge时形成冲突。当前与批内两层都通过后，最终占用合法。
4. 预留Core行、约束索引、目标Head、变化/移除事实与实际有序成员的容量。准备完成后先释放全部Before占用，再发布全部After占用和主端点。

这样最终一一映射中的交换在同一校验中通过；外部占用者与批内重复键分别有唯一检查点。Parallel只执行身份与实际生命周期检查。p为After约束键数，p≤2m；费用为p次当前占用查询、最多p次占用者R和EditByEntity查找、p次FinalClaims插入，提交再按实际旧/新键数清除/写入。批内字典首次L2，热复用按实际容量Clear；所需key/Entity项按真实宽度清零。

Claim数组增长为Grow(Claim,I,I',I)，每角色12I B；UniquePair使用Cost-Model的Dictionary首次/增长。约束索引在注册/恢复时扫描当前主端点一次建立，和Applied邻接分别按自己的观察点恢复。

### 主值提交与邻接应用

无序即时批次按“摘全部旧成员→写最终主值/占用→挂全部新成员”执行。相同端点保留其原成员位置；实际改变端点执行一次Drop/Append。每次Drop通过当前Applied取位置，沿用交换回填后的值。

有序Head的工作数组只为实际触及的Head建立：复制该Head有效成员，按批次原顺序处理摘除、插入和显式排序；最终校验与准备通过后发布该数组/Count，并按实际发布范围回写Place。设触及Head共d个成员、局部实际移动s项，费用包含Copy_Item(d)、本地实际搬移、实际发布成员数对应的Applied定位/Place写、每Head数组/Count发布和旧数组归还。这个局部数组同时服务顺序解释与提交，普通无序路径使用前述紧密内核。

Hierarchy的最终工作序列沿Span.CopyTo复制到Core的Children数组，再按发布范围写AppliedParent.Place。一般关系发布每个成员时取得一次实际AppliedEdge ref，由同一行读Source/Target以生成Other，并沿该ref写本角色Place；自环的两个方向位置按原角色规则更新。整序列发布的Place范围为实际写入的d项，局部摘挂路径继续按实际移动的s项计费。

Hierarchy中仅追加/改父的有序批次先取得受影响父节点的成员工作表，再对每个父压缩一次离开的孩子，最后按输入顺序追加到达的孩子并回写Place。输入中每个child只有一项时，复用最终编辑字典判定其After；同一child有多项时，使用原顺序里的父状态与最后到达位置解释中间变化。两个分支共用上述最终森林校验。

即时、主值与Applied同步、投影为空且每child唯一的追加批次，在最终森林校验和列/成员容量准备后，直接取得Core持有的Children数组。批次头记录实际移出标记；发生移出的有序父压缩一次保留成员并修正Place，纯到达父保留现有成员序列。新成员按输入次序写入已预留数组，批次头暂存Count，最后每父发布一次Children与Written版本。显式插入、多次编辑同一child和延迟投影继续沿上述工作表解释。E*有序维护同样沿已有移出记录选择成员压缩，纯到达父复用原成员序列。

直接路径用临时Dictionary<Entity,Children<D>>持有受影响有序父的数组引用、顺序位和本批Count；成员数组继续由Core列持有。全部父头在准备阶段登记，每次到达沿CollectionsMarshal.GetValueRefOrNullRef取得表中的Children引用一次，直接完成成员写入与Count更新，结束时每父发布一次头并清表。即时唯一追加路径直接消费该表；其他顺序编辑继续读取实际工作表。该表的entry为hash/next共8 B、Entity键8 B、Children值16 B，合计32 B；容量c另有4c B桶目录，首次和扩容按实际容量计分配、清零及复制。最终Count为0的父直接通过Core RemoveValue转交完整旧Children到Removed事实，保留移除前Count和成员内容；这份数组沿事实消费寿命释放。

主值与Applied同步时，即时批次复用同一次旧父读取及移动判定；成员压缩以工作表所属父节点作为旧父，按最终编辑字典决定保留项。连续编辑同一新父时，活动身份检查复用该批次已经确认的父节点。存在延迟差额时，Applied继续从自己的已应用状态读取，投影继续消费当前主值。

成员工作表与延迟投影由Hierarchy持有。复制当前成员时从备用List<Entity>栈取得容量；工作完成或投影被替换时清Count后归还。延迟投影在Maintain消费前继续持有自己的表，消费完成再归还；Dispose同时释放备用容量。一次批次按源Chunk与所需列组合统计数量，每组从同一Chunk的代表行计算一次目标布局，并将组内数量累加到目标容量。某组覆盖源Chunk全部行时，列准备沿整块迁移；其余实体沿已有逐行入口准备。具体连续复制由[[Bulk-and-Copy]]持有。写主值和邻接时重新读取迁移后的EntityRecord，复用已经通过身份校验的记录与Applied入口。

延迟批次提交主值与约束索引，Applied/邻接保持自己的已应用版本；Maintain随后从普通Changed读取实际差额。校验失败保留整批原主值/占用/邻接，暂存数组归还。容量准备和用户计算在提交前完成，提交部分由类型化赋值、已预留数组操作与版本登记组成。

## Maintain的具体顺序

1. 固定本次主值读取上界、销毁事实末序号和组件变化基线；取得该关系的主值/邻接写访问。
2. 处理销毁事实中的完整身份。销毁edge摘除其Applied两端并按身份清约束占用；销毁端点枚举其已应用邻接，读取每条edge当前主端点，按当前关系寿命规则决定删除或保留。父销毁对仍以它为活动父的孩子执行Detach；已经改父的孩子按当前Parent挂入目标。
3. 读取组件变化目录的实际Changed候选（显式Enableable类型包含禁用行）和Removed<T>事实；按主体取得当前主值与Applied。列已移除时最终端为空；当前父或端点已经销毁时执行同一Detach/edge清理规则，其余项比较得到真实改变的端。准备目标容量，再摘旧/挂新并写Applied。直接入口已经应用的相等项在此确认。
4. 发布Children/邻接的实际变化版本，再写组件成功基线和销毁reader进度。统一日志维护点按最小reader进度回收；Removed事实由完成所需消费者后的ClearRemoved阶段清理。

直接操作与Maintain复用同一个“已知旧/新端的转移”内核。旧代Head/Applied字典处理完整身份；约束占用以当前主值为准。两套数据各有消费者与独立更新时间。

Clear 在同一完成点清 Core 主值、约束索引和派生数据，并推进 reader；恢复保留模式同时恢复主值/Applied/顺序/进度并从主值建立约束索引，重新建立模式按 [[Serialization]] 从主值重建。类型注册与跨 World 的类型信息继续按进程寿命持有。

## 普通销毁与延迟目标索引

普通Destroy保留“返回时相关存活实体已完成清理”的行为；DestroyDeferred提供“事实发布后由Maintain完成”的显式入口。Core通过已注册的实体生命周期消费者调用关系层，消费者名单只含实际注册的domain/关系类型。基础X保持纯Core内核，完整Destroy追加实际消费者查找和清理。

Hierarchy的BeforeDestroy复用已验证身份的实际Core行。维护基线等于World.Version、延迟状态为false，且该行Parent/Children存在位均为0时，已完成维护的主值和邻接共同确定该实体的拓扑参与情况；入口只清理实际开启的空有序头标记。其余实体沿原有读父、处理到达成员、摘挂及事实转交链完成清理。销毁事实消费同样读取既有状态：旧代Children/Applied目录均为空、该身份Applied.Parent为Null且到达成员为空时，转入同批后续事实；reader进度仍在统一维护结束时推进。组件事实、旧代头和延迟到达继续由原持有者维护。

Core的批量销毁通过内部`IWorldLifecycle.RequiresIndividualDestroy(Archetype)`取得表级参与信息。Hierarchy将延迟状态、维护基线与World.Version的差异、有序策略、表内Parent/Children类型参与项做或归并；一般关系使用逐实体处理。Core对纯POD、实际索引/独立值/保留状态均为0、Sparse状态为空的输入表组合该判断，逐实体需求为0时复用[[Bulk-and-Copy]]的标记尾段回收，并按原输入顺序发布完整Destroyed事实。该入口把参与信息交给Core，实际关系数据继续由本类型持有，生命周期与reader的完成点保持统一。

延迟修改可能让当前Parent/endpoints与Applied不同。每个关系服务建立按新目标分组的差额成员索引：只有After与Applied不同的主体进入组。复用[[Indexes]]的KeyGroup与Membership算法，key为完整目标Entity；每个成员反查增加Generation共12 B，组key各保存一份，边的两个角色分别索引。旧活动端从Applied取得，新目标从组key取得，Core继续持有唯一主值。SetDeferred移除旧差额成员、提交主值、按新差额加入目标组；Maintain应用后删除该成员。首次/增长/空组回收按实际组与成员计费。

差额索引Clear沿实际组清成员反查：单成员组由First提供身份，多成员组遍历Items[..Count]，按Id将对应12 B Member置空，再归还该组实际持有的数组。结束时清已用组头和全部桶并重置used/free。成员数组继续保留容量，清理量按当前pending成员、已用组头和桶数计入，Set/Remove沿原有热路径执行。

DifferenceTargets的Group在x64保存Target Entity、First Entity、Items引用、Hash、Count、Next及对齐，共40 B。其目录预算为40c_group B，成员反查为12c_member B，桶为4c_bucket B，另计各多成员组租用的8c_items B身份数组。单成员直接使用First；转为多成员时才取得数组并复制首项。目录首次/扩容按这些实际宽度清零和复制，Clear按上述实际成员及已用组头处理。

销毁身份e时，关系服务取得e的已应用邻接及差额索引中指向e的成员，形成实际候选集合并按完整主体身份去重。逐候选读取当前主值：当前仍指向e的主体执行Detach/edge寿命操作；已经改变目标的主体应用其当前主值，摘掉e的旧邻接。最后处理e自身的Parent/edge并执行Core释放。复制候选、字典查询、实际成员访问与结构迁移全部计费；工作量由注册服务数和局部邻接/差额决定。

显式DestroyDeferred释放Core列前将仍需消费的Children数组移交给按完整Entity索引的旧代数据；Maintain消费后归还。普通Destroy经过即时清理后发布的事实仍由各reader推进。旧代数据和Removed事实分别按各自消费者保持寿命，转交时指定唯一数组持有者；同时需要两份独立旧值时执行一次实际复制。

恢复保留模式保存Applied与差额观察点，并从主值和Applied重建差额目标索引。默认v5的Rebuild在保存前完成Maintain，读取已应用Children及邻接顺序后建立Applied/Place和空差额索引；从主值新建拓扑的入口沿Parent/endpoints构造成员。两条入口的实际输入、遍历与复制费用由[[Serialization]]分别组合。

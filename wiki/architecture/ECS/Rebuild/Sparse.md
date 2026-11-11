# Sparse：位置映射、附着链和完整成本

分析时间：2026-10-22 22:50 +08:00。状态：S-O 为完整旧实现源码；D 为分页方案；E* 身份前缀按原生入口独立计数。

分析方法：固定独立于表组合的侧存储、按实体随机访问、连续稠密枚举、实体销毁后释放全部附着值；逐步计算映射、值、反向附着、首次、扩容与恢复。口径见 [[Cost-Model]]。

## 数据与候选

`SparseStore<T>` 持有 Entity[]、T[]、Count 与映射页目录。`SparseStores` 持有类型目录、每实体首附着编号 First[]、附着项数组与空闲链。附着项只需 `{TypeId,Previous,Next}`，12 B：实体由操作入口提供，行由映射页提供。删除使用该实体的已取得 cell ref。API：Has/Read/Get/Set/RemoveSparse、Entity/Value span、Sparse 查询。

| 候选 D | 已绑定 Store 后成功定位，返回 cell | 每页/每实体映射 | 新增与中间删除的附加维护 | 全部销毁 |
|---|---|---|---|---|
| 8 B 分页 cell：RowPlusOne/AttachmentPlusOne | 〈2,2,3,0,0〉 | 4096 项一页，32768 B；页目录每项 8 B | cell 一次整项写；附着编号直接从 cell 取 | 沿 S 个真实附着项 |
| 4 B 分页 row + 稠密行的附着编号数组 | 定位 row〈2,2,3,0,0〉；需要附着编号再〈1,1,0,0,0〉 | 每页 16384 B；每稠密容量另 4 B/项 | 新增写 row 与附着编号两项；回填再搬编号 A2/W1 | 沿 S 项；各类型再定位稠密附着编号 |
| 平坦 8 B cell 数组 | 〈1,1,2,0,0〉 | 每类型按实体上限 I 分配 8I B | 相同 cell 与附着链 | 沿 S 项 |
| Dictionary<Entity,位置> | Dictionary P1 + Hhit；完整 Entity key 比较 | 桶/entry 按实际成员数 | Hget-new/Hremove 与稠密回填后位置更新 | 沿附着链，或扫全部类型 |

选定 8 B 分页 cell 与 12 B 全局附着项。分页按实际触及的实体区间分配，4 B 候选每页省 16384 B，同时新增一套稠密目录及附着编号读写。平坦映射少 P1/A1/B1，承担每类型 8I B 清零与保留。这里将随机读的分页代价与稀疏内存性质一并保留。

## 源码依据

S-O [SparseSet.cs:67–127、155–179、252–283](https://github.com/ODtian/SomeEngine.Next/blob/80f446207d2968cf0c0a0636b0c1429e44f8fe37/src/SomeEngine.ECS/Sparse/SparseSet.cs#L67) 多一层 Storage；TryDenseIndex 重读页目录并比较 DenseEntities 的完整身份；写入口经过 WritableStorage。S-O [Owners.Sparse.cs:30–138、169–181](https://github.com/ODtian/SomeEngine.Next/blob/80f446207d2968cf0c0a0636b0c1429e44f8fe37/src/SomeEngine.ECS/Owners.Sparse.cs#L30) 在 SparseSet 外再做实体/迭代检查，RemoveAll 扫 Stores 的整个容量 U。

| 源码步骤 S-O | 逐步操作 | D 对应步骤 |
|---|---|---|
| TrySet 类型命中 | stores/对象 P2、A1、范围/类型 B2、out W1、Volatile 读1 | T=〈2,1,2,0,0〉，返回对象；类型目录由 World 结构入口维护 |
| TryDenseIndex 命中 | Storage 由调用者 P1；页范围 P1；页判空 P2/A1；重新取页/项 P2/A2；DenseEntities P1/A1；6 个显式条件 + Entity 相等；out 写4 | SFind=〈2,2,3,0,0〉；实体身份由外层 R 一次确定 |
| 值访问 | DenseData P1/A1；写前 WritableStorage 的 Storage P1、共享 B1/Volatile 读1 | Values P1/A1，写 W1 |
| RemoveDense 中间 | Entity 数组字段读3次、值数组2次；两次页定位；末项两数组清空 | 已绑定数组各一次；POD 只减 Count，实际引用值清尾 |
| 写分离 | Storage 对象、页目录、全部已分配页、两稠密数组复制；共享状态维护 | 完整捕获时复制主数组；普通写按下表 |

EasyEcs 固定版本提供表列存在位；要求分页侧存储与独立稠密枚举时使用 E* 侧存储适配器。普通列的存在变化与 Sparse 的附着变化分别列为功能差额。

## D 读写与附着内核

R/T/G 见 Cost-Model。SFind 返回 pages 与 cell ref：页目录 P1、页元素 P1/A1、cell A1，范围/页空/RowPlusOne B3。所有后续内部步骤复用这些引用。以下 POD、容量足、页存在。

| 步骤 | 〈P,A,B,W,L〉 | 实际访问与写入 |
|---|---|---|
| 已有值 Read/Get | 〈1,1,0,0,0〉 | Values[row] |
| 已有值 Set | 〈1,1,0,1,0〉 | 写 sizeof(T) |
| 新增后半：复用 free 附着项、原首项存在 | 〈4,5,3,8,0〉 | Entity/Values/Attachments/First P4；两值项、free 项、First ref、旧首项 A5；稠密容量/free 选择/原首 B3 |
| 新增含 SFind | 〈6,7,6,8,0〉 | Entity、T、freeHead、12 B 新附着项、旧首 Previous、First、8 B cell、Count，共 W8 |
| 删除后半：链中间且稠密中间 | 〈4,9,3,9,0〉 | 节点/前项/后项 A3；Entity/T 各读写 A4；移动实体的页/项 A2；数组与页 P4 |
| 删除含 SFind：链中间、稠密中间 | 〈6,11,6,9,0〉 | 链重接两写、free 两写、Count、Entity/T 回填两写、移动 row、原 cell，共 W9 |
| 删除含 SFind：链中间、稠密末项 | 〈3,5,6,6,0〉 | SFind、附着项与前后项；链/free W4、Count W1、cell W1 |

附着项首次使用而非 free 复用：将 freeHead 写替换为 AttachmentCount 写，另加附着数组容量 B1；容量增长另加 Grow。链首删除用 First 写替代前项 Next 写，持有 First ref 时省该前项 A1；链尾省后项 A1/W1。T 含引用时额外清旧末值 A1/W1/GC。结构整项写分别标明 12 B、8 B。

| 公共操作 | D 完整向量 | 同能力 E* |
|---|---|---|
| Has 成功 | T+R+SFind=〈5,4,7,0,0〉 | T+R_E+SFind=〈6,5,8,0,0〉 |
| Read/Get 成功 | 上项+值访问=〈6,5,7,0,0〉 | T+R_E+SFind+值访问=〈7,6,8,0,0〉 |
| Set已有值 | T+R+SFind+写=〈6,5,7,1,0〉 | T+R_E+SFind+写=〈7,6,8,1,0〉；所需访问分别计入 |
| Set 新增，复用附着项 | G+T+R+新增=〈9,9,11,10,0〉 | G*+T+R_E+新增=〈10,10,12,10,0〉 |
| Remove 中间 | G+T+R+删除=〈9,13,11,11,0〉 | G*+T+R_E+删除=〈10,14,12,11,0〉 |
| Remove 末项 | G+T+R+尾删=〈6,7,11,8,0〉 | G*+T+R_E+尾删=〈7,8,12,8,0〉 |
| 遍历 n 项 | 绑定 Entity/Values P2；A2n/B(n+1)，值修改另 Wn | 同样两稠密数组 P2/A2n/B(n+1) |

R_E=〈2,2,3,0,0〉来自原生World.IsAlive(EntityId)，G*是保护适配侧数组的一次结构范围；独立调用链见[[Adaptation]]。D Sparse相对D普通列Read多P2/A2/B3，表达分页与独立类型存储的实际代价；性能对照使用上表完整侧存储能力。

## 销毁：把链操作合到批次外

Destroy 已取得当前实体首附着项时，Sparse 一次绑定 Attachments/Stores P2，First ref 沿用外层。顺链读 S 个节点的 next/TypeId，分别按类型删除稠密值并清 cell。该批全部附着项释放后，把整条 Next 链接到原 freeHead：末项 Next、freeHead、First 三写。这样批量销毁直接省去每类型的前后链重接与逐项 freeHead 发布。

批量销毁先按实际成员完成保留值选择，再按普通列能力判断连续尾段回收。符合尾段条件时，先释放该段普通列和位置缓存，再逐身份沿First执行上述实际附着链清理，最后统一回收身份并写入Chunk.Count。World已有Sparse存储时仍沿此流程；First为0的身份只承担一次首附着读取，实际成员承担自己的类型值释放和稠密行回填。

| 每个实际附着类型的步骤，POD | 稠密中间 | 稠密末项 |
|---|---|---|
| 已知活跃 cell | 〈2,2,2,0,0〉 | 〈2,2,2,0,0〉 |
| 稠密删除，cell/node 已取得 | 〈3,6,1,5,0〉 | 〈0,0,1,2,0〉 |
| 节点读取与 Store 定位 | 〈1,2,0,0,0〉 | 〈1,2,0,0,0〉 |
| 每类型和 | 〈6,10,3,5,0〉 | 〈3,4,3,2,0〉 |

完整 Sparse 清理为 P2 的批次绑定 + 各类型上表向量 + 循环 B(S+1) + 批次 W3 + S 次类型化分派 I。含引用值逐类型再清尾。S-O 扫 U 项额外 A U、循环 B(U+1)、类型存在 B U，再对存在的每个 Store 执行 RemoveOptional；新方案按实际 S 个成员付费。

## 查询、复制与恢复

固定 k 个 Sparse 条件的描述为 k 个 int，创建 A k/W k、L0（inline 容量内），长条件数组 L1；与相同条件数的 EasyEcs 描述配对。首次绑定 k 次 T，各 Store 引用缓存到查询的类型条件数组，A k/W k。增长只改变 Store 内部数组，遍历入口重新取得实际页/值基址。

混合查询选最小候选集：读 k 个 Count，A k/B(k+1) 和 k−1 次大小比较；同一遍历内 Count 保持固定。表候选逐条件执行 SFind；成功取值另 P1/A1。Sparse 候选每行先 Entity A1，实体记录 P1/A1，再取预建的匹配表 bool 目录 P1/A1/B1，其余 k−1 条件各 SFind。候选循环 B(n+1)，实际失败按短路停止。选择与数组绑定放在批次外，成员变化直接更新实际 Count。

Clone 读取 S 个附着节点，每项 Store 定位〈1,1,0,0,0〉、源 SFind+值读、目标新增内核；源实体身份 R 一次，目标 R 一次，G 一次，循环 B(S+1)。全类型 Clear 直接遍历 N 个稠密 Entity/值、清其 cell 与附着链，含引用值 Zero_T(N)，Count W1；整个World Clear复用各类型Store对象与容量，清实际成员cell并重置Count；全局附着数据按批次重置。

## 首次、扩容、缓存维护

| 触发 | S-O | D |
|---|---|---|
| 首个类型 Store | SparseSet、Storage、页目录、Entity[]、T[]，L5 | Store、页目录、Entity[]、T[]，L4；Stores 项 A1/W1 |
| 首个映射页 | int[4096]，L1；Zero_int(4096)+Fill_int(4096,−1) | Cell[4096]，L1；Zero_Cell(4096)；页项 A1/W1 |
| 首次全局附着索引 | 销毁扫 U 项 | First[I]、Attachments[c]，L2，清零 4I+12c B |
| 页目录增长 | Grow(ref,c,c',c) | Grow(ref,c,c',c) |
| 稠密增长 | 两数组 L2，复制 Entity/T 的旧容量 | Grow(Entity,c,c',N)+Grow(T,c,c',N) |
| 附着/首项增长 | 对应类型目录容量继续影响销毁扫描 | Grow(Attachment,c,c',used)，12 B/项；Grow(int,I,I',I)，4 B/实体 |
| 4 B 映射候选增长 | 每页较小 | 相比选型额外 Grow(int,c,c',N)，稠密附着编号 4 B/项 |
| 显式捕获 | Storage 共享，首次写复制全部已分配页与两稠密数组 | 复制 Entity/T 主值；完整恢复逐 S 行写 cell 与附着链 |

恢复时已有 Entity/T，新增后半扣除 Entity/T 两次数组写与引用绑定，Count 在类型末统一发布；映射/12 B 附着项/旧首 Previous/First 各按实际 S 项写入。值索引与追踪仅为已开启类型重建，费用并入 [[Serialization]]。

## 路径与生命周期设计

Set 已有项只写稠密值；新增项先 Reserve 稠密数组和全局附着项，再定位 page/cell 与实际数组。独立 Remove 用已取得 cell 完成附着链摘除；DestroyAllAttached 直接顺整条链清各类型并整链归还。Buffer/托管 Sparse 值分别调用实际释放/清尾，能力由闭合 T 决定。

混合查询先取得结构读取范围，再选择本次最小候选集并绑定各Store；该范围固定各Count与映射数组。表布局匹配目录跟随 [[Storage-and-Queries]] 的新增表进度刷新；写入和附着修改在范围外即时执行或随命令回放。

E* 的包装 Destroy 先用完整 EntityId 清实际附着，再调用原生 Entity.Destroy，并追加其适配事实；原生结构入口与身份检查按调用实际保留。恢复时先填 Entity/T 主数组，再逐项建立 cell/附着链；完整批次在末尾发布 Count。

## Clear/Restore与已绑定查询

SparseStore对象按World寿命保持，查询缓存持有这个对象。Clear遍历实际N个成员清cell，清有效引用值，Count归零；World批量清理同时重置First、附着Used/free字段。页目录、稠密数组和对象供后续写入复用。Restore在相同Store上安装实际数组并重建cell/附着链，范围开始重新取基址。Dispose最终归还数组并结束对象寿命。

类型级Clear清该类型的实际附着链项；全World Clear沿实体记录批次重置First并统一重置附件目录。两条路径分别按真实成员、实体和引用清理数量计费。

World最终释放沿各Store的ReleaseData执行实际拥有值释放，再把Count归零并交还Values、Entities、Pages和Changes数组引用。已建立的Sparse查询继续引用同一Store，枚举读取归零后的Count。Clear则沿上述可复用路径保留Store及其容量；两种完成点的实际处理由[[Execution]]统一组合。

## Sparse的追踪与并行范围

声明追踪的SparseStore按需建立单项ComponentChanges数组，持有与[[Changes]]相同的行版本、64行词和4096行块摘要。稠密row作为版本索引；Get/Set登记Written，新增登记Added/Written，交换删除按CopyVersion保存搬入行的原版本并清尾位。首次与增长包含该32 B变化头和实际Rows/Words/Blocks数组。

保留移除值的Sparse类型在Remove时把旧值交给实体的普通Removed<T>事实，再完成稠密删除；Core事实所需最终布局、容量与独立释放均进入完整费用。Destroy按Changes页的统一保留规则处理实际附着项。

Sparse值资源键使用Sparse存储种类及TypeId，范围为稠密行；摘要键使用Sparse摘要种类及TypeId，覆盖该Store。独立已有值Set取得值写和所需摘要写；附着增删取得结构写。普通未追踪已有值主体为T+R+SFind+写=〈6,5,7,1,0〉，公开加E；结构路径继续使用G组合。

生成范围记录其驱动布局。主列驱动以Chunk/行定位，Sparse驱动以TypeId/稠密行定位；生成工作体据此获得实体及其余值。并行时，实际追踪写入均位于同一驱动布局并按词分区；涉及多个独立追踪写布局的批次选择串行范围执行。未追踪的其余值按唯一实体行写入，实际列/映射定位分别计费。随机写的权限核对使用驱动布局的实体成员位置；细分的跨存储工作可用普通Schedule声明自己的实际访问区间。

# 首次构造、缓存增长与过滤内核

分析时间：2026-10-22 22:50 +08:00。状态：D 冷路径设计预算；S-E 对照使用同一固定提交。

分析方法：把首次类型、表、边、查询和 Chunk 分配拆成独立数据构造；按相同最终结果计入新数组清零、有效数据复制和查询首次可见。基础向量见 [[Cost-Model]]。

## 输入与容量

K 为实际组件数；U 为最高 TypeId+1；w=ceil(U/64)；c 为行容量；n 为活跃行；Kp/Km 为普通 POD/含引用列数；Kt 为开启追踪的列数；Ke 为已分配启用位的列数。Tag持有列元数据，Shared每列另持有4c B的行编号数组，共享主值及成员目录由[[Shared]]持有。每个变量由本次具体表或查询决定。

默认行容量从16起，内部增长取RoundUpToPowerOf2(max(needed,2c,16))；公开ReserveRows按调用方给出的行容量建立行段。每表按实际列大小计算MaxRows，使每个底层数组满足Array.MaxLength；批次Reserve据此分配。固定Chunk配置给出c_fixed，表持有PrimaryChunk、Chunks/Count与可写块链；ReserveRows的最后一块使用min(c_fixed,剩余行数)。增长模式达到单数组容量后沿相同多块路径追加。

批量创建在已知本批行数超过当前Chunk容量时，通过ReserveRows一次预留现有行数加本批行数；较小批次继续沿既有几何增长入口预留。调用方给出的后续临时实体容量同样通过公开Reserve计入相应阶段。

初始能力已知时，调用方通过现有组件组合建立最终列。随机访问Job把JobOrdinal及随机写所需JobExtra随Position、Velocity、Health和既定标签组合创建；编号写入、常量填充和容量准备一起进入首次成本。后续查询与缓存位置目录绑定这些已建立的列，跨实体访问继续沿[[Jobs]]声明的全类型访问登记。

Append取得边缓存的TargetChunk；容量足时直接使用。满块时，增长模式先扩大当前块；达到本表MaxRows或选定固定块时，沿可写块链跳过已满项，链空时建立并登记一块。Chunk保存NextAvailable和InAvailable；满块删除一行后，在首次重新可写时加入链头。默认PrimaryChunk在表寿命内保留，查询每次范围读取当前块目录。固定块追加、可写链维护和边缓存回写分别计入实际触发事件。

创建Chunk时取得ArchetypeStore的下一个Chunk编号；首次出现第二块时设置HasMultipleChunks。查询在每次范围开始读取该标志。新增Archetype同时登记其已建立CachedLookup类型，后续Create/Move按紧密列表更新实际位置项。

类型、组合、边、查询和成员目录从小容量起倍增，显式 Reserve 可直接达到需要值。Grow 的判断由调用点承担一次；分配、初始化、复制和发布分别入账。Array/Span 自身的控制项使用相同实现配对。

## 位图和组件序列

每种固定泛型组合首次生成排序去重的 int[] 与位图；通用动态组合先收集实际类型，再形成同样的结果。表持有不可变位图和 TypeIds。字典键引用表持有的长尾，散列值在该内容建立时计算一次。

以下输入和输出 span 已绑定；元素定位都按 A 计，局部变量赋值属于算术。实际 inline 256 bit 专用实现可将相同工作展开成定宽位操作。

| D 内核 | 〈P,A,B,W,L〉 | 实际工作 |
|---|---|---|
| 两个位图合并/相减/相交，w 个已补齐词 | 〈0,3w,w+1,w,0〉 | 读两个词、写一个词；每词一次位运算 |
| 向既有位图增删 a 个给定 TypeId | 〈0,2a,a+1,a,0〉 | 读 ID、定位目标词、更新目标词 |
| 枚举 w 词中的 K 个置位 | 〈0,w+K,2w+K+1,K,0〉 | 读词、逐置位写 ID；TrailingZeroCount K 次 |
| 位图长尾建立 | L1 + Zero_ulong(max(0,w−4)) | 内联四词随结构值存储 |
| TypeIds 建立 | L1 + Zero_int(K) + 枚举写入 | K 在元数据统计时已确定 |
| 组合内容去重 | Dictionary P1 + Hhit/Hget-new | Hash/Equals 各读取实际词；已缓存散列直接读一次 |

位图新建与 TypeIds 枚举各发生一次。按类型编号建立直接列目录时，每个 TypeId 写 ColumnPlusOne，一次 A/W；紧密列号在同一循环产生。S-E ToBitArray 扫描位容量中的各 bit，D 按词枚举置位；两侧最终 ID 数组的 2 B/4 B 项宽分别保留。

源码：[BitMask](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/BitMask.cs)、[首次表去重](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Archetypes.cs#L121)。

## 首个表和 Chunk

| 分配数据 | S-E 具体内容 | D 具体内容 |
|---|---|---|
| 表/Chunk 对象 | Archetype 一对象 | Archetype、主 Chunk 两对象 |
| 实体行 | 8c B，L1 | 8c B，L1 |
| 组件序列 | ushort[K]，L1 | int[K]，L1 |
| 类型到列 | short[U] + 32 B Lookup[U]，L2 | 32 B ColumnEntry[U]，L1 |
| 列及固定元数据目录 | Columns/Present/Absent/LastWrite/Words/Blocks/Removals/infos，共8数组 | TypeSet首次使用时建立ComponentColumns及实际非空描述数组；各World的表引用同一类型描述 |
| 普通值 | 每Chunk每列创建数组；POD 含63 B对齐余量 | World内每POD类型一个缓冲区，各Chunk追加行段；每Chunk含引用列各一个T[] |
| 启用数据 | Present 目录在建表时存在；位数组首次缺失时建立 | DisabledCounts[K] 在首次需要时 L1；每已启用列位数组 L1 |
| 追踪数据 | companion 列和 Words/Blocks/Removals | Changes[K] 32K B，首次有追踪列时 L1；每追踪列的版本/摘要见 [[Changes]] |
| 行复制描述 | Transition 首次另外建立 | POD 8Kp B、托管描述按实际类型；表内描述供边复用 |
| 边目录 | 空共享数组，首次操作扩容 | 空共享数组，首次操作扩容 |
| 表集合登记 | 分配新全表数组并复制旧 A 项 | 容量追加 A1/W1、Count W1；满时 Grow |
| 查询维护 | 对全部已绑定查询执行匹配并复制命中数组 | 首次打开查询时执行新增表匹配，见下节 |

D基础分配按对象/实际数组逐项求和：2个表/Chunk对象、实体数组、TypeIds、ColumnEntry、Km个含引用值数组、位图长尾及实际可选元数据；每种POD类型首次使用时另建立其World缓冲区和行段目录。32U B直接目录清零、32K B变化头目录、POD缓冲区容量及对齐余量均计入首次或增长。TypeSet持有固定TypeIds及惰性ComponentColumns，后者一次分类普通POD、托管、追踪、独立值、索引、Shared、分组、初始化、计算和清理类型，并保存生命周期/实体引用标志。闭合泛型组合跨World复用这份只读分类，动态组合在首次实际建表时建立；Archetype和GroupTables沿该TypeSet取得描述，World继续独立持有值、已激活分组和方向边。首次并发绑定通过一次CompareExchange发布完成的描述。S-E表和Chunk合一少一个对象；D的Chunk身份服务固定Chunk和数组增长后的引用稳定性。

列注册操作闭合到具体 T。运行时 Type 查找只发生在动态注册/外部类型解析；列建立按 K 项执行类型化 Create，I=Kp+Km。成员存在/元数据字段和 Values/Rows 各在建表循环写入一次。

## 首条迁移边

D 顺序为：方向目录查缺 → 形成最终类型位图及实际GroupBy键组合 → 组合表查找/建表 → 建立共有列描述 → 建立附加转移描述 → 发布边。Shared编号随行转移，其成员目录由共享值修改入口维护。

| 阶段 | 热复用后的首次成本 |
|---|---|
| 最终集合 | 上表位图运算；只在首次建边发生 |
| 目标存在 | 内容键查找一次；命中目标作为边的 TargetChunk |
| 目标首次出现 | 前节完整建表与登记 |
| 纯 Add/Remove POD 共有描述 | 取得共有一侧只读描述 P1，保存引用 W1 |
| 混合操作交集 | 两个排序序列扫描实际 v 项；每项比较一次；结果 8k B 数组 L1，写 k 项 |
| 托管、启用、追踪步骤 | 按真实共有类型建立紧密列表；分别计元素读写与数组 L |
| 边对象 | L1；目标、POD 描述、附加描述引用各 W1 |
| 方向目录增长 | Grow(ref,capacity,newCapacity,used)，再发布边 A1/W1 |

源 Transition 同时持有完整、POD、托管三套步骤，并在构造时使用三个 List。D 将实际附加状态的迁移步骤放入一个按种类分段的紧密目录；POD 采用表描述复用，混合交集单独建立。已有边的目录和目标读取见 [[Storage-and-Queries]]。源码：[Transition 构造](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/Archetype.cs#L1230)。

## 首次查询与增量刷新

查询绑定先取得目标描述；首次新建 QueryData、匹配数组与实际行过滤描述。普通固定签名的描述来自静态类型数据，世界内的结果各自持有。

给定 ΔA 张新增表、r 个匹配结果、每表实际检查 w_t 个词和 f_t 个需要绑定的行条件，完整刷新为：

1. 绑定 All/Matches/描述基址；循环 ΔA 次，读取 ΔA 个表引用。
2. 每表执行表级位图匹配。通过时绑定该表的 f_t 个行条件。
3. 追加 r 项 Match，容量充足时每项〈1,1,1,2,0〉；在整个刷新外预留 r 时把容量检查移到批次外。
4. 写 CheckedArchetypes 一次。首次绑定还发布 World 的查询目录项一次。

| D 表级内核，span 已绑定 | 通过完整 w 词的向量 | 短路方式 |
|---|---|---|
| 包含所有必需词 | 〈0,2w,2w+1,0,0〉 | 每词 AND 与期望比较，首个缺项结束 |
| 排除普通类型词 | 〈0,2w,2w+1,0,0〉 | 首个非零交集结束 |
| Any 类型词，第 j 词命中 | 〈0,2j,2j,0,0〉 | 首个非零交集成功 |
| 绑定 f 个行过滤列 | 〈1,3f,f+1,f,1〉 | 已有条件 ID span；Columns P1，每项读 ID/列，写过滤项；输出新数组 L1 |

最后一行的 A 包含 ID、列读取与过滤项写入。使用 stack/复用构建区时输出 L 由最终持久 Filter 分配承担一次。过滤项保存 TypeId、条件种类与紧密元数据下标；范围开始再取当前 Chunk 的值/位/版本基址。

热查询固定签名目录与新表检查共〈2,1,3,0,0〉。描述构造 L0、首次绑定 O(已有表数×实际词数)、后续绑定 O(1)、增量刷新 O(新增表数×实际词数+r)。S-E 按相同描述、相同表集合与首次可见点比较。

首次/增量绑定在结构读取范围内锁住私有QueryData：Monitor进入/退出各一次，进入后重读匹配版本。对象本身作为冷路径锁；数组、Count和AllRows写完后release写CheckedMatches，热读取使用一次acquire读。CheckedArchetypes保存增量扫描进度。查询目录首次注册使用目录锁，完成对象初始化后发布目录项；目录增长先复制再发布新数组引用。等待/唤醒按实际后端调用计。版本职责与范围入口由[[Storage-and-Queries]]及[[Jobs]]持有。

## 行过滤的实际工作

范围开始把实际参与的 E 个启用位、C 个变化列基址绑定到局部；绑定成本按每项所需引用与列定位计入一次。下表各词内循环已展开，P0，实际条件数按表匹配结果确定。

| 内核 | 数组与写入 | 分支与算术 |
|---|---|---|
| 启用 All/None | 每条件每词 A1；输出掩码在局部 | AND/AND-NOT 各一次；通用条件循环 B(E+1)，生成展开时为0 |
| 启用 Any | 每条件每词 A1 | OR 一次；结束与 All 掩码相交 |
| 最后一词有效行裁剪 | A0/W0 | 按实际 n 的尾部掩码一次 |
| 普通 Changed | 每列 LastWrite 字段；命中块/词各 A1；需要精确判断的行版本 A1/行 | 列/块/词与基线比较，实际进入的摘要数各计一次分支；行版本 > last |
| Added | 通过 Changed 候选后定位 RowVersions A1/行；已有行 ref 直接复用 | Added > last 一次；初始化与增长见 [[Changes]] |
| Count | 每词生成掩码后 A0/W0 | PopCount 一次 |
| Any | 每词生成掩码后 A0/W0 | 非零判断一次，首次命中结束 |
| 行输出 | 每返回行身份 A1、每值列 A1 | TrailingZeroCount 与清最低位各一次；输出行循环 M+1 |
| Optional | 每范围列存在性一次；有启用位时每行 A1 | 存在/启用判断在相应层执行 |

这里保留真实访问次数 w_t/E/C/M，完整入口 = 绑定/范围 + 实际访问的词/块/行 + 用户计算 + 范围完成。相同条件、相同命中集合与同样已绑定基址才逐项相减。

## 首次启用、增长与回收

首次位数组：L1、Zero_ulong(ceil(c/64))、Fill_ulong(ceil(n/64))、发布 Enabled W1；首次 DisabledCounts L1/Zero_int(K)；ActiveEnabledTypes 追加〈1,1,1,2,0〉。启用位只覆盖真实活跃行，新增行按创建路径设置相应位。

Chunk增长分别处理Entities、POD行段、含引用值数组、Ke个位数组和Kt个版本/摘要数组。POD行段按注册顺序紧密保存，增长差额delta为新旧行容量之差。末尾行段直接扩展；中间行段将其后的保留行段整体向右移动delta行，清零该段新增范围，再更新后继起点及类型位置缓存。含引用列复制活跃n项并按实际引用执行屏障。摘要容量按新行数的词/块数增长，旧有效摘要直接复制。

表对象、Chunk对象和TypeIds保持固定；查询保存Chunk引用，范围外重新绑定实际数组。固定Chunk首次建立同一组列数据，追加Chunks与可写块链。Clear/Dispose的值释放与缓存处理见[[Execution]]。

单组件与多组件组合共用Reserve<T...>(capacity)入口，准备该组合的实际行容量。单组件重载沿同一TypeSet、POD提示、Find与ReserveRows内核处理；调用方按本组合的实际目标容量提供capacity。

## World的POD列缓冲区

World以TypeId直接索引PodColumnBuffer；一个缓冲区持有该类型的固定地址字节数组、Rows基址、UsedRows、CapacityBytes及16 B行段目录。行段为{Chunk,StartRow,Capacity}，Chunk的ColumnEntry.Values引用相同数组，Rows指向该行段起点。主值由缓冲区持有，行身份与行号继续由各Chunk持有。

首次所需字节为max(4096,neededBytes,initialRows×sizeof(T))，neededBytes由该类型实际申请的行段末端乘sizeof(T)得到。显式Reserve<T...>先沿TypeSet的POD描述建立对应类型缓冲区，首次建立时以World当前实体记录容量给出initialRows提示，再通过Find及ReserveRows准备该组合的实际行；按需出现的类型以initialRows=0及4 KiB起始容量建立。首次按所需字节分配，后续增长在所需字节超过旧容量时取max(所需字节,旧容量+floor(旧容量/2))，再向上对齐至4096 B。该策略保持几何增长，行段请求超过1.5倍增长值时按实际需求及页对齐建立容量。实体记录容量由World的身份目录持有，各类型值容量由自己的行段及显式容量提示持有。每次实际分配、清零、复制及行段目录增长均在触发操作中发生。含引用类型与Shared编号沿各Chunk的typed数组路径建立。

行数据容量达到32 KiB时，先取得4096 B对齐基址，再加(TypeId&3)×1024 B的类型前缀，分散页内起点；较小缓冲区先取得64 B对齐基址。首次建立且容量≤256 KiB时，类型偏移为((TypeId&1)×32) XOR (((TypeId>>1)&1)×48) B；较大首次缓冲区与后续增长采用(TypeId&1)×32+((TypeId>>1)&1)×16 B。起点保留16 B对齐，数组长度计入容量、前缀、类型偏移和max(63,alignment−1) B对齐余量。该策略决定类型值缓冲区的起点，Chunk列视图继续按StartRow×sizeof(T)取得实际行段。

1.5倍几何增长降低小幅追加后的容量余量；持续小步增长会增加扩容次数和累计复制。理想几何链在相同末容量C下，旧2倍策略的历史容量和约C，1.5倍策略约2C；实际复制沿UsedRows与页对齐逐次计入。中间行段扩张另计后继保留行数×sizeof(T)的重叠安全复制；若同时扩字节数组，本次复制为旧UsedRows与后继保留行数之和乘sizeof(T)。已给定大步Reserve时沿max的需求项直接准备所需容量，字段与所有权保持原布局。

POD缓冲区首次使用GC.AllocateUninitializedArray<byte>(length,pinned:true)取得固定地址数组。后续增长的数组长度小于85,000 B时沿同一路径分配；更大增长使用普通未初始化数组并以GCHandle固定。Add/Grow交出新行段前清零新增有效范围，尚未交出的容量随实际使用初始化。数组、固定句柄、Rows、行段和容量由同一个PodColumnBuffer持有，GC引用沿列值和公开ref/span维持数组生命期。

缓冲区增长先复制UsedRows×sizeof(T)字节，随后更新每个已登记Chunk的Values和Rows，再释放旧固定句柄。POD的CachedLookup保存相对类型缓冲区基址的字节偏移，单纯更换数组时沿用已有偏移与Generation目录；下一读取范围从同一个缓冲区对象取得新Rows基址。中间行段扩张在同一结构写范围内完成尾部搬移，随后把所有后继StartRow增加delta并重绑其Column.Rows；沿各表既有Cached目录筛选相同TypeId，调用BindRange按Presence重绑位置、Generation及RowsFollowIds。当前增长Chunk继续由Chunk.Grow末尾重绑。旧数组沿其GC引用生命期回收。UsedRows为各实际行段Capacity之和，容量充分时搬移L0，实际保留内存沿整个字节缓冲区容量计入。

同一次Chunk.Grow的各POD列使用相同的行容量差额与行段顺序，行段起点变化独立于sizeof(T)及各自字节数组扩容门槛。因此，具有相同行段集合的多列继续共享行起点；连续多列视图与逐行段回调沿既有绑定入口消费。字段、行身份、行号和对象持有者保持原布局。

TypeId、Rows、UsedRows与完整性状态由同一个缓冲区对象的连续基类字段保存，行段目录、数组引用和增长参数随后保存。该对象同时接入CachedComponent的维护回调；布局调整保持主值、行段和完整性状态的同一持有者。Query的单类型借用引用指向这个对象，增长时取得对象更新后的Rows；Clear与Restore复用World中的类型缓冲区，Dispose清空实际数组引用和行段。

Clear复用已建立的Chunk与列容量；Dispose沿实际建立的缓冲区逐个释放当前固定句柄、解除数组引用并清空行段目录，同时抑制该缓冲区的终结器。缓冲区终结器负责释放随World回收的当前固定句柄。取得的ref/span在下一次结构修改前使用，公开借用签名见[[Implementation]]。连续值视图使用本页建立的行段；选择条件与遍历规则由[[Storage-and-Queries]]持有。

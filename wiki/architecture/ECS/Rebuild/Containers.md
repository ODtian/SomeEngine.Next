# Buffer 与资源：候选、完整路径和选型

分析时间：2026-10-22 22:50 +08:00。状态：S 为源码计数；D 为本次选型与设计预算。

分析方法：固定单个缓冲独立持值、连续 span、实体迁移后保持内容、深复制和实际回收语义；先计算已取得行内 buffer ref 的操作，再加实体定位、追踪、容量和池。统一口径见 [[Cost-Model]]。

## 数据与候选

Buffer组件按元素类型T注册，默认在Core组件行保存T[] Items和int Count，x64行宽16 B。每行独立持有元素数组；空值的Count为0，首次取得可写视图时由World的BufferPool取得空头或已归还的数组。公开ReadOnlyBuffer<T>/DynamicBuffer<T>为只读/可写ref struct视图，分别借用实际组件行的ref readonly/ref。

以下比较固定相同长度 n、容量 c、同一 T；入口已经取得行内 buffer ref。h 是显式声明的行内容量，d 是实际搬移项数。

| 候选 D | 索引读 〈P,A,B,W,L〉 | 容量足的 Add | 第一次放入元素 | 行迁移与回收 |
|---|---|---|---|---|
| 独立对象、连续 T[] | 〈2,1,2,0,0〉 | 〈2,1,2,2,0〉 | 空池 L2：对象、元素数组；池命中 L0 | 行搬 8 B；回收一次 Count 写和一次池归还 |
| 行内 Items/Count、连续 T[] | 〈1,1,2,0,0〉 | 〈1,1,2,2,0〉 | 空池 L1：元素数组；池命中 L0 | 行搬 16 B；回收一次 Count 写和一次池归还 |
| 行内 h 项，溢出连续数组 | 行内〈0,1,2,0,0〉；溢出〈1,1,2,0,0〉 | 行内〈0,1,2,2,0〉；溢出〈1,1,2,2,0〉 | 前 h 项 L0；首次溢出 L1 + Copy_T(h) + 引用发布 W1 | 每行至少搬 16+h·sizeof(T) B；回收溢出数组 |
| 每类型共享元素区，行内 Offset/Count/Capacity | 已绑定共享区时〈1,1,1,0,0〉 | 已有区间时〈1,1,1,2,0〉 | 分配区间；共享区不足时增长整个元素区 | 行搬 12 B；每次释放区间维护空闲区索引 |

共享元素区的表外绑定为 T=〈2,1,2,0,0〉，取得其 Items 再 P1；表内整批绑定一次。上表的共享区列以该绑定完成为边界。区间扩展需要新连续区间：扫描 v 个空闲区时，空闲目录 P1、A v、B(v+1)+v 次长度比较；命中时修改区间起点/长度 W2。整体增长另复制该元素区已用 a 项，代价 Copy_T(a)。分裂、合并各写两个相邻区间字段；使用有序空闲目录时再计二分 b 次 A/Compare 与实际搬移 f 项的 Copy_Range(f)。这些是完整 Add/Remove 的分配器费用。

选型：默认采用行内Items/Count及独立元素数组；声明行内容量的组件采用h项行内版本。默认布局每行16 B，容量来自元素数组Length，局部扩容和归还沿该行完成。独立对象布局每行8 B引用，另有一个约32 B的Items/Count对象；行内头把两处存储合并为一处，并减少一次指针读取及一次首次分配。四种候选按相同读取、首次分配、增长复制和行迁移口径入账。

## 有源码依据的 Buffer 内核

S-E：[DynamicBuffer.cs:44–152](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/DynamicBuffer.cs#L44)，[池、增长与复制:191–300](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/DynamicBuffer.cs#L191)。D 在入口将 Items 取得一次。以下 POD 路径，容量充足。

| 相同操作与输入 | S-E 〈P,A,B,W,L〉 | D 行内头 〈P,A,B,W,L〉 | 两侧实际数据工作 |
|---|---|---|---|
| 索引读 | 〈2,1,2,0,0〉 | 〈1,1,2,0,0〉 | 读取一项 |
| AsSpan | 〈2,0,1,0,0〉 | 〈1,0,1,0,0〉 | 相同 Span 构造另加 |
| Add | 〈3,1,2,2,0〉 | 〈1,1,2,2,0〉 | 元素写、Count 写 |
| AddRange(n) | 〈3,2n,2,n+1,0〉 | 〈1,2n,2,n+1,0〉 | Copy_T(n)、Count 写 |
| Insert，搬移 d | 〈5,2d+1,3,d+2,0〉 | 〈1,2d+1,3,d+2,0〉 | Copy_T(d)、元素写、Count 写 |
| RemoveAt，搬移 d | 〈3,2d,2,d+1,0〉 | 〈1,2d,2,d+1,0〉 | Copy_T(d)、Count 写 |
| RemoveAtSwapBack | 〈3,2,2,2,0〉 | 〈1,2,2,2,0〉 | 末项读、补位写、Count 写 |
| Clear | 〈1,0,1,1,0〉 | 〈0,0,1,1,0〉 | Count 写 |

Copy的A/W已展开在表中，其Kcopy控制项另加一次。含引用的Remove再清尾项A1/W1；含引用的Clear再加Count判断与Zero_T(n)。D的Count直接读行字段；Capacity读取Items一次。EnsureCapacity复用已绑定Items，容量判断后按needed返回或扩容；TrimExcess调用Array.Resize至实际Count。

## 从公共入口到结束

| 完整路径 | S-E | D |
|---|---|---|
| Entity 读缓冲并索引 | 原生 Read 定位 + 表中索引读 | 定位与元素内核〈5,4,6,0,0〉，再加独立借用范围费用 |
| Entity 写缓冲并 Add | 原生 Get 定位 + S-E 变化登记 +〈3,1,2,2,0〉 | 定位与Add内核在追踪关闭时为〈5,4,6,2,0〉，再加独立借用范围及实际变化登记；首次空头另计Rent及16 B头写 |
| 查询内连续处理 n 个缓冲 | 批次绑定列与追踪，n 次 buffer 内核 | 批次绑定列与追踪，n 次 buffer 内核；每行取组件项 A1 |
| 组件 Add/Remove/实体 Destroy | 原生存在位变化 + Buffer 回收 | Add/Remove按M(k)、Destroy按X，再加入实际Buffer回收；Enable/Disable保留Buffer内容 |
| 深复制 n 项 | 非空 L2 + Copy_T(n) + 构造字段写 W3 | 空池新目标L1 + Copy_T(n) + 16 B头写；已有容量L0 + Copy_T(n) + Count W1及目标头写 |

独立ReadBuffer/GetBuffer由BeginValueRead开始借用，视图Dispose沿EndValueRead结束：同步Core对_readers递增/递减，共W2；入口与结束分别选择执行连接，Dispose另判断本视图的_release。连接Job时这两次计数为原子更新，并组合[[Jobs]]的类型访问取得/释放。查询内GetBuffer/GetBuffers复用外层已取得的范围，子视图沿_release=false结束，外层统一承担一次计数和访问释放。完整费用由上表内核、实际借用入口及E/Job层相加。

ReadBuffer在BeginValueRead已经建立的权限内复用Access取得同一Buffer列，保留身份、代数及存在性检查。ReadBuffer/GetBuffer在完整构造本地视图后设置成功标记，finally为构造失败配对EndValueRead，成功交给视图Dispose；写借用的Rent、NextVersion和Stamp顺序保持。finally形态保留实际内联资格，机器栈及代码体积由受限JIT回执单列；持久字段、容量、堆分配及业务复制量保持原值。

S-E DynamicBuffer 自带 ITrackChanges。比较保留变化查询时，两侧加入 [[Changes]] 的已开启登记；默认关闭的收益另列为整个变化登记向量。S-O [DynamicBuffer.cs](https://github.com/ODtian/SomeEngine.Next/blob/80f446207d2968cf0c0a0636b0c1429e44f8fe37/src/SomeEngine.ECS/DynamicBuffer.cs#L82) 还经过 Chunk 缓冲头、可写分离和 MarkChanged；这是旧实现的完整能力费用。

## 首次、维护、增长与归还

| 触发 | S-E | D 行内头 |
|---|---|---|
| 首次 Store，空池 | Rent 的空值/Count 判断；对象 L1，空 Items 共享 | Rent的Count判断，返回16 B值；空Items共享，L0 |
| 首次类型池 | 原生类型池的实际建立 | 每个World/Buffer<T>建立一个BufferPool<T>对象L1，后续租借沿同一对象 |
| World类型池目录增长 | 按原生类型目录实际处理 | 8c B引用目录，L1、Zero_ref(c')、Copy_ref(c)、发布W1 |
| 首次非空元素数组 | L1 + Zero_T(c')，引用 W1 | L1 + Zero_T(c')，引用 W1 |
| 池命中 | 〈2,2,2,2,0〉；发布组件 Store W1 | 相同目录读取、清尾及Count修改；每项16 B，发布组件头W1 |
| 数组 c→c' | Grow(T,c,c',n) | Grow(T,c,c',n)；容量策略为至少 4、倍增与 needed 的最大值 |
| 回收 | 清实际引用 n 项；Count W1；Items P1、保留上限 B1；池 Return | 清实际引用 n 项；Count W1；Items P1、保留上限 B1；池 Return |
| 首次 free 目录 / 目录增长 | L1、Zero_ref(c')、Copy_ref(c)、发布 W1 | L1、Zero_Buffer(c')、Copy_Buffer(c)、发布W1；每项16 B |
| 行内首次溢出 | E* 使用同 h 行内适配：L1 + Copy_T(h) | L1 + Copy_T(h) + 溢出引用 W1；后续仅复制该缓冲的 n 项 |

Return内核为〈1,1,2,2,0〉，空闲目录每项保存16 B头；池保留元素上限采用4096 B，超过时释放该行对数组的引用。缓冲的引用清理、归还上限判断与池容量判断各发生一次。表中的分配清零已计实际容量，构造字段的引用写按实际GC屏障另列。默认追踪关闭使普通Buffer写入只修改值与Count；开启追踪时由取得写引用的入口登记一次。

## 资源

资源主值由ResourceValue<T>.Value持有，HasValue表达当前资源存在性，World._resources的ResourceEntries沿静态泛型ID定位该类型存储对象。前32项直接位于目录，后续编号沿可增长尾数组取得对象。S-E [World.Singletons.cs](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Singletons.cs) 具有相同定位形态。

| 操作 | S-E / D 的已展开入口 | 首次与维护 |
|---|---|---|
| Read/Get | P2/A1/B2 | 首次对象 L1，目录项 A1/W1 |
| Set 已存在 | P2/A1/B2/W1 | 写 sizeof(T) |
| Remove 已存在 | P2/A1/B2，置空目录 W1，重置 T W1 | 实际 Buffer/用户资源释放各调用一次 |
| 目录增长 | Grow(ref,c,c',c) | 清 8c' B、复制 8c B，发布 W1 |
| 存档/复制 | 绑定一次 T codec，调用一次实际值编码/复制 | 见 [[Serialization]] |

## 容器接口与释放设计

默认Buffer组件行保存Items/Count，BufferPool<T>持有可归还的Buffer<T>[]及Count。带h容量的缓冲由闭合组件类型声明，SourceGen生成固定的行内字段与溢出引用；同一ComponentId在所有World使用相同物理布局。h和溢出分派在该类型的实际方法中确定。

ReadBuffer返回ReadOnlyBuffer，提供Count、只读索引与ReadOnlySpan；GetBuffer返回DynamicBuffer，提供写索引/span及Add/Insert/Remove/Resize/Clear等方法，并在取得写入口时登记一次变化。Resize沿Reserve保证容量，实际扩容时分配数组并复制Count项，新增区间归零，缩小时清实际引用，再更新Count；之后取得的span绑定当前数组。跨行迁移转移实际容器，Clone/Prefab/Capture独立复制元素。Remove/Destroy/事实替换只在该容器真正释放时清实际引用并归还一次。

World.ClearValues在逐列释放前，通过内部IBufferPool.ReserveReturns把本次World交给已有池。BufferPool<T>沿现有Archetype/Chunk目录，汇总Buffer<T>和已声明保留的Removed<Buffer<T>>列行数上界，再加池内现有空闲数，一次准备16 B头的目录容量。随后原有ReleaseRows、RemovedOperations与Return按原顺序转移实际头；预备扫描按实际池数和布局数计入Clear及整体恢复的费用。Dispose沿最终释放直接结束数组持有期。

GetBuffer在身份和存在性检查后保留实际ColumnEntry ref，用它取得组件行并沿同一ColumnPlusOne定位变化记录。版本仍由World.NextVersion取得，ComponentChanges.Stamp沿既有Written/Added及摘要规则登记；输入列项同时服务值定位与变化定位。

QueryChunk.GetBuffer(row)复用查询范围已经持有的行寿命，沿实际ColumnEntry ref取得缓冲头并登记同一行的变化，视图的Dispose沿查询范围结束。

QueryChunk.GetBuffers<T>()提供整段写入入口，返回ref struct BufferRows<T>，由Count和DynamicBuffer<T>索引器表达实际行段。入口沿现有StampRange登记本段Written并保留Added版本，绑定一次BufferPool与缓冲头span；索引器按实际需要租用空头。查询范围持有全部视图的行寿命，普通单行GetBuffer继续登记自己的实际行。

初始化从已附着的Buffer列按有效行准备容量并写共同元素：SomeEngine使用GetBuffers行段写视图，EasyEcs使用原生ForEachBatch，Friflo与Arch使用原生查询块span，DefaultEcs使用GetAll组件span。实际数组取得、缓冲池租用、首次查询绑定及追踪登记进入各自首次能力费用。

Buffer初始布局沿各库已有批量创建能力准备：SomeEngine复用四至六类型双值span创建，独立值沿实际CloneValue形成各行；EasyEcs使用CreateEntities并复制本批连续Position列；Friflo使用保留的CreateEntityBatch，逐行填写批次Position后创建；Arch使用Signature批量创建并沿组件块填充共同值。身份输出继续映射到同一Position输入序列，DefaultEcs沿原生CreateEntity及组件Set建立。空Buffer头和后续内容准备分别在自己的创建与容器阶段完成。

缓冲准备及包含Clone/Destroy的一轮操作由调用方持有一次World.OpenStructureWrite。该范围覆盖同批实体的容量准备、元素操作、复制和回收；内部结构操作复用已有结构访问。每个Buffer视图继续按原入口取得和Dispose，写版本、独立复制及实际释放沿各自操作完成。

Clear清有效元素引用并将Count归零，保留容量；TrimExcess使用显式缩容；Dispose/删除组件归还实际数组头。4096 B池保留上限控制归还后的容量，实际存活缓冲按needed增长。

World整列释放或批量Destroy连续尾段的Buffer时，类型绑定和BufferPool定位各发生一次。ComponentType.ReleaseRows接收实际start/count，复用IComponentColumnOperations的span释放入口。池按行清实际元素引用、重置Count并判断4096 B保留上限，统计本段实际归还数量后一次准备空闲数组容量，再按原行序追加16 B数组头；组件范围统一清空。World最终Dispose使用整列清空完成Buffer值释放，缓冲池随World一起结束寿命。公开的单值释放和逐项Remove/Destroy继续使用同一池的单值Return入口。

逐项删除继续沿OwnedValues.Release完成实际回池，再由RemoveRow统一清理末尾Managed格。Buffer释放重置的16 B行头与随后的Clear分别进入零写预算；该简单路径与非尾填洞共用原清理入口。索引、缓存、Sparse、Count及身份代数沿既有顺序完成，释放资格与TransferKinds保持同一布局规则。

Read/Get/Set/Remove按ResourceEntries类型目录定位一次。Remove先把HasValue置为false，再由已绑定的ResourceValue<T>释放旧值并清空Value；已清空的类型存储供同World的后续Set复用。重新新增先取得结构写，再填写Value并发布HasValue；已有值Set沿原Replace完成释放和替换。Has/Read/Get、复制、存档类型选择和检查点访问按HasValue消费实际资源。Clear/Dispose只释放实际存在值，随后清空目录并结束这些类型存储对象的寿命。Release、Replace及同步Remove入口沿闭合类型内联，实际用户Dispose各调用一次。每个实际使用类型的存储对象在结构边界首次建立，World内删除/重建复用该对象；保留容量包含其HasValue、空T存储及实际对象对齐。Job 通过资源类型及读写模式声明访问；保存通过已绑定的实际资源 codec 编码一次。资源自身包含可释放容器时由该类型操作完成释放。

同一批次重复访问固定资源类型时，调用方先通过现有GetResource/GetSingleton/Get或参照侧实际值入口绑定各T的可写ref。混合资源组按16个已知T绑定引用，再沿原4,096次类型选择执行读值、实际Dispose和替换；引用使用块结束后执行原16项Remove/重建。初始化形成每项ResourceData与本批相同Counter的关系，混合替换沿原Dispose更新计数并写Value、Second和Third，复用该Counter引用。五库把绑定引用交给同一个非泛型ReadSetResources内核，复用相同编译代码完成值计算；16次绑定、全部值操作及各自删除重建按所属阶段计费。

## 视图与数据的具体边界

内部Buffer组件只有Core可访问的存储字段，公开只读视图通过ref readonly组件行取得数据，公开可写视图通过ref组件行取得数据。两种视图持有或复用已取得的结构/值访问，Dispose结束自己取得的范围；查询内视图复用查询范围。热方法直接读取行中Count和Items，沿用本页P/A内核。ReadRemovedBuffer借用Removed<Buffer<T>>实际旧值字段，沿相同只读视图取得数组与Count。

ReadOnlyBuffer返回ref readonly元素与ReadOnlySpan；DynamicBuffer返回可写元素及Span并提供结构性元素操作。元素数组增长完成后，后续span重新取得当前数组。行位置由外层结构范围固定，Buffer内部元素范围由该Buffer写访问持有。含可变引用的元素在Job中沿实际元素类型的alias声明选择读取或独立复制。

迁移转交16 B头；删除/Destroyed保留事实由相应值持有者接管并释放；Clone/Capture取得独立数组并写目标头，实体字段Remap遍历目标实际Count项。存档codec写Count和有效元素，读时在目标World池取得头、准备容量、逐项解码并推进Count。显式行内缓冲沿相同两种公开视图，生成器通过闭合类型提供行内与溢出访问。

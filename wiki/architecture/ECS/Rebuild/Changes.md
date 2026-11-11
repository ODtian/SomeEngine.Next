# 变化版本、移除旧值与事实寿命

分析时间：2026-10-22 22:50 +08:00。状态：D算法方案；Stamp 的 S-E 分段计数本轮已复核。

分析方法：分别核对当前行版本、Added、Removed<T> 旧值和销毁观察者的消费者；沿写入、迁移、消费、回收与恢复展开。源码的存在位 Removed 过滤和本项目的保留旧值能力分别配对。

## 数据及语义

追踪由类型声明开启，普通闭合 T 路径直接读写值。Chunk.Changes 按紧密列号保存变化头；每个头为 Rows Array、WordChanges[]、BlockChanges[] 与 LastWrite，32 B。Rows 的实际类型由闭合 T 的追踪声明确定：仅 Written 使用 ulong[]，同时 Added/Written 使用 RowVersions[]。变化头数组仅在本表存在追踪列时分配，大小 32K B；实际行/摘要数组只为开启类型建立。

| 数据 | 字段与大小 | 消费者 |
|---|---|---|
| 行版本 | 仅Written为ulong，8 B/行；Added/Written为RowVersions，16 B/行 | 按声明的实际能力用于精确行过滤和迁移 |
| WordChanges | Latest、OtherLatest、Rows，24 B/64行 | 快速确认本版本写过的行，以及其它行的版本上界 |
| BlockChanges | Latest、OtherLatest、Words，24 B/4096行 | 跳过旧块与旧词 |
| LastWrite | ulong，含于32 B头 | 整列变化跳过 |
| Removed<T> | 移除时的 T 与版本，按 T 对齐 | 旧值查询；同一实体/类型保留最近一次移除 |
| EntityChanges | 销毁身份序列及消费者进度 | Hierarchy/Relations 显式维护 |

写入或取得可写 ref 时更新 Written。新增时同时写 Added/Written。纯迁移与填洞复制原来的两项版本，并提高目标摘要上界；组件值的业务写才产生新的 Written。Added 用 Changed 候选筛选后读取 Added；其新增8 B/行与新增一次版本写属于相对 S-E Changed 的明确功能增量。

World.Set<T>(ReadOnlySpan<Entity>,in T)为同一批次取得一个写版本。已有值写Written，新增值写Added/Written；同一批同时包含两种情况时共用该版本。内部已解析行的新增入口接收已有批次版本，单项入口按各自调用取得版本。

批量Create的列输入也沿同一完成链发布追踪状态：实际值先Copy/Fill到最终列，CompleteCreatedRows调用StampCreated，为该实际行段取得一个版本并逐行写Added/Written及摘要。调用方按最终布局组织输入span，并保留输出身份与原输入序号的对应关系；列追踪复用批次范围。

## Stamp 的逐步内核

输入已取得 Chunk、紧密列号和 row，T 开启追踪；实际值写由调用者计。D 在 Changes 数组取 ref 一次，Rows 和 Words 各绑定一次；生成器按该 T 的8 B/16 B行形状生成相应的元素访问。下列伪代码展示包含 Added 的形状，Written-only 路径直接写 ulong 元素。

```csharp
// D 设计伪代码。v 为本次已取得的写版本。
ref var changes = ref chunk.Changes[column]; // P1/A1
var rows = (RowVersions[])changes.Rows;      // P1，生成器选择实际行形状
rows[row].Written = v;                       // A1/W1
ref var word = ref changes.Words[row >> 6];   // P1/A1
if (word.Latest == v) {                      // B1
    word.Rows |= 1UL << row;                 // W1
} else {
    word.OtherLatest = word.Latest;          // W1
    word.Latest = v;                         // W1
    word.Rows = 1UL << row;                  // W1
    ref var block = ref changes.Blocks[row >> 12]; // P1/A1
    Raise(ref block, 1UL << (row >> 6), v);
    changes.LastWrite = v;                   // W1
}
```

Raise 在版本相等时 OR Words（B1/W1），版本较新时写 OtherLatest/Latest/Words（B2/W3），迁入较旧版本时提高 OtherLatest。普通写满足当前版本为最新的内部不变量；复制入口使用完整 Raise。

## 搬移版本与筛选算法

每个词满足：Latest是行版本上界；Rows中的置位对应当前实际等于Latest的行；OtherLatest是其余行版本上界。删除和覆盖行先清该行的Rows位，再按搬入版本更新。块的Words按词的Latest维护，历史上界可以保留。

连续销毁尾段复用ClearTail(start,end)：行版本数组按实际范围清空，首个部分词保留前缀成员位，后续覆盖词的成员位统一清零。Latest、OtherLatest、块摘要与LastWrite继续持有历史上界，当前有效行数由Chunk.Count限定。单行删除沿ClearRow使用相同的行版本与成员位规则。

```text
CopyVersion(destinationRow, sourceAdded, sourceWritten):
    destination.Added = sourceAdded
    destination.Written = sourceWritten
    bit = 1UL << (destinationRow & 63)
    word.Rows &= ~bit
    if sourceWritten > word.Latest:
        word.OtherLatest = word.Latest
        word.Latest = sourceWritten
        word.Rows = bit
    else if sourceWritten == word.Latest:
        word.Rows |= bit
    else:
        word.OtherLatest = Max(word.OtherLatest, sourceWritten)
    RaiseBlock(wordIndex, word.Latest)
    column.LastWrite = Max(column.LastWrite, sourceWritten)
```

FillHole对洞位调用该过程，尾行退出时清尾位；跨Chunk迁移在目标更新后清源位。以上保存源版本；新建/Clone的目标实体按创建语义使用本次Added/Written。行拷贝的额外Rows清位W1、版本大小比较和实际Raise均加入搬移账，基础普通Stamp表继续对应业务写入。

Changed先比较列LastWrite，再比较Block.Latest、Word.Latest。Latest≤last时跳过；OtherLatest≤last时使用相应Words/Rows掩码；其它情况向下一层检查，行层逐项判断Written>last。最后与有效行及启用/查询条件相交。Added在候选行上再判断Added>last。这样覆盖旧行后，掩码和当前行版本保持同一含义。

Written-only使用8 B行版本、候选为完整64行词且Vector256硬件加速可用时，行层沿16组四个ulong进行无符号Written>last比较，以ExtractMostSignificantBits取得每组四位并按原行号合成64位结果。摘要捷径仍先执行；尾词、Added/双版本行及稀疏候选沿原逐位路径。每词读取同一512 B版本，随后参与相同Presence、Enabled及其他条件合成；字段、缓存、数组、容量、分配及版本写入增量均为0。

| 相同普通写入状态 | S-E 〈P,A,B,W,L〉 | D 〈P,A,B,W,L〉 |
|---|---|---|
| 同词已经处于本版本 | 〈5,5,2,2,0〉 | 〈3,3,1,2,0〉 |
| 新词，块已经处于本版本 | 〈10,10,4,6,0〉 | 〈4,4,2,6,0〉 |
| 新词、新块 | 〈10,10,5,8,0〉 | 〈4,4,3,8,0〉 |

S-E 同词为 TicksColumn〈1,1,1,0,0〉、Words 二级定位〈2,2,1,1,0〉、Data/行写〈2,2,0,1,0〉。新词路径重新进入 NoteWord，再定位 Words；Blocks 为二级定位；LastWrite 为数组项。D 复用 changes/word ref。源码：[Ticks/NoteWord](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/Archetype.cs#L477)、[BlockTicks.Raise](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/Archetype.cs#L1182)。

随机 Get 在已有值定位后直接调用上表。查询按范围绑定 changes/rows/words/blocks，随后逐行只写 Written，并按实际 U 个词/V 个块处理摘要。连续 n 行的 Written 数据写为8n B；完整行版本分配/复制为 v_T·n B，Written-only 的 v_T=8，含Added的 v_T=16。摘要数组为24U/24V B。Buffer 开启追踪时在写访问入口登记一次，同一次 span 使用中的元素操作复用该登记。

## Removed<T>：保存旧值并合并重复移除

现有行为要求 Remove 后能够读取旧 T，同一实体在 ClearRemoved 前的再次移除覆盖同一份事实；对应 [ChangeTrackingTests](../../../../tests/SomeEngine.ECS.Tests/ChangeTrackingTests.cs) 的 Remove_WritesRemovedFact、RemoveReaddRemoveBeforeClear_RefreshesOneRemovedFact 与 ClearRemoved_ReleasesFactWithoutDestroyingLiveEntity。

Removed<T>采用普通事实列。开启移除旧值的类型在Remove时把“去T、建立Removed<T>”合成一次最终目标迁移；旧值在源行释放前取得。同一事实已存在时覆盖值/版本。Buffer内容转交事实组件，随后由ClearRemoved或事实替换释放。

| 路径 | D 实际工作 | 等价 EasyEcs 能力 |
|---|---|---|
| 首次移除并保留旧值 | 已有 T 定位；一次最终边/迁移；写T旧值与版本；建立事实列 | E* 同样保留旧值组件并调用原生存储入口 |
| 同实体再次移除 | 覆盖事实旧值/版本；释放被替换的实际容器；T 的实际移除 | E* 同样一次覆盖与实际移除 |
| 查询 Removed<T> | 按事实列和Version过滤，返回旧值 | E* 相同事实查询 |
| ClearRemoved(through) | 扫实际事实，对Version≤through的项释放值并移除事实列，组合一次MoveKnown；保留清理实体满足释放条件时回收身份 | E* 相同范围与回收 |
| 查询当前活动成员的补集 | 列布局与实际启用位组成Without<T> | S-E存在位过滤；带移除版本与旧值的观察采用上一行事实能力 |

Removed<T>使用已有表、查询和迁移内核；事实写sizeof(T)+8 B，按T实际对齐。ClearRemoved后Has<Removed<T>>为false；相同源/目标表的连续清理段复用边和容量。事实组件由Core写入与回收，Buffer/托管释放按实际内容入账。Enable/Disable保留T并登记Written，Added与Removed事实对应实际列生命周期。

事实自身由显式Version表达时间，类型配置的RemovedValue为关闭；ClearRemoved执行事实值释放与布局移除。关系维护等实际消费者先读取事实，再进入统一清理阶段。

原有清理组件所表达的“销毁后保留旧值直到消费”作为显式保留能力继续存在：Destroy 将实体迁到清理/Removed/Destroyed 组成的最终表，普通组件释放，销毁事实发布一次；清理结束后释放 ID 并推进代数。默认 X 比较选择普通立即释放能力；保留路径按最终迁移、事实值与清理工作另计。

## 销毁序列：追加、读取、推进与回收

EntityChanges 存储持有 Entity[] Items、Start、Count、FirstSequence、ReaderProgress[] 与有效 reader 目录。每个 reader 只持 ID；Read 返回尚未消费的连续区间，Consume 把进度写入存储。注册点是当前尾序号，服务首次注册时在同一结构边界扫描既有主值并建索引。

跨World及参与者集合变化的消费进度沿[[Serialization]]的具名DestroyedReader契约，以应用提供的Guid关联位置；运行时紧密目录继续服务读取、推进和注销。内部层级/关系消费者由自身SavedData持有恢复进度，普通Clear沿Core统一Reset。

容量足追加沿用 AppendFact=〈1,1,1,2,0〉；读 f 项绑定一次 Items，A f、循环 B(f+1)。Consume 只写该 reader 的进度一次；统一回收点扫描 q 个实际消费者，A q、B(q+1)、q 次最小值比较，更新 FirstSequence/Start/Count 三项。最后一个 reader 注销时清有效区间并复用容量。

每次 Consume 都搬移后缀会反复复制同一批剩余事实。D 将回收合并到维护批次末尾：只推进 Start；追加空间耗尽时，把仍需保留的 u 项一次搬到起点，Copy_Entity(u)、Start W1，空间仍不足时 Grow。连续读取每次获得一个 span；span 使用期属于读取范围。reader 注册/注销各维护一次紧密进度目录，移位时修对应 reader ID 的目录位置。

| 生命周期事件 | 存储动作 | Relationships 动作 |
|---|---|---|
| 首个消费者注册 | 进度目录/日志按需分配；登记当前尾序号 | 扫当前 Parent/endpoints，建立 Applied/邻接 |
| 普通/延迟销毁 | 追加原完整Entity身份 | 普通销毁已完成局部清理，reader确认进度；延迟销毁由Maintain清理 |
| Maintain 成功 | 写本 reader 的消费进度 | 主值与 Applied 比较、实际摘挂完成 |
| 多消费者回收 | 取各消费者最小进度 | 各服务继续从自己的进度读 |
| 消费者注销 | 移除进度、统一回收 | 释放该服务的派生数组 |
| Clear/Restore | 按对应恢复模式重置或恢复序列与进度 | 在同一完成点重建或恢复 Applied |

实体 ID 提前复用仍按 [[Relations]] 的完整身份/旧代集合处理。追加事实记录销毁前身份，代数递增与空闲链发布发生在该身份释放时。

## 系统观察与版本推进

World在同一次数据代数内持有单调ulong版本；Restore推进数据代数，并按所选模式同步恢复版本与观察进度。SystemState 持有上次成功观察基线 LastSystemVersion 与本次观察上界 CurrentSystemVersion；上界在依赖完成、系统开始执行时确定。查询观察区间为 last < version，实际任务依赖保证读取的值可见。系统执行、其需要等待的工作、命令回放和规定的维护阶段成功后，把本次观察上界写回 LastSystemVersion；失败保留原基线。

写版本与观察基线分别使用：公开写操作或写批次在实际开始写入时取得一个新版本，范围内全部变化复用；读取保持当前版本。并行查询在有实际匹配范围且包含追踪写入时，取得一个共享写版本，传给全部范围。层级传播在首次实际 Write 时取得共享版本。写版本晚于本次观察上界时，下一轮允许再次观察该次写入。

范围按完整64行词划分；同一追踪列的各任务持有不同词。任务先更新自己的 row/word；完成后按实际触及块合并摘要与 LastWrite。这样共享块摘要由完成阶段写入一次，费用由 [[Jobs]] 的摘要合并项承担。

## 首次、增长与恢复

每个追踪列首次分配 Rows[c]、WordChanges[ceil(c/64)]、BlockChanges[ceil(c/4096)]，L3，并初始化实际容量；Rows按v_T=8或16 B计。基础变化头数组首次 L1/Zero(32K)。增长分别 Copy 有效行/词/块，再发布各引用；Added/Written 随行共同移动。

普通读写的追踪调用和追踪分配为0。开启 Added 的 E* 适配增加同样的 Added 行版本；保留旧值的 E* 增加同样的 Removed<T>。普通 Changed 内核使用本页 S-E 原生对照。

检查点保留模式复制行版本、事实与消费者进度；重新开始模式先完成 Maintain，恢复后用同一新基线初始化所有消费者和实际保留事实。具体保存模式及完整成本由 [[Serialization]] 持有。

## 保留清理实体的完成点

Destroy遇到Cleanup或保留事实时，将实体放入含Destroyed标记的最终布局，普通主值按声明释放，原身份与代数继续有效。IsAlive返回该身份仍存在；IsPendingCleanup由当前Archetype的Destroyed标记确定。表的清理列计数在类型组合构造时确定。

Remove/ClearRemoved完成最后一份实际Cleanup或保留事实后，执行最终行释放、代数递增与空闲链发布。销毁事实在第一次Destroy时发布一次，最终身份回收复用这一生命周期记录。相关普通查询与保留事实均从真实组件列读取。此路径对应RootOnlyEnableableAndCleanupComponents_RetainTheirSpecialSemantics。

关系端点及Parent的新输入使用“存在且处于活动生命周期”的条件；已进入保留清理的实体继续供旧值读取与清理消费。占用索引查询和普通销毁清理沿同一活动身份规则，避免将保留清理期间的存储身份作为新的关系占用。

保留/释放规则同时用于Core列与Sparse实际附着项。第一次Destroy释放普通项，保留声明为Cleanup的项及所需事实；保持Destroyed布局。最后一次清理检查当前表的实际保留列数与剩余Sparse附着链，二者完成后回收身份。清理中的普通新写入沿活动生命周期入口处理，保留数据继续通过其读取/清理入口访问。

# 值索引、排序和分组：算法与完整成本

分析时间：2026-10-22 22:50 +08:00。状态：D 选定组号反向定位；S-E 为固定源码。Set 与固定成员视图沿用已确认规则。

分析方法：固定任意 GetKey、同步 Set、紧密成员与有序范围能力，展开旧组定位、新键查找、成员搬移、首次、增长和释放。计数口径见 [[Cost-Model]]。

## 数据与选型

T 主值在 Core 列。ValueIndex<T,TKey> 持有 Buckets、KeyGroup[]、Membership[] 和按需建立的 OrderedGroups。每个不同 key 在一个 KeyGroup 中保存；每实体只保存组号和组内位置。组号在数组增长中保持，最后成员离开后回收。

| D 数据 | 字段 | 消费者与宽度 |
|---|---|---|
| Membership | GroupPlusOne int、Place int | 旧组定位/紧密删除；8 B每实体，0组号表示当前未入索引 |
| KeyGroup | Items引用、Key、Hash int、Next int、Count int | hash查找、连续成员、旧键相等比较；20 B固定字段加Key及实际对齐 |
| Buckets | 首组编号+1 | int[]；按hash定位，Next为同桶下一组+1 |
| OrderedGroups | 按Key排序的组号 | int[]；首次有序查询建立 |
| Used、FreeHead、GroupCount | 三个int | 已用组范围、空闲组链、实际key数 |

空闲组的Next复用为空闲链；组内Key/Items引用在回收时清空。Membership跟随实体空间增长，Destroy/Disable摘除后清组号；当前身份复用沿Core生命周期处理。Find与有序视图持有读取范围，组号在这个范围内有效。

| 旧键方案 | Set的GetKey次数 | 旧组取得 | 每实体状态 | 每不同key状态 |
|---|---:|---|---|---|
| 写前提取旧主值 | 2 | 计算旧key再hash查找 | Position 4 B | key及成员头 |
| 每实体复制旧key | 1 | 读取实体节点，再hash旧key | align(sizeof(Key)+5) B | 字典key及成员头 |
| 组号反向定位（选定） | 1 | Membership→KeyGroup，数组各A1/P1 | Membership 8 B | 一份key及hash链接/成员头 |

组号方案比4 B位置增加4I B，换得任意投影的一次GetKey和旧组直接定位。相对于按实体缓存key，key随不同值数量持有。选择覆盖同一任意值投影能力，投影、Hash和Eq的调用数分别入账。

## 查找与成员内核

采用单数组分离链接hash表。组号是数组下标，扩容保持下标；增长Buckets时按已经保存的Hash重连Next。静态EqualityComparer<TKey>.Default按闭合类型调用。下列命中链访问c组，其中e组hash相等，实际调用e次Eq。

```csharp
// D设计伪代码：buckets/groups已经绑定，hash由调用方计算一次。
int group = buckets[Bucket(hash)] - 1;
while (group >= 0) {
    ref var item = ref groups[group];
    if (item.Hash == hash && Equal(item.Key, key))
        return group;
    group = item.Next - 1;
}
return -1;
```

绑定两数组P2；命中为 **Khit(c,e)=〈2,1+c,2c+e,0,0〉+Hash一次+Eq e次**；未命中循环多一次终止判断。调用者已绑定groups时扣P1。哈希桶从首次建立起使用实际正容量，桶取模为整数算术。

| 已知group/member ref、容量足 | 〈P,A,B,W,L〉 | 实际修改 |
|---|---|---|
| Append，Membership目录未绑定 | 〈2,2,1,3,0〉 | Items/成员反查基址，写Entity、Membership整项8 B、Count |
| Drop非尾项，保留当前Membership供后续改组 | 〈2,3,1,3,0〉 | 末项移入洞、修移动实体Place、Count |
| Drop尾项，保留当前Membership | 〈0,0,1,1,0〉 | Count |
| 真实退出索引 | 上述Drop + 当前Membership清零W1 | 当前ref由入口定位 |
| 枚举N成员 | 〈1,N,N+1,0,0〉 | 只读Items有效前缀 |

Drop的P2为Items与Membership目录；调用方已绑定目录时扣P1。POD Entity数组尾部由Count表达有效范围。成员增长为Grow(Entity,c,c',N)，读写各8N B。

S-E依据：[ValueIndex Node/Put/Link/Unlink](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/ValueIndex.cs#L433)、[留洞IndexHead](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/ValueIndex.cs#L35)。S-E保存每实体Key/Place/Linked；D保存每实体GroupPlusOne/Place，成员都按同样固定读取范围比较。

## Set的完整顺序

1. 一次取得主值行；索引已经建立且该行启用时，计算新key一次，从Membership取得旧组。
2. 新key与旧组Key相等时，写T并登记Written，成员保持原位置。
3. key改变时，按新key查目标组。准备组/桶/成员/有序数组的实际容量；首次新组所需用户Hash/Eq/Compare也在提交前完成。
4. 取得扩容后的实际数组/ref；旧成员紧密删除，必要时回收空组；写T，追加目标成员并整项覆盖当前Membership，登记Written。
5. 已禁用行写T并登记Written；Enable时计算其当前key并加入，Disable/Remove/Destroy直接通过Membership摘除。

同一个Set持有结构写范围，所有准备与主值/成员更新共同完成。容量增长后按组号重新取ref；一行最终Membership只写一次。T的释放和Removed旧值处理由 [[Execution]] 组合。

| 等价入口，普通已启用T | D完整预算 | S-E完整预算/调用数 |
|---|---|---|
| Set，同key | E+S+索引〈2,2,2,0,0〉=**〈7,5,8,3,0〉**；GetKey1、Eq1 | 原生Set〈12,6,10,3,0〉+Put〈2,1,4,0,0〉=〈14,7,14,3,0〉；GetKey1、Eq1 |
| Set，两个key组已存在、旧组中间成员 | E+S+索引=**〈10,10+c,11+2c+e,9,0〉**；GetKey1、Hash1、Eq(e+1) | 原生Set+Put/Unlink/Link；GetKey1、旧/新各一次hash查找及原生Node/留洞维护 |
| Disable/Remove/Destroy，旧组仍有成员 | 核心入口+built B1+Membership/旧组〈2,2,1,0,0〉+Drop+当前Membership清零W1 | 核心入口+原生Node范围/Linked读取+旧key字典查找+Head.Remove+Node清理 |
| Find/Count，已有key | 类型绑定T+built B1+Khit+命中判断B1；Count读group.Count | 原生Index绑定+built+Dictionary Hhit+命中判断；Count读Head.Count |
| Find后的遍历 | 上行+Items P1+A N/B(N+1) | 上行+Ids P1+A u/B(u+1)+u次占用判断，u为已用位置 |

changed Set的索引部分为〈5,7+c,5+2c+e,6,0〉：旧Membership/组定位P2/A2；新桶查找P1/A(1+c)；两端Items P2；旧末项/洞/移动反查与新项A4；built/相等/容量/尾项/旧组空五项判断；六写为旧洞、移动Place、旧Count、新项、新Count、当前Membership。S负责主值和G，E负责公开执行连接。首次/空组另按下一节追加。

Set的查找循环在命中处直接进入已有组转移并返回；循环结束后进入新组准备，因此命中路径沿Khit的分支计数。新key且旧组仅有当前实体时，复用该组号和成员数组：按旧Hash摘桶链接，写新Key/Hash/Next并挂新桶，更新实际有序位置；Membership与Count保持。这条冷分支计旧桶扫描/重连W1、新Key/Hash/Next/桶四写及实际有序搬移，新旧有序位置在准备阶段取得。

GetKey是确定的值投影，计算次数跟实际写入入口一致。启用位与Added/Written使用各自专题的实际操作。索引尚未建立时只有built分支；默认关闭的类型按闭合类型归约。

## 新组、空组、首次和增长

创建路径在主值写入后绑定已激活的索引。普通值直接把创建入口持有的输入交给IndexCreated；带值克隆操作或Shared编号的类型，从实际主列取得最终值。绑定成功后调用Added并计算实际key一次。索引首次建立仍沿活动主值扫描，因此创建入口与首次索引扫描各自沿实际生效点处理数据。

首次Index按Chunk绑定实际ColumnEntry及值Span，普通完整列直接把同行Entity和值交给Added。具有存在位或启用位的列先应用相应行选择；Shared和Tag沿实际值读取入口取得投影。扫描期间复用已经取得的主列与行，随后由相同Added内核计算key并维护成员目录。

单实体Clone、集合复制完成、恢复重建及Shared/Tag首次扫描已经持有实体、列项与行号时，沿既有内部AddFromEntity(Entity,in ColumnEntry,row)直接取得该列的只读值。Shared复用ReadSharedColumn，Tag消费其逻辑值，普通列复用ColumnEntry.At；随后交同一Put完成结构权限、身份、启用、一次GetKey及成员维护。调用方沿原结构写范围传递已有位置，省去重复World.Read的完整定位；字段、缓存、容量、分配和业务复制量增量均为0。

| 事件 | D算法与费用 | S-E同能力费用 |
|---|---|---|
| 首次Index | Index对象、Membership[I]、Buckets、KeyGroup[]；扫描N个活动值，每项GetKey1+Khit/miss+Append | Index/SpareIds/池目录/Dictionary/Node[]；扫描N值逐Put/Link |
| 新key组，容量足 | miss查找；取FreeHead或Used，写组整项、free/used、桶、GroupCount四项；实际Append | Hget-new、Head建立、Node写入 |
| 最后成员离开 | 从保存Hash的桶查到该组号，改桶或前驱Next；Return成员数组；清组并接free链；FreeHead/GroupCount更新 | Head归还、Hremove、Node清理 |
| Membership增长 | Grow(Membership,I,I',I)，8I有效字节 | Grow(Node,I,I',I)，按实际Key/Place/Linked宽度 |
| KeyGroup增长 | Grow(KeyGroup,g,g',Used)，组号保持 | Dictionary entries复制，key和24 B头随entry复制 |
| 桶增长 | 新int[]清零；扫描Used项，Count>0时按保存Hash写Next/桶各一次 | Dictionary同样按保存hash重新链接 |
| 成员增长 | Copy_Entity(N)后发布Items；池目录/Return按Cost-Model | Copy_Entity(u)，u含留洞使用区间 |
| Clear/恢复 | 清实际Membership、归还各Items、清KeyGroup/Buckets；首次重建扫活动主值 | 清Node/Head与Dictionary，首次Build扫主值 |

空组摘链直接比较组号，Hash/Eq调用为0；实际访问x个链项记A x、相应循环/组号条件，前驱另A1，链接写W1。组清理按实际Key引用执行写屏障。首次新组与回收均在有序视图开启时加入下面的顺序维护。反向目录、hash表、成员池各自是自己的唯一数据持有者。

## 有序key

OrderedGroups首次由有效组号构成，按(CompareKey(groups[id].Key),GroupId)排序；CompareKey为0时使用GroupId递增次序。GroupCount同时是有效顺序长度；读取范围固定数组和组内容。选择连续int目录，范围遍历直接取得组，按实际Count读成员。

| 同能力候选 | b步查范围起点 | 插入/删除位置j | 遍历q个key组 | 保留数据 |
|---|---|---|---|---|
| 排序Key数组+hash成员头 | Key数组P1/A b，Compare b | Copy_Key(g−j)，再维护长度 | 每key一次Khit/Hhit | 一份排序key副本 |
| 排序组号数组（选定） | 顺序/组数组P2/A2b，Compare b | Copy_int(g−j)，组号和GroupCount实际写入 | 每key两次A，直接得到成员头 | 4g B组号 |
| 平衡树 | Nodes P1，实际b步链接/Key读取 | 实际路径和旋转链接更新 | 按实际树链接访问q项 | key或组号及树链接 |

首次排序扫描Used个组，按Count选择g个有效编号，写g项，随后Sort(g)。新组先二分取得插入位置，再搬移实际后缀；空组回收前按(旧Key,GroupId)二分取得唯一位置并搬移后缀。组号回收发生在顺序项移除之后。Compare次数与搬移d项分别计入；有序视图寿命由读取范围固定。

这项选择增加二分时每步一次组定位，降低大key复制字节并省去范围遍历的逐key hash。树适合以修改频率换取链接维护的配置；本设计选连续范围作为默认完整能力。

## GroupBy与接口

GroupBy的主值沿类型本身的列存储，Shared类型通过行编号读取共享主值。入口采用声明的整数组键；读取新组键一次，表内当前组键从组目录P1/A1取得，相等判断B1。改组时源表group→目标表缓存取得目标，Dictionary P1+Hget-hit/new，再调用MoveKnown(k)，沿用Set已取得的记录/结构范围/目标。每实际新表和查询首次可见计入 [[Construction]]。

分组初始化及一轮逐项修改由调用方持有一次World.OpenStructureWrite，范围内的Set复用已有结构访问。身份解析、组键投影、同组写入和实际改组仍按每个输入项完成；随后各组查询沿原有范围执行。批次范围只集中结构访问的取得与释放，主值、投影次数和查询成员沿同一输入序列确定。

一批跨组修改沿[[Bulk-and-Copy]]的Set实体span/值span入口执行。调用方保存本批身份顺序并填写对应新值，入口按实际目标组累计容量需求，统一Reserve后按输入顺序迁移和写入。每个输入的组键投影仍执行一次；同组写入继续沿其直接入口完成。身份和值输入数组由调用方复用，分配计入首次与完整周期。

同一类型组合的GroupTables持有按类型序排列的实际Groupings。单组键使用Dictionary<int,Archetype>；多组键使用GroupKeys{int[] Values,int Hash}和ReadOnlySpan<int>的替代查找，逐项比较完整键序列，首次新组合才复制键数组并建表。查找、迁移边和存档表描述使用相同的组键次序。Shared行编号与GroupBy整数组键各自沿本类型主值和已激活分组声明建立。

逐项值的批量Set复用该目标绑定：ComponentGrouping<T>先校验每个输入身份、计算该值的GetGroup一次，并沿GroupTarget取得最终Archetype；连续输入来自同一源Archetype且目标目录只有一个活跃分组类型时，批次局部复用该GroupTables引用，按本项实际key取得目标；多分组组合沿GroupTarget读取所需组键。暂存每项目标引用，以World内Archetype.Id索引int计数，并用紧密表列表记录实际目标。各目标按现有有效行数加到达数量Reserve一次，然后按原输入顺序取得已校验记录、沿MoveToGroup移动实际共有列，并调用SetGroupedMainValue写主值、Shared成员和变化版本。目标引用数组保留8M B，计数目录保留4A B，A为覆盖的表编号容量；紧密目标列表和数组在该分组声明内复用。每批开始按上一批实际目标清计数。活跃值索引的修改继续由索引Set入口维护主值与分组。

首次GroupBy扫描N行，实际m个改组各执行目标定位和MoveKnown；连续同组段Reserve一次并复用列基址。源码：[World.Groups](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Groups.cs#L23)。

Index、Find、Count、OrderedKeys/Range返回读取范围内的结果。值写通过Set进入上述一步维护链，普通非索引值继续提供可写ref。索引恢复从主值和启用状态重建；组号属于当前索引存储的内部编号。


## 有序组的定位与提交

键相等由索引EqualityComparer决定，范围顺序由排序Comparer决定；两个不同组的key比较为0时，组号确定其内部次序。Range的LowerBound/UpperBound只比较key，从而覆盖所有排序相等的组；单组插入/删除使用(key,GroupId)比较。二分每步仍为顺序项与组项A2，Compare为0时追加一次组号比较。

改key前取得旧组的唯一位置与新组插入位置，全部用户GetKey/Hash/Eq/Compare在成员及主值提交之前完成。旧组删除后，新位置减去旧位置在其前面的那一项；已有目标组沿其已知组号追加。单成员组复用时使用同一调整规则，然后写新Key/Hash/链接和顺序项。提交阶段执行已确定位置的数组搬移与字段写。

GetKey、Hash、Equality与Comparer按注册的同一规则工作，组内key在存续期保持该规则下的值；key变化通过Set提交。KeyGroup的组号与排序内部次序由该索引持有，公开结果由key及实际成员表达。

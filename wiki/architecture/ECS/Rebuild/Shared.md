# Shared：唯一值、行编号与成员查询

本页持有Shared的目标存储与完整操作。类型集合由Archetype表达，共享值由World内每个SharedValues<T>唯一持有；各实体行保存该类型的int编号。基本列定位、存在位与迁移沿[[Storage-and-Queries]]，完成范围沿[[Execution]]。

## 数据归属与布局

| 数据 | 主要持有者 | 消费者与维护 |
|---|---|---|
| 唯一T值与Count | SharedValues<T>的T[] Values | Intern、Read、Clone与存档；编号按World寿命保留 |
| 按值查找 | Buckets int[]、Links ValueLink[] | Intern查找与新增；ValueLink为Hash/NextPlusOne，共8 B |
| 每行共享编号 | Chunk中该类型的int[] | 实体读取、更改、迁移、Clone与恢复；ColumnPlusOne/Presence表达成员资格 |
| 每编号的活动成员 | SharedGroups的SharedGroup[] | FindShared、简单共享Count及启用/移除维护 |
| 成员反向位置 | SharedGroups的int[] Places | 按Entity.Id交换删除并修正被移动成员 |

SharedGroup为内联两个Entity、可选Entity[]和Count，x64为32 B。1、2个成员直接使用内联项；第三个成员到达时租用数组并复制原两项，后续按实际容量倍增，空组归还成员数组。Places按实体记录容量建立，4I B；行编号4c B；组目录32g B；成员数组按实际租用容量计。

相同类型集合的不同共享值共用Archetype。共享编号属于各行主数据，按共享值的成员目录属于SharedGroups；GroupBy声明沿[[Indexes]]自己的布局与排序规则处理。每实体独立共享值的输入建立K个唯一值与K个小组，元数据按K和实际成员数增长。

## 唯一值归并

Values、Links、Buckets从16项起步，首次登记default(T)为编号0。编号由同一World的SharedValues<T>发出。Values是唯一主值数组；专用桶/链接保存编号和已计算的hash，比较使用该类型的EqualityComparer<T>。

Intern依次计算一次输入hash、取得桶、沿NextPlusOne访问候选；hash相同的项再比较Values中的实际T。命中返回已有编号。新增时先准备Values/Links与桶容量，再写主值、链接、桶头和Count。值数组与链接数组倍增；桶负载达到3/4时建立两倍桶，沿已存hash重接各链接。桶增长复用已计算的hash，完整恢复从已读回主值重建hash与桶。

| 已绑定类型存储的操作 | 主值与查找工作 | 结果与分配 |
|---|---|---|
| Intern已有值 | 一次hash、实际c项链接、e次T相等比较 | 返回已有编号；容量足时L0 |
| Intern新值 | 相同查找前缀，准备实际需要的数组容量 | 写Values/Link/桶头及Count；增长按实际数组发生 |
| 按编号Read | Values[id] | readonly ref；有效期沿当前结构/访问范围 |
| 最后一个成员离开 | 清该编号的成员组 | 唯一值与编号继续按World寿命保存 |
| 恢复查找索引 | 已读回K个值，各计算一次hash并挂桶 | Buckets/Links按实际容量建立 |

专用桶/链接使查找、连续复制和存档共同读取同一Values。原源码中的Generation/List/Dictionary组织见[SharedComponentStore](https://github.com/ODtian/SomeEngine.Next/blob/80f446207d2968cf0c0a0636b0c1429e44f8fe37/src/SomeEngine.ECS/SharedComponentStore.cs)。参照完整Shared的主值、行编号和成员适配由[[Adaptation]]持有。

## 公开入口与完整链路

```csharp
SharedId<T> InternShared<T>(in T value) where T : struct, ISharedComponent;
ref readonly T ReadShared<T>(Entity entity) where T : struct, ISharedComponent;
ref readonly T ReadShared<T>(SharedId<T> id) where T : struct, ISharedComponent;
void SetShared<T>(Entity entity, in T value) where T : struct, ISharedComponent;
void SetShared<T>(Entity entity, SharedId<T> id) where T : struct, ISharedComponent;
void RemoveShared<T>(Entity entity) where T : struct, ISharedComponent;
SharedMembers FindShared<T>(SharedId<T> id) where T : struct, ISharedComponent;
```

按值Set先取得实体记录及实际列；已有值与输入相等时完成实际Written登记。实际值改变时Intern输入，再进入按编号写入。按编号Set复用该类型存储及已定位record：相同编号完成实际Written登记，实际编号改变按以下顺序处理。

1. 首次附加该类型时，按普通类型边迁移到包含此Shared列的最终表，并从最新record取得目标行。
2. 读取原编号与当前启用/存在状态，预留目标成员组容量。
3. 活动成员从旧组交换摘除，修正被移动成员的Places，再追加到新组并写本实体Places。
4. 将新编号写入该行int列，恢复实际需要的存在位，登记Added/Written。

已有Shared列更改编号直接维护行与成员组。索引/分组声明接入其已有Set路径，复用本次取得的主值与编号；key计算、布局整理与版本由[[Indexes]]持有。

泛型Create先沿完整TypeSet取得目标表，并由CreateKnown建立实体行和同一次创建的版本。闭合T的WriteCreatedValue对Shared取得唯一值编号，直接写该行int列，并以old=-1把新实体加入活动成员组；普通列沿原有值复制/克隆写入。全部初始值就绪后执行已声明的索引、GroupBy整理和组件初始化。单值及多值Create共用此路径；批量Create遇到Shared时调用对应泛型Create，各实体的唯一值归并和成员追加仍按实际调用计费。

按实体Read沿record→Chunk列→行编号→Values[id]返回只读主值。按编号Read直接读取Values。OpenRead<T>对Shared在范围开始绑定Values，随后按每行编号读取；查询范围的ReadShared<T>返回绑定Values与编号span的SharedReadOnlySpan<T>。每行持有自己的共享编号，调用方按行访问对应唯一值。

FindShared绑定该编号的活动成员span并持有结构读取至Dispose，结果中的Entity使用完整身份。仅筛选一个共享值的Count使用组Count；组合其他条件的Query先匹配类型集合，再按行编号、存在/启用位及实际变化条件过滤。查询构造和过滤由[[Storage-and-Queries]]持有。

## 迁移、启用与生命周期

| 事件 | 行主数据 | 成员目录与唯一值 |
|---|---|---|
| 普通布局迁移 | 复制已有Shared编号及存在/启用状态，修正EntityRecord | 同一Entity与编号继续复用Places，实际类型增删分别处理 |
| Disable/Enable | 保留行编号和值，改变启用位 | 从活动组摘除或加入相同编号组 |
| RemoveShared | 清存在性，或合并到本次最终布局 | 摘除活动成员，唯一值继续保留 |
| Clone同World | 复制编号与状态 | 为实际活动目标追加成员 |
| Clone跨World | 将实际共享值归并到目标World，写目标编号 | 目标存储建立自己的组与Places |
| Destroy | 按实际存在/启用状态读取行编号并释放行 | 摘除活动成员，修正交换项位置 |
| Clear | 清实体行与成员状态 | 归还成员数组、清组与Places，复用已归并值和编号 |
| Dispose | 结束行与访问寿命 | 释放唯一值实际拥有的内容及存储 |

Shared声明的相等/只读规则适用于World内唯一主值。拥有内容的类型沿已注册值操作完成World复制和最终释放；Buffer等独立容器的逐行寿命由[[Containers]]持有。

## 首次、增长与恢复费用

K为实际唯一值数，I为实体目录容量，N为活动成员数。首次类型建立SharedValues与SharedGroups对象，Values/Links/Buckets、组目录和Places分别分配；默认编号0使用同一Intern新增内核。首次Shared列由普通建表/附列路径建立int数组与ColumnEntry元数据，各份容量与清零进入首次阶段。

Values/Links增长复制有效K项，桶增长读取已有hash并重挂K项，组目录增长复制32 B组项，实体目录增长复制4I B Places。成员组第三项和满容量时租用实际成员数组，复制有效项并归还旧数组。交换删除修正一个被移动成员，清空组归还其数组。小组内联成员与大组数组容量分别进入保留内存。

Capture保存唯一主值及编号、每行编号和实际存在/启用状态。Restore先读回Values并重建桶/链接，随后从最终实体行重建活动组与Places；按编号保留模式沿已有编号恢复，跨World归并沿目标编号映射处理。独立值复制、行复制、引用修正与所选进度模式接入[[Serialization]]。

ReadWorld的字段解码/实体修正先于唯一值归并。按文件编号逐项解码一个T，修正引用后沿同一Intern取得目标编号，保存该文件编号的对应关系；新值与重复值沿原值持有/释放规则交接。目标值/链接按文件给出的K项预留，实际Count为归并后项数。编号一致时复用按编号读取；首次出现编号变化时建立4K B映射数组并写入已有等值前缀，此后继续记录实际目标编号，分配/初始化按实际容量计入。

4K映射只转换实际Shared列编号。GroupBy表键由实际值的GetGroup()形成，沿[[Indexes]]的独立分组规则处理：字段修正后逐行取得各分组值的目标键，完整键相同的行进入同一目标表。多个源表归并到同一目标类型/组键时按源顺序追加Chunk/行段；一个源表产生多种目标键时复用首次GroupBy的扫描、Reserve和Move内核重组行。恢复移动保留读入的版本、存在位、启用位及值持有关系。实际GetGroup调用、迁移、旧值复制、容量增长及重绑计入ReadWorld；最终活动成员/Places从全部目标行建立一次。

完整费用为实际Intern查找/增长、首次类型附加、逐行编号访问、更改时的活动成员维护、查询、复制/恢复与最终释放之和。16组与每实体独立值分别按[[Acceptance]]执行相同输入、完成点及四个阶段。

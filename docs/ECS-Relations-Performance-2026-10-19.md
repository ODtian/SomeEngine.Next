# ECS 关系查询性能与架构证据（2026-10-19）

本次测量通过真实 World、公开查询、现有键索引、实际二叉 AABB 树、维护依赖、成对输出缓冲与几何消费执行九类工作。完整配置为108个独立场景，主查询与额外完整帧共126项验收指标。用户于2026-10-19确定最终每项原精度median比与p95比均≤1.01，并单列H1/H2同函数波动；原生Passed继续保留≤1.00的严格对照。固定输入与采样见[配置](ECS-Relations-Measurement-Configuration.md)，逐项功能及包交付见[证据索引](ECS-Relations-Delivery-2026-10-19.md)。

这些优化对应的公开读取、静态事实、许可与借用回调归属见[接口调整表](ECS-Relations-API-Changes-2026-10-19.md)。

## 几何空工作在分区形成处剔除（r45）

源码 `c0effa290eee225c296f779a85b9d79109d789a5` 只修改 CollisionTree.CreatePartitions：原拆分和选择次序保持，返回前按原顺序压缩单叶自配对与包围盒分离的节点对。原有空间遍历在入口直接剔除同样的节点对；前移后只为需要继续遍历的分区提交计数任务。关系与手写来源都使用同一方法，数据、节点布局、有效分区遍历区域与实体对输出保持对齐。

`PartitionedCountAndFillReturnTheFourSerialContactPairs` 单项通过，实际 NativeAOT publish、源命令确认和push均exit0。生产任务于00:59:35.8719394Z–01:00:59.2955702Z执行83.4236308秒；Native对象26,077,547B、UTC01:00:57.8540395Z。该增量的两个审阅轴均确认此最小回归范围，见[审阅记录](evidence/ECS-Relations-2026-10-19/code-review-final.md)。

两项同源反馈均等价，原严格及批准口径均通过2/2；[原精度表](evidence/ECS-Relations-2026-10-19/focused-native-r45/approved-summary.json)保留实际双侧数据。

| 场景 | 边界 | 关系median ticks | 手写median ticks | median比 | 关系p95 ticks | 手写p95 ticks | p95比 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| threads-small-sparse-parallel | ScheduledPairOutput | 555 | 742.5 | 0.7474747474747475 | 883.25 | 1225 | 0.7210204081632653 |
| threads-small-uniform-parallel | ScheduledPairOutput | 1735 | 2176 | 0.7973345588235294 | 2637.75 | 3307 | 0.7976262473540974 |

按case最近实际完成时间形成的[完整来源索引](evidence/ECS-Relations-2026-10-19/focused-native-r45/latest-case-coverage.json)汇总为108/108等价、严格114/126、批准126/126。来源为r38的93场景/110指标、r39的9/10、r41的4/4和本轮r45的2/2；[来源汇总](evidence/ECS-Relations-2026-10-19/focused-native-r45/latest-case-coverage-summary.json)和[CSV](evidence/ECS-Relations-2026-10-19/focused-native-r45/latest-case-coverage.csv)逐项记录实际源码与原始报告。

本轮稀疏场景实际01:14:48.0808897Z–01:14:48.4324801Z、均匀场景01:22:06.7870844Z–01:22:07.2615292Z，执行累计0.8260352秒。测量准备任务实际执行3.4368573秒，源生产任务实际执行83.4236308秒。各控制与文档观察均在相应场景执行窗口之外；[队列边界](evidence/ECS-Relations-2026-10-19/focused-native-r45/queue-boundaries.json)同时保存外部场景轮转与实际quiet判断。

## 普通组件组结论直接表达（r44）

源码 `bf4348ce600cd423090d1c1430596fc60ea97a2e` 将普通 SpatialComponentFilter 的组结论直接设为 Unknown，对应此类型只在叶子调用组件语义的职责；键和bitmap摘要保持各自的三态事实。真实Native的CountTraversal确认子节点组件状态直接写常量0，内层组合的组件动态继承判断消去；完整函数968条指令、3732B、栈440B，见[r44原生记录](evidence/ECS-Relations-2026-10-19/focused-native-r44/)。

该轮两项等价、批准通过1/2。稀疏并行median为715/705.5 ticks、比值1.0134656272147413，p95为1025.5/1086.25 ticks、比值0.9440736478711163；均匀并行median为1894.5/2195.5 ticks、比值0.8629013892051924，p95为2501.5/3409.25 ticks、比值0.7337390921756984。[原精度数据](evidence/ECS-Relations-2026-10-19/focused-native-r44/approved-summary.json)保留该轮实际结果。

## 空间描述集中持有（r43）

源码 `73f7e089990e65cf4fc5517593e31aaeee03298a` 将空间来源与具体筛选描述集中到一个私有 SpatialQueryData 中，外层 SpatialCandidates 只保存一个引用。公开 Source、Filter 继续按值返回；构造和组合时形成描述数据，setup/cold 沿原范围计入这次分配。各层调度传递该引用，实际枚举仍使用具体泛型。输入保持1项、实际 NativeAOT 发布及源码命令确认均 exit0。新描述的持有和默认相等行为见[接口调整表](ECS-Relations-API-Changes-2026-10-19.md)。

同一计数 Data.Execute 的实际 Native 读取确认：指令66→59、代码274→264B、栈248→184B、初始化144→112B；每个分区的 TJob 本地值缩至16B。Filter按值getter对应的32B副本继续保留，来源于公开值语义。完整函数、统计和原始生产回执保存在[r43原生目录](evidence/ECS-Relations-2026-10-19/focused-native-r43/)。

| 场景 | 边界 | 关系median ticks | 手写median ticks | median比 | 关系p95 ticks | 手写p95 ticks | p95比 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| threads-small-sparse-parallel | ScheduledPairOutput | 741 | 729 | 1.0164609053497942 | 1161.5 | 1100.5 | 1.0554293502953203 |
| threads-small-uniform-parallel | ScheduledPairOutput | 2189 | 2352 | 0.9306972789115646 | 3338.75 | 3980.25 | 0.8388292192701463 |

两项均等价、批准通过1/2；[按case最后实际完成记录汇总](evidence/ECS-Relations-2026-10-19/focused-native-r43/latest-case-coverage.json)为108/108等价、严格113/126、批准125/126，稀疏并行为本轮余项。该轮场景实际执行0.8734899秒、报告5.1793279秒、整轮294.3263704秒。Master在嵌入读者成功后等待外部释放记录，实际于00:26:57.6464040Z补齐；[执行边界](evidence/ECS-Relations-2026-10-19/focused-native-r43/queue-boundaries.json)中的ControlReleaseCorrection记录这次控制等待。后继读者在保存证据时直接写入释放记录。

## 分区内来源直接执行（r42）

源码 `d224fbff18b9ec94cefb85edaf37db8bad6cf173` 将内部 CountJob、FillJob 改为普通结构体，使各分区本地任务中的来源直接执行。PairJobData 继续只读保存原始任务，执行处继续形成各分区自己的 TJob 值。00:05:45.7066630Z–00:06:18.8569592Z 的输入保持行为检查通过；实际 NativeAOT 发布及源码命令检查均 exit0。

CPU读者 `16edbce2c6204e5280e7021ed442d137` 于00:07:45.7100462Z–00:07:49.1340376Z执行，exit0；读取的对象为26,042,005B，UTC00:07:06.7085146Z。[单个完整原生函数](evidence/ECS-Relations-2026-10-19/focused-native-r42/native-PartitionData.asm)及[实际统计](evidence/ECS-Relations-2026-10-19/focused-native-r42/native-function-statistics.json)确认以下同函数变化。

| 关系侧计数 Data.Execute | r41 | r42 |
| --- | ---: | ---: |
| 指令数 | 75 | 66 |
| 代码字节 | 343 | 274 |
| 栈字节 | 312 | 248 |
| 初始化字节 | 208 | 144 |
| 分区任务本地副本字节 | 72 | 72 |
| 随后来源副本字节 | 64 | 0 |

两项反馈范围为 threads-small-sparse-parallel 和具有实际填充输出的 threads-small-uniform-parallel，沿相同配置测量提交到完成边界。

实际两项均等价，必验边界通过一项，见[原精度表](evidence/ECS-Relations-2026-10-19/focused-native-r42/approved-summary.json)。

| 场景 | 边界 | 关系median ticks | 手写median ticks | median比 | 关系p95 ticks | 手写p95 ticks | p95比 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| threads-small-sparse-parallel | ScheduledPairOutput | 703 | 681 | 1.0323054331864905 | 1039.75 | 1159.75 | 0.8965294244449235 |
| threads-small-uniform-parallel | ScheduledPairOutput | 2075.5 | 2251.5 | 0.9218298911836553 | 2782.25 | 3724.5 | 0.7470130218821318 |

## 分区数据共享、候选借用和列读取（r41）

源码 `5b6c96e8ed4f78e4c2a617c345e49623a19d5c91` 的分区批次共享 PairJobData，各任务携带数据引用和分区编号，执行处保留各自的 TJob 值；固定双向候选以 ref readonly 借用身份行；组件列在行枚举形成有效行号后直接取得引用。后者同时用于关系与手写的公开 ReadRows 路径。[六条实际原生函数](evidence/ECS-Relations-2026-10-19/focused-native-r41/native-functions.raw.asm)及[核对片段](evidence/ECS-Relations-2026-10-19/focused-native-r41/native-confirmation-excerpts.txt)保存对应指令。

本轮六场景全部等价，六条必验指标通过五条，详见[原精度表](evidence/ECS-Relations-2026-10-19/focused-native-r41/approved-summary.json)。前轮大规模 Default 和双向候选的两条余项达标，小规模稀疏并行保留其实际差值。

| 场景 | 边界 | 关系median ticks | 手写median ticks | median比 | 关系p95 ticks | 手写p95 ticks | p95比 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| threads-small-sparse-parallel | ScheduledPairOutput | 716.5 | 708 | 1.012005649717514 | 1157.5 | 1101.5 | 1.0508397639582387 |
| threads-small-uniform-parallel | ScheduledPairOutput | 1990.5 | 2221.5 | 0.8960162052667117 | 2608.25 | 3268.25 | 0.798057064178077 |
| finite-large-mask8-mutual | Query | 268195.5 | 269146.5 | 0.9964666083341228 | 288072.75 | 290092 | 0.993039277194821 |
| finite-small-mask1-mutual | Query | 3898.5 | 3989.5 | 0.9771901240756987 | 4476.75 | 4635.75 | 0.9657013428247856 |
| default-large-dynamic90 | Query | 266999.5 | 288378.5 | 0.9258647922782038 | 278045.5 | 299389.5 | 0.9287082546315084 |
| default-large-dynamic50 | Query | 254856.5 | 275595.5 | 0.9247484084464369 | 273603.25 | 295006.5 | 0.9274482087682814 |

同一源码同时修复启用状态变化的成员维护：World、QueryRow、QueryChunkView 统一记录实际变化，既有实体变更观察推动树成员更新。审阅修复批次16项相关检查、最终源7项增量检查均通过，见[双轴审阅](evidence/ECS-Relations-2026-10-19/code-review-final.md)。

## 输出与并行前缀改动反馈（r39）

源码 `393a9fbf009aa2e8269636fdf532551271a85bc5` 将公开枚举输出保存在唯一终端值中，筛选层继续借用该终端，外层finally统一回写；并行配对在count任务全部完成后，由父执行直接累计前缀。父执行写入输出资源，子fill声明各自私有写区间。[六条完整原生路径](evidence/ECS-Relations-2026-10-19/focused-native-r39/native-six-functions.raw.asm)与[对应片段](evidence/ECS-Relations-2026-10-19/focused-native-r39/native-output-value-excerpts.txt)确认Default、Key、Mutual逐结果输出地址读取已消去。相关6项Relations、3项Physics行为检查全部通过。

本轮覆盖r38的12条余项及finite-large-mask8-mutual、default-large-dynamic50两个关联场景。14个场景全部等价；15条必验指标中≤1.00通过9条、≤1.01通过12条。[原精度表](evidence/ECS-Relations-2026-10-19/focused-native-r39/approved-summary.json)、[原始索引](evidence/ECS-Relations-2026-10-19/focused-native-r39/index.json)保留本轮全部结果。以下三条为本轮余项，双方各256帧。

| 场景 | 边界 | 关系median ticks | 手写median ticks | median比 | 关系p95 ticks | 手写p95 ticks | p95比 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| threads-small-sparse-parallel | ScheduledPairOutput | 790.5 | 749 | 1.055407209612817 | 1343.75 | 1260.75 | 1.065833829069998 |
| finite-large-mask8-mutual | Query | 268618.5 | 271380.5 | 0.98982240802121 | 291954 | 288920.5 | 1.0104994280433546 |
| default-large-dynamic90 | Query | 261471.5 | 257452 | 1.0156126190513184 | 272111.5 | 268253 | 1.0143838093143414 |

实际NativeAOT恢复及发布任务为`6e2ce65a563b48c98da5ebbe9976a6c9`，六角色CPU读者为`c0b4357d7bd14e01b9a3e96f942f8bf2`。原`78b`沿普通JIT恢复结果执行publish，命令exit0后形成托管产物，exe检查使总任务exit1；接续6e2显式恢复AOT依赖后生成实际原生对象与exe。原78b及其后置011门控回执另行保留。14场景的Master准备结束于23:11:26.0482858Z，末场景回执观察于23:12:23.5476774Z，单CPU三段分析于23:12:31.2873494Z完成。

[本轮队列事实](evidence/ECS-Relations-2026-10-19/focused-native-r39/queue-boundaries.json)记录场景实际执行57.4884483秒、场景间隔0.0095493秒、三段分析7.7394402秒；提交至报告完成72.149083秒，QuietPassed=true。[逐边界GC](evidence/ECS-Relations-2026-10-19/focused-native-r39/gc-by-boundary.json)中30个侧/边界组合各256帧，Gen0/1/2均为0。

## 最近完整矩阵（r38）

源码 `51c8ce75949cf16d7190e14028c655f526d5697b` 的139个场景任务与5个分析任务均exit0。NativeAOT的108个场景全部结果等价；126条必验记录中，原≤1.00严格通过103条，按用户批准的≤1.01通过114条。[原始索引](evidence/ECS-Relations-2026-10-19/final-native-r38/index.json)、[完整原精度表](evidence/ECS-Relations-2026-10-19/final-native-r38/approved-summary.json)与[12条余项](evidence/ECS-Relations-2026-10-19/final-native-r38/approved-unmet.md)保存各自实际输入、边界、分配、GC和绝对ticks。12条余项的两侧各256帧中Gen0/1/2均为0。

| 场景类 | 必验记录 | ≤1.00严格通过 | ≤1.01批准通过 |
| --- | ---: | ---: | ---: |
| finite | 18 | 9 | 15 |
| default | 9 | 2 | 4 |
| character | 12 | 8 | 10 |
| bitmap-space | 18 | 17 | 18 |
| character-space | 6 | 6 | 6 |
| complete | 18 | 18 | 18 |
| updates | 18 | 17 | 17 |
| threads | 18 | 17 | 17 |
| consume | 9 | 9 | 9 |

同源JIT独立记录9个场景，9项等价，11条必验记录按≤1.01通过9条；实际数值见[JIT表](evidence/ECS-Relations-2026-10-19/final-jit-r38/approved-summary.json)。[九项更新分阶段](evidence/ECS-Relations-2026-10-19/final-phases-r38/index.json)与[九项新进程启动](evidence/ECS-Relations-2026-10-19/final-startup-r38/)沿自己的计时边界保存。

从Master提交到最后分析完成为2218.687801秒。各部分采用实际任务回执统计：

| 环节 | 任务数 | 实际秒数 |
| --- | ---: | ---: |
| 独占准备 | 1 | 170.0310502 |
| 场景执行 | 139 | 899.6199581 |
| 分析执行 | 5 | 47.3681585 |
| 其余任务与调度间隔 | — | 1101.6686342 |

完整事实见[执行时间表](evidence/ECS-Relations-2026-10-19/final-validation-r38/execution-summary.json)。Native108单系列执行769.5431486秒，场景间隔720.1267171秒，首场景22:15:58.3390174Z、末场景22:40:48.0088831Z。[队列边界](evidence/ECS-Relations-2026-10-19/final-native-r38/queue-boundaries.json)保存统一预提交、准备完成及观察区间；五系列QuietPassed均为true。

## 同源同函数对照（r38）

同一51c产物的H1/H2均执行相同手写函数，实际侧状态独立、工作区顺序共享，Required=false。[中性汇总](evidence/ECS-Relations-2026-10-19/final-hh-r38/control-summary.md)与[原始索引](evidence/ECS-Relations-2026-10-19/final-hh-r38/index.json)保留Query和Consume的各自分布。

| 场景 | 边界 | H1 median ticks | H2 median ticks | median H1/H2 | p95 H1/H2 |
| --- | --- | ---: | ---: | ---: | ---: |
| finite-small-mask1-mutual-control | Query | 3978.5 | 3977.5 | 1.0002514142049026 | 0.99437733118076044 |
| default-small-dynamic10-control | Query | 998 | 998 | 1 | 0.97347198641765709 |
| finite-large-mask8-mutual-control | Query | 264241 | 264105 | 1.0005149467068022 | 0.99354239542077416 |
| default-large-dynamic50-control | Query | 254937 | 254274 | 1.0026074234880484 | 1.0045233144067844 |

## 历史完整矩阵（r32）

历史完整系列`formal-native-r32`对应C#源码`3b582fe541d86d7f556b3f9d6854962d292454ef`的同一NativeAOT产物，每侧32×8帧、先后各128帧。108项全部等价，126项中原≤1.00严格通过104项，按用户批准的ApprovedRatioLimit=1.01通过110项。[双口径逐项表](evidence/ECS-Relations-2026-10-19/formal-native-r32/approved-summary.json)、[16项余项完整同列表](evidence/ECS-Relations-2026-10-19/formal-native-r32/approved-unmet.md)、[原始报告索引](evidence/ECS-Relations-2026-10-19/formal-native-r32/index.json)和[队列边界审计](evidence/ECS-Relations-2026-10-19/formal-native-r32/queue-boundaries.json)保存实际结果。以下同一组列分别给出两种判定。

| 场景类 | 主指标≤1.00 | 主指标≤1.01 | 额外完整帧≤1.00 | 额外完整帧≤1.01 |
| --- | --- | --- | --- | --- |
| finite | 7/18 | 13/18 | — | — |
| default | 0/9 | 0/9 | — | — |
| character | 11/12 | 11/12 | — | — |
| bitmap-space | 18/18 | 18/18 | — | — |
| character-space | 6/6 | 6/6 | — | — |
| complete | 9/9 | 9/9 | 9/9 | 9/9 |
| updates | 9/9 | 9/9 | 9/9 | 9/9 |
| threads | 17/18 | 17/18 | — | — |
| consume | 9/9 | 9/9 | — | — |

本轮108个case与3个分析均exit0。最后case提交07:41:37.2003216Z，准备结束07:41:38.4516225Z，首case开始07:41:38.4523762Z；最后case结束08:02:09.7127512Z，首分析开始08:03:03.7813299Z。case实际进程执行765.978644秒，case间隔465.281731秒。预监听读取manifest为07:41:38.4125360Z–07:41:38.4343887Z，与case重叠数为0；最终分析收据事件08:12:10.6944000Z结束保护窗口。

前序完整`controlled-r22`对应源码`0ac0159349d41d46fa8197b86a4e1bd148debf4a`，108项等价、≤1.00为108/126；其[原精度表](evidence/ECS-Relations-2026-10-19/controlled-r22/summary.md)与[原始报告](evidence/ECS-Relations-2026-10-19/controlled-r22/index.json)保留自身31轮配置。

前序完整`matrix-r15`对应源码`cc08ae37b03e7d9066adbd78207a6e8249a4b1d1`，126项中99项通过；其[原精度表](evidence/ECS-Relations-2026-10-19/matrix-r15/summary.md)与[原始报告](evidence/ECS-Relations-2026-10-19/matrix-r15/index.json)保留。后续受影响子集按各自完整系列保存，结果由其实际源码和原始门槛确定。

## 历史同函数对照（r32）

独立系列`handwritten-native-r32`使用同一C#源码`3b582fe541d86d7f556b3f9d6854962d292454ef`与同一NativeAOT产物。H1和H2分别持有自己的侧状态，顺序复用相同真实工作区，并执行同一个独立手写类型。每侧32×8帧，先运行与后运行各128帧，16次预热与7对冷构建沿正式配置执行。两项实际结果等价，Required=false；[原始报告](evidence/ECS-Relations-2026-10-19/handwritten-native-r32/index.json)、[中性汇总](evidence/ECS-Relations-2026-10-19/handwritten-native-r32/control-summary.md)与[逐顺序分布](evidence/ECS-Relations-2026-10-19/handwritten-native-r32/handwritten-order.json)保存独立诊断。

| 场景 | 边界 | H1 median ticks | H2 median ticks | median H1/H2 | H1 p95 ticks | H2 p95 ticks | p95 H1/H2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| default-large-dynamic50 | Query | 279085.5 | 279357.5 | 0.9990263372202286 | 302176 | 304618 | 0.9919834021627087 |
| finite-large-mask1-mutual | Query | 263141 | 262912.5 | 1.0008691104454903 | 278763.25 | 277829.5 | 1.003360874205223 |
| default-large-dynamic50 | Consume | 34238 | 33938.5 | 1.0088247860099886 | 38115 | 37440.5 | 1.0180152508647053 |
| finite-large-mask1-mutual | Consume | 28 | 28 | 1 | 36 | 34 | 1.0588235294117647 |

计时频率为10000000 ticks/s，两项两侧帧内分配总量均0，GC0/1/2均为0。Default两端类型均为HandwrittenDefaultSide，finite两端均为HandwrittenMutualCandidatesSide。Consume记录真实接触累计；finite本项为零输出，保留其28 tick的实际边界。逐顺序统计由每帧QueryTicks和ConsumeTicks投影，两个边界沿同一真实帧顺序。各项median/p95与正式EH结果分别呈现。

独占准备于08:36:34.6373061Z开始、08:36:37.6281879Z结束，最后case提交08:36:36.2493426Z；两个case分别执行于08:38:20.4200084Z–08:38:35.7183983Z和08:46:16.5186382Z–08:46:35.3059396Z。最终分析于08:51:27.9378760Z结束，监听事件08:51:27.9383666Z释放保护窗口。[完整边界审计](evidence/ECS-Relations-2026-10-19/handwritten-native-r32/queue-boundaries.json)与[命令、stdout、stderr](evidence/ECS-Relations-2026-10-19/commands-handwritten-native-r32.json)保留实际调度和采样边界。

## 正式3b读取状态的编译证据

正式产物的默认配对核心由实际World回调调用位置选定。两份完整机器码及后续文本统计保存在[当前核心证据](evidence/ECS-Relations-2026-10-19/commands-native-r32-core.json)。原读取任务7704f5da已输出两个完整函数，后置统计的变量覆盖使总退出码为1；ee23bbaf继续读取原stdout完成修正统计。下表的代码位置采用同一obj的`.managedcode$I`节偏移。

| 同一要素 | 关系Required核心 | 独立手写核心 |
| --- | --- | --- |
| 函数入口 | 0x1d49c0 | 0x15e5e0 |
| 入口栈 / 初始化 | 920B / 848B | 920B / 848B |
| 双循环热区 | 0x1d4a75..0x1d4d16 | 0x15e698..0x15e939 |
| 热区指令 / 字节 | 137 / 673B | 137 / 673B |
| 热区起点mod32 / mod64 | 21 / 53 | 24 / 24 |
| 每outer的剩余行枚举值复制 | 200B | 200B |
| 行推进 / chunk推进 / 列绑定 | 0xd8e0 / 0x154d40 / 0x161600 | 0xd8e0 / 0x154d40 / 0x161600 |

组件引用、Kind短路、完整Entity顺序、输出字段以及共同调用逐项对应；实际代码地址与分支边界各自保留。Current重构针对这组共有枚举数据：当前位置字段作为主要持有处，QueryRow与QueryChunkView在读取Current时形成，后续产物记录实际复制量、栈和测量结果。

## 测量边界

| 名称 | 开始 | 完成 | 实际工作 |
| --- | --- | --- | --- |
| Setup | 创建该侧数据 | World、Scheduler、查询、输出和接触累计形成 | 冷装配和真实初始分配 |
| IndexBuild | 开始构建派生索引 | 索引构建或首维护完成 | 同输入树、摘要、键桶与维护初始化 |
| FirstQuery | 冷索引已形成 | 首次公共查询返回 | 查询准备、遍历、筛选和真实pair写入 |
| FirstConsume | 首次pair已形成 | 首次消费完成 | 计数或真实几何/穿透累计 |
| Query | 进入公共查询 | 输出数量已写回 | 查询准备、遍历、筛选与输出的融合工作 |
| MaintenanceAndQuery | 当帧数据写入完成 | 维护依赖与查询填充完成 | 提交、维护、查询及输出 |
| FrameTotal | 开始当帧写入或静态配对 | 消费依赖完成 | 实际整条工作链 |
| ScheduledPairOutput | 外层成对任务提交 | 全部分区与输出完成 | 分区、count/prefix/fill、子任务调度与join |
| PairsAndConsume | 进入公开配对入口 | 实际几何消费完成 | 配对和真实接触累计 |
| IndependentMaintenance/Query/Consume | 该阶段前置依赖已完成 | 该阶段提交至完成 | 独立诊断采样，原始报告各自具名 |
| ProcessToFirstResult | 启动新进程 | 父进程收到首结果行 | 操作系统启动、程序集初始化及Main内首执行 |

MainTicks从Main入口开始；FirstQueryTicks只包围该进程的首次查询。进程到首结果单独使用父进程时钟。维护、查询和消费的独立阶段采样位于 `--mode phases`，主维护链仍按真实依赖连续调度。Query将融合的遍历、筛选与输出作为一个实际边界记录。

所有帧在计时范围外记录当前/全线程分配与GC/Managed/WorkingSet，原始 FrameAllocatedBytes覆盖该帧实际查询和消费。cold total覆盖装配、索引、首查询及首消费。计数/前缀/填充按现有公开调度边界进入ScheduledPairOutput。

## 对照算法与共同内核

静态warm顺序复用同一World、成员查询、候选与pair/contact工作数组；每侧持自己的计数、索引、树/摘要、维护观察者和作业输出资源。cold和持续更新各自持有实际构建的数据。固定输入、身份创建顺序、业务值、遍历顺序、摘要布局、分区/线程配置、输出格式、采样与消费相同，报告分别记录共同工作区与每侧资源。

| 工作 | 扩展入口/独立部分 | 手写入口/独立部分 | 共同具体内核 |
| --- | --- | --- | --- |
| 默认配对 | ComponentPairsWithin，具体组件绑定和IRelation调用 | HandwrittenDefaultPairs，剩余行组合与直接Kind条件 | World查询、相同ReadRows列绑定、完整Entity先后规则、真实输出 |
| 角色键桶 | KeyGroups/KeyPairs及具体键语义 | HandwrittenKeyPairs，键收集、桶对和实体循环直接展开 | World.GetByIndex、相同HashSet键序、成员确认与输出 |
| 有限类别 | 固定候选→BitmapFilter→具体OneWay/Mutual许可 | HandwrittenBitmapCollision，直接字段位交集 | 同候选、World查询与可选引用读取、身份输出与接触计数 |
| 空间筛选 | SpatialTraversal与具体组合筛选/证明 | HandwrittenSpatialBitmap/Keys/Groups，节点/方向/证明/叶子循环独立 | CollisionTree、相同摘要数组/布局、基本树/摘要操作与查询读取 |
| 增量维护 | CollisionMaintenance与具体摘要变更组合 | HandwrittenSpatialMaintenance，观察/版本/成员/父链路由独立 | EntityTableChanges、ChangedChunks、相同树/摘要基础更新和Scheduler |
| 串并行输出 | CollisionPartitions与公开FillPairs/FillPairsParallel | HandwrittenSpatialPartitions，独立EnumeratePartition | 相同CreatePartitions、CollisionPairBuffer、count/prefix/fill、ReadOnlyQueryPairJobs和子任务 |
| 实际消费 | 公开配对结果进入消费 | 手写配对结果进入同一消费 | 相同PairJob/ConsumeJob、Body几何读取、接触次数及穿透累计 |

完整 `complete`、`consume`、以及空间键/bitmap类直接从公开关系入口与 `HandwrittenSpatialCollision.Filtered/Bitmap/KeyPairs` 入口成对计时，查询准备成本随实际调用计入。持续更新和调度类记录各自实际维护与输出路径。

## 内存范围

原始 IndexMemory 将 TreeLive、TreeAddressed、TreeCapacity 分列；live为存活拓扑，addressed为可寻址节点高水位，capacity为实际数组。类别和键摘要分别记录同三种范围。LeafMap记录有效键值payload、条目数及实际entry容量；KeyBuckets记录现有索引实体payload。

输出记录实际行数、已用payload和容量。分区记录节点对、count、offset、有效分区索引四数组及访问数组的已用/容量；维护访问与接触累计单列。具体元素大小来自目标产物 `Unsafe.SizeOf<T>()`。对象、字典管理、查询、观察者、Scheduler与租用数据进入真实分配和进程内存读数。线程类的pair与接触累计分别由其实际缓冲持有。

## 原始差值与落实的架构变化

| 源码提交 | 实际差值/指令 | 实现变化 | 原始记录 |
| --- | --- | --- | --- |
| 1bc8ebb4 初始 | 完整空间重复三次归一读取定义；每次1632B，稳定帧多4896B；维护未变化行进入值类型默认接口方法，456×32B装箱 | 复用已经满足的读取定义、单成员融合范围；库内HasSummaryChange显式实现；逐块保存具体filter | [initial-small](evidence/ECS-Relations-2026-10-19/initial-small/index.json)、[optimized-1-small](evidence/ECS-Relations-2026-10-19/optimized-1-small/index.json) |
| 7c6febe4 → 920e4eb4 | 固定候选先验证成员，后续绑定再次回查；空间来源也取得了相同行 | 查询专用输出/谓词借用两行、末端一次身份适配；键源先排除身份顺序；空间逐叶复用行 | [optimized-2](evidence/ECS-Relations-2026-10-19/optimized-2/index.json)、[独立阶段](evidence/ECS-Relations-2026-10-19/optimized-2-phases/index.json) |
| 4f52aa7a | Has和读取列重复定位；摘要热调用重复取得Nodes跨度；内外两层exclude | TryReadRef借用引用；节点引用直接进入摘要证明；Where/Except在既有筛选持有处统一选择 | [optimized-3](evidence/ECS-Relations-2026-10-19/optimized-3/index.json) |
| 22d9b1d3 | exclude仍处于热布尔数据流；普通组件、bitmap、key可在循环前选择同一接受策略 | 静态WhereSelection/ExceptSelection；过滤层仅持实际数据 | [optimized-4](evidence/ECS-Relations-2026-10-19/optimized-4/summary.md) |
| 35e71c5a | 默认绑定第二参数在语义短路前定位；两侧原行遍历具有同一成员/版本行为 | ReadRows在块边界绑定可选列，引用按行索引借用；手写默认配对同用公开准备；bitmap缺失早返回后直接许可尾表达式 | [optimized-5](evidence/ECS-Relations-2026-10-19/optimized-5/summary.md)、[完整matrix-r5](evidence/ECS-Relations-2026-10-19/matrix-r5/summary.md) |
| b68e892d | Native默认循环在排序和输出各读两次Entity；两个方向展开两套Has/Matches；顺序条件先物化三向数值 | 缓存外/内Entity；先选择有向组件引用后共用一次Matches；Precedes表达完整身份bool先后，手写同步采用相同准备 | [optimized-6](evidence/ECS-Relations-2026-10-19/optimized-6/summary.md) |
| a40d6c75 | 键桶80B枚举复制与32B Current中转；维护逐行重复组件/版本读取；Tree getter多次调用 | MoveNext(out group)、原成员视图复用；chunk版本绑定与静态组合变化读取器；同类型几何/摘要变化共享，Tree局部引用 | [optimized-7](evidence/ECS-Relations-2026-10-19/optimized-7/summary.md)、[构建与红绿](evidence/ECS-Relations-2026-10-19/commands-r7.json)、[实际Native读取](evidence/ECS-Relations-2026-10-19/native-r7.json) |
| 3bf336e1 | 必有组件仍逐对判断存在性；交换不变规则先排序；整体CategoryBits投影提前读取双方字段 | Requires闭合Required/Optional模式，IsSymmetric先匹配后排序；具体许可按方向读取标量，摘要保持原布局 | [optimized-8](evidence/ECS-Relations-2026-10-19/optimized-8/summary.md)、[行为与发布](evidence/ECS-Relations-2026-10-19/commands-r8.json)、[实际Native读取](evidence/ECS-Relations-2026-10-19/native-r8.json)、[九个独立维护阶段](evidence/ECS-Relations-2026-10-19/phases-r8/summary.md) |
| dcf50ecc | 默认私有循环只使用First成员，但接收完整视图；同组件AddReads执行两次 | 私有循环直接接QueryCursor，入口一次Requires分类，同类型读取静态合并；恢复键收集的独立边界 | [optimized-9](evidence/ECS-Relations-2026-10-19/optimized-9/summary.md)、[构建、检查与caller读取](evidence/ECS-Relations-2026-10-19/commands-r9.json) |
| fc2b9c2d | r9轻selector前仍构造216B视图；键收集与配对共享成员字段的长存活 | 轻selector内联后仅形成First24B，caller栈256B降为88B；薄构造器赋原字段，独立CollectKeys接收查询借用 | [optimized-10](evidence/ECS-Relations-2026-10-19/optimized-10/summary.md)、[构建、原生与成员检查](evidence/ECS-Relations-2026-10-19/commands-r10-r11.json) |
| 87559c84 | 纯成员判断形成40B QueryRow；Contains全内联后两侧对子调用选择不同 | Contains复用同一World匹配，输出需要组件行时静态保留TryGetRow；共同bool匹配边界独立并复用一次EntityStore读取 | [optimized-11](evidence/ECS-Relations-2026-10-19/optimized-11/summary.md)、[构建、原生与许可检查](evidence/ECS-Relations-2026-10-19/commands-r11-r12.json) |
| 89a59155 | 许可两方向直接test/je；pair中转有四次int写入、16B重读和回调this读取 | 无状态static abstract结果操作，原pair以scoped in直达输出；默认私有loop仅接members/predicate/output，共同Contains单次取得Store | [optimized-12](evidence/ECS-Relations-2026-10-19/optimized-12/summary.md)、[实际指令与检查](evidence/ECS-Relations-2026-10-19/commands-r12-r13.json) |
| d361242b | static结果和原身份直接写输出；两端候选loop各展开两大份World匹配 | TryGetEntityQueryRow与Contains保留共同点匹配边界，内部复用同一record helper，具体许可与结果继续内联 | [optimized-13](evidence/ECS-Relations-2026-10-19/optimized-13/summary.md)、[实际原生与分布读取](evidence/ECS-Relations-2026-10-19/commands-r13-r14.json) |
| 0f4fdf4b | 行匹配保留同一独立入口；Key caller仍形成216B视图，bitmap输出引用跨组件绑定存活 | 键轻入口向静态桶循环仅传具体成员与访问输入；先绑定组件引用，再向具体许可借用唯一输出 | [optimized-14](evidence/ECS-Relations-2026-10-19/optimized-14/summary.md) |
| cc08ae37 | 默认Bound在轻分支前清1136B回退状态；固定候选先持两份QueryRow跨第二次点读取 | 轻只读选择与完整回退的数据形成分开；固定身份通用绑定先取得第一组件再进入第二次点读取 | [optimized-15](evidence/ECS-Relations-2026-10-19/optimized-15/summary.md)、[实际原生与IL](evidence/ECS-Relations-2026-10-19/commands-r15.json) |
| 92d8b230 | Bound回退清零已裁剪；Single仍独立清120B、复制32B描述两次并判断可空版本 | 明确内联现有Single，在轻入口继续合并普通调用的版本与执行准备 | [optimized-16](evidence/ECS-Relations-2026-10-19/optimized-16/summary.md)、[外层原生与聚焦检查](evidence/ECS-Relations-2026-10-19/commands-r16-r17.json) |
| 5bf03d44 | Single已合入公共调用并闭合默认版本；仍有三次32B描述复制，位图每候选形成两份40B行 | Single构造以scoped in直接接收原描述字段并持有一次值副本；类型化公共点读取将成员匹配与单组件引用形成放在同一操作 | [optimized-17](evidence/ECS-Relations-2026-10-19/optimized-17/summary.md)、[实际外层与点读取红绿](evidence/ECS-Relations-2026-10-19/commands-r17-r18.json) |
| 22fad2a6 | 共同typed point已缩为8B out；每候选仍清16B；scoped in构造另形成64B执行临时 | 内部点读返回单个ComponentReference，公开轻包装形成bool/out；固定数组直接foreach；执行局部直接赋已有字段 | [optimized-18](evidence/ECS-Relations-2026-10-19/optimized-18/summary.md)、[实际四函数与聚焦检查](evidence/ECS-Relations-2026-10-19/commands-r18-r19.json) |
| bb799424 | typed point已通过RAX返回；循环首引用/count仍有栈存取，默认仍两段32B描述 | 深point直接借用原QueryCursor输入；轻只读选择直接读取原readonly描述字段 | [optimized-19](evidence/ECS-Relations-2026-10-19/optimized-19/summary.md)、[实际四函数与聚焦检查](evidence/ECS-Relations-2026-10-19/commands-r19-r20.json) |
| 81478391 | 许可/首引用/count热段已对应手写，输出准备仍有两段24B，候选loop展开在World作用域 | 薄入口与独立数组core分开；filtered直接形成现有字段；已有读取轻复用与新定义创建分开 | [optimized-20](evidence/ECS-Relations-2026-10-19/optimized-20/summary.md)、[实际四函数与聚焦检查](evidence/ECS-Relations-2026-10-19/commands-r20-r21.json) |
| 9f749801 | 独立数组core接受热段30条/101B，手写29条/100B；每候选多一次输出引用探测及提前地址形成 | core以单一局部具体输出执行整个候选循环，finally回写原输出；普通调用、异常和提前结束沿同一回写范围 | [optimized-21](evidence/ECS-Relations-2026-10-19/optimized-21/summary.md)、[实际四函数与7项行为](evidence/ECS-Relations-2026-10-19/commands-r21-r22.json) |
| 0ac01593 | 单向28/29条、双方31/32条热段已直接对应；默认准备仍有第一份32B metadata值及固定初始化，Where/Except在描述中为bool | BitmapFilter与ComponentPairsWithin以TSelection闭合接受选择；生成Where/Except直接返回对应普通组合值，既有过滤输出与许可继续复用 | [受控完整系列](evidence/ECS-Relations-2026-10-19/controlled-r22/summary.md)、[后继五函数与10项行为](evidence/ECS-Relations-2026-10-19/commands-r22-r23.json) |
| 204efb6b | 静态选择使默认准备各减16B、两份描述32→24B；第一份metadata临时仍存在，当前默认双方137指令/673B热体完全对应 | RelationQuery以普通struct的单值字段持有声明描述，公共Enumerable继续值返回，实际执行体仍消费值副本并finally回写输出 | [受控27项](evidence/ECS-Relations-2026-10-19/controlled-r23/summary.md)、[当前热体、样本及生命周期检查](evidence/ECS-Relations-2026-10-19/commands-r23-r24.json) |

每份原始记录包含其实际完整提交。公开行为检查覆盖原成员、异型/可选组件位置、Where/Except、字面结果、ref输出与提前结束；[交付索引](ECS-Relations-Delivery-2026-10-19.md)列出实际文件。最终包、生成物与Native消费链沿[实际包刷新入口](ECS-Relations-Packages-AOT-2026-10-19.md)对应代码。

a40d6c75 的结构变化来自同一 NativeAOT 产物的指令定位：键桶枚举把相同成员视图保留成长期局部，挤占输出身份字段寄存器；`KeyGroups.MoveNext` 先写 Current 再由调用方读取。实现改为直接使用原成员视图和 `MoveNext(out group)`。筛选使用静态 `RequiresMatch` 直接选择接受分支。维护把 Tree 引用保留在 Reconcile/Refit 调用局部，并通过 `ReadRowChanges<T>()` 在 chunk 边界绑定原版本跨度；组合筛选给回调提供具体变化读取器，原 `RowIndices` 继续形成成员集合。手写维护使用相同公共版本读取。

新增公共版本读取的红检查队列为 `98eed6b306cf4c7991fe012c33768823`（公共成员尚待实现时的 CS1061），绿检查 `6212b321edd94f24afbb42b73bb595f5` 通过1项真实 World 行版本检查。关系桶与筛选队列 `2bafedde700a4ac88bb35bcb332a2775` 通过5项；维护及并行消费队列 `61d5cfc267384e9ba435cba3322d9687` 通过4项。这些检查均运行仓库行为规则，结果为0 violations。

r7 的原生证据同时明确了已收敛的部分：键桶直接进入 HashSet.MoveNext 和 GetByIndex，80B枚举复制与32B Current中转均已消去；原来提前形成的身份tuple也已标量化为三个寄存器与一个栈字段，手写使用两个寄存器与两个栈字段。exclude只参与桶级接受选择，inner pair直接执行身份顺序、成员和输出。后续键改动因此集中在仍独立调用且复制24B输入/结果的KeyGroups构造，使用原构造器内联完成准备融合。

r7 稀疏维护在小/中/大的 MaintenanceAndQuery 原精度比值分别为0.9479976090854751/0.913827655310621、0.9288389513108615/0.9190815542019042、0.925881086898036/0.9221554987415026，三个完整帧也通过同一严格判断。默认配对的下一处成本来自排序在Kind匹配之前执行以及两次HasValue；已有查询的Requires事实闭合必有模式，作者显式IsSymmetric事实允许先匹配再排序。有限类别的具体许可改用标量读取，单向按需取得两个字段，双方许可按两个方向短路，CategoryBits/摘要保持原布局。

r8 的 Required+IsSymmetric 默认循环与手写已逐条对应：配对主段均为673字节，内层组件绑定、Kind、身份、排序和输出均为175字节；通用入口额外抽取24B成员查询，frame为912B，手写为880B，行栈位置相差8。实际Where caller先形成216B视图，再复制First查询并读取Requires；手写直接向循环传入24B查询。后续把私有循环输入收敛为QueryCursor members，并按相同组件类型合并AddReads。

r8 同时给出了构造器强内联的实际回退证据：KeyPairs栈由r7的392B升到808B，收集键所用成员视图的四个字段长期占用寄存器，候选和输出新增Index/Generation栈重载。小规模角色16聚集的原精度比值变为1.0904528217711353/1.095232464179428。实现恢复独立的键收集边界，桶级选择和已标量化tuple继续沿原路径。两侧HashSet/List、成员查询、GetByIndex算法和数据持有均相同。

r9 的 SingleExecution caller 把完整216B视图就地形成：Access/First共48B，另外七个查询共168B清零，然后独立调用公开selector。它已收敛r8的整块BulkMove，剩余完整构造由只读CPU队列 `a52f561cbb374a3fb960995e245516ca` 直接确认。下一处实现只内联这个无候选循环的轻selector，让其仅First读取在调用处裁剪；候选循环保持独立。键构造进一步收敛为原三个字段的薄赋值，具体收集helper独立接收scoped in查询借用，成员字段的存活在收集函数结束。

r10 实际指令确认轻selector收敛：caller仅形成First的24B，读取Requires后直接进入Required循环，栈由256B变为88B。Mutual许可按方向懒读并使用内存test；最终bool进入Where仍保留setne/movzbl/test。对应小规模mask4双方许可的median为4740/4885.5ticks、p95为5460.95/5423.95ticks，实际输出1008对。这些原始结果逐项保存实际判定。

键桶中的纯成员检查继续使用同一World点匹配流程：QueryCursor.Contains只取得成员事实，TryGetRow取得后续组件读取所需的行。KeyPairs按输出ReadsComponentRows静态选择，手写键枚举及Between去重同步采用Contains。公开Changed行与optional读取检查先得到缺少Contains的红结果，再通过1项真实World行为；既有角色、组合、ref输出提前结束和Between共5项通过。

r11 的26个反馈场景完整保留。其中默认私有循环和手写的673B主段、175B内层、137条归一化操作、880B栈及三个共享推进/列绑定调用均对应；实例入口额外两条操作使主循环相对起点增加5B。实际默认比值仍有5%–9%差值，后续将私有循环输入改为显式成员与关系值，测量继续观察实际入口和布局的影响。键桶的内联成员判断中，扩展保留IsAlive/TryGetValue调用，手写展开IsAlive并直入FindValue；成员匹配保留独立bool边界，使双方进入同一具体实现。角色键tuple和输出字段继续具有同样紧凑形状。

有限类别的结果消费接口为具体许可增加Evaluate回调，默认实现继续调用该许可自己的Matches。Builtin许可按方向进入Matched/Unmatched，原BitmapFilterOutput单持有当前身份和输出状态，组件链沿原scoped行转发。自定义完整目标类别许可的字面结果与既有六项位图行为共7项通过，检查队列为 `fd00d6ee4a6445ecb374592797908f36`。

r12 在15项反馈中确认中/大角色键及小规模dynamic90通过严格判断。实际原生读取显示许可两个方向已直接test/je；当前pair仍在适配器中写四个int，接受后再读16B写输出。后继把输入改为scoped in，将结果操作改为static abstract无状态类型，原输出仍单持有。7项检查队列 `d153f245229045c1b700c0283f89615b` 通过。r12 的默认static入口准备由5条变为2条，手写为3条；键的四个成员检查均调用同一Contains函数，其中Store引用形成一次并复用。

r13 的16项反馈中8项严格通过。原生读取确认了static结果操作与原身份直接进入终端输出；双方方向判断均为Allowed读取、内存test及直接条件分支。共同World行匹配在提取helper后被两侧候选loop各展开两份，包括结构状态、record、archetype字典和逐行筛选。后继为TryGetEntityQueryRow保留独立边界，和Contains复用同一匹配helper。剩余Output借用跨组件绑定的现象由同一原生记录保留，反馈继续按实际边界定位。

r13 单向mask4小场景的高尾集中在连续时段：扩展后段变慢后，紧随的手写帧也连续变慢；其原精度1.3779131621735523判定仍保存。中规模角色64交错的扩展分布整体右移，median61225/59574.5ticks，两侧先后次序均出现持续差值。所核对的五项每侧248样本均记录GC=0、DataWrite=0及恒定一致的输出量，后续优化据此区分具体执行成本和实际波动。

r14 保留共同点匹配边界后，16项反馈中3项严格通过；单向mask1小规模为0.90920840064620356/0.94650281360586308，中规模角色64交错为0.99643525823937729/0.9909627398585259。双方许可mask4大规模为1.0090796568356213/1.0141933825092542。原生键caller仍构造Access/First共48B并清零其余168B，再独立进入大循环；其具体配对规则只读取两个成员集合。后继将公开入口与桶循环分开，Within沿同一First借用，Between/Product保留各自实际读取范围；语义和实现以scoped in继续保持描述的只读调用含义。

r15 的16项代表反馈中9项严格通过。小/中角色键的median/p95比值分别为0.93889668573896146/0.72199782401740809、0.92901277737097154/0.92179233462713184；大规模角色16交错为1.0002589194514533/1.0174027101224457。小规模mask4单向与双方为0.9789706696181516/0.94767167825777465、0.99105545617173529/0.93550897237590025；大规模mask4双方为0.9885768930983212/0.97329500175662587。两向匹配、自定义结果转发及缺失组件规则由7项公开检查覆盖，键位置、组合和提前结束由5项检查覆盖。

r15 的[实际原生读取](evidence/ECS-Relations-2026-10-19/commands-r15.json)确认键caller只形成24B第一成员输入，并将同一地址传给Within桶循环的三个查询借用；core栈为248B。位图两侧各调用两次同一World行匹配函数，输出在组件引用形成后直接取地址，原输出地址的跨绑定栈存放已收敛。当前固定身份来源先形成两份40B行，再进入组件绑定；手写在第二次点匹配前已形成第一组件的8B借用，二者的实际寿命与frame分别记录在同一输出中。

默认外层的独立读取定位到实际数据形成范围：EnumerateBoundQuery入口为1192B栈，在只读CanWrite条件前清1136B；只读分支随后进入另一个168B栈、清120B的Single。手写入口为104B栈、清56B。双方GetQueryDefinition、WithOptionalRead、AcquireQueryRecord和ReleaseQuery各执行一次。完整回退的数据形成因而需要独立于轻量的单成员选择入口；实际边界与指令由同一原生记录持有。

固定候选的身份数组由当次参与实体形成，有限类别采样保持该成员集合。来源直接转发身份后，通用identity binder依次执行第一成员点读取、第一组件引用、第二成员点读取、第二组件引用；手写保持相同两次公共点读取并直接执行字段位运算。多层固定身份筛选各自绑定的成本进入其完整调用，已经取得行的Within/键/空间来源继续复用查询专用借用输出。

r16 的32个反馈场景形成33项主/完整帧判断，其中5项通过。小规模角色16聚集/交错为0.90826284970722182/0.93446435379336668、0.92376388586364622/0.96284720521236455；稀疏维护和完整帧、空间角色交错均通过。位图18项的实际median比为1.0010687566797292至1.153845045045045，默认配对9项为1.0008403633609675至1.02089081592432，原精度逐项保存在同一报告中。

r16 原生外层确认Bound的1192B栈和1136B回退清零已裁剪，Single仍保留120B初始化、两次32B描述复制和可空版本分支；两侧外层初始化合计176B/80B。[有限类别函数摘录](evidence/ECS-Relations-2026-10-19/native-r16-mutual-excerpt.json)保留明确的选取范围与源队列元数据：查询字段和输出已在候选循环前形成局部，第一组件引用跨第二次point读取存活；每候选仍有覆盖两份40B QueryRow的六次16B零写，手写在入口初始化行home。该记录为按函数选取的有界摘录，范围说明随证据保存。

r17 的12项反馈中3项通过：默认small dynamic10为0.99429073342116825/0.88786111669682954，large dynamic10为0.99807369137956747/0.99244240602079692，角色large hair64 clustered为0.95837238104866929/0.96186877128710302。其余默认7项与角色2项逐项保留原精度判定。原生Single符号已消入RelationArraySide.Query，默认基线直接调用同一ExecuteQuery；外层栈合计320B降为264B，初始化仍176B，手写对应192B/80B。描述仍经三段32B复制进入执行数据，后继让Single构造以scoped in直接接收原描述字段并持有一次普通值副本，输出保留值字段及finally回写。普通描述的ref借用实验由C#输出回写的CS8352约束收敛到这一值持有方式，实际构造复制继续由原生证据核对。

类型化公共点读取通过真实World红绿检查：Changed成员、托管组件和可选读取给出字面结果4,-1,-1,-1，红队列5b97d35094724620a72e63a1a6a06398、绿队列d1f56e6fe2484602950f347998c941ed。QueryCursor.TryReadRef直接复用原World成员匹配和访问声明，成功只形成8B组件借用。位图身份绑定与手写候选依次执行相同两次公共操作，绑定失败直接进入具体Unmatched，成功由具体许可Evaluate驱动输出；7项位图行为通过队列7b766e27a4a240ecb196ce6d895a475b。

r18 的30个反馈场景均完成等价核对，5项通过严格判断：finite 2/18、default 0/9、character 3/3。三项角色键的原精度比值分别为0.97809878844361609/0.95980235042735063、0.97042172944079652/0.97734208970487202、0.97680443499685976/0.96925287715227315。中规模mask8双方的实际比值为1.2490853752267348/1.2400116983307163，该项原样保留。

r18 的四个完整原生函数确认两侧共同调用同一typed point，成功写8B引用并返回bool。通用每候选清零从96B缩为16B，第一组件引用跨第二次调用存活，许可按方向直接load/test/je；组件读取成功后的旧bool合流也已消去。剩余offset栈存取、count双读及两份引用home初始化与手写逐项记录。默认scoped in构造实际产生64B执行临时，frame与清零各比r17多32B，后继直接初始化执行局部的三个字段。内部点读进一步用既有ComponentReference的HasValue表达存在性，以单值返回替代bool和out-buffer，公开TryReadRef包装继续保留；固定数组来源直接foreach原数组，保留身份复制与输出语义。

r19 的30项反馈中15项通过：finite 6/18、default 6/9、character 3/3。默认三项剩余p95比为1.0005188644281373、1.0131834303687346、1.0035292589086611，全部按原精度保留判断。小规模mask1单向为1.1486469154803129/1.1834857251575210，中规模mask8双方为0.97966326262765147/0.99850081364069820。

r19 的World点读取直接通过RAX返回8B引用，候选中的第五out-buffer和16B引用home清零均已收敛，数组迭代使用指针递增及dec/jne，原offset栈存取已消去。通用首引用仍跨第二次调用存入栈，手写保存在寄存器；通用同时持有World/查询状态/版本三个字段，后继让深point借用同一原QueryCursor，在该边界读取字段。默认直接局部赋值使frame由296B降为216B，清零由208B降为120B，描述为两段32B、输出为一段24B。六项关系与一项真实World检查均通过，源码及完整原生输出由同一证据文件关联。

r20 的30项反馈中13项通过：finite 3/18、default 7/9、character 3/3。默认small dynamic50的median/p95为0.98155814828754229/1.0267195923929140，small dynamic90为1.0030674846625767/0.96410583166454256，其余七项严格通过。所有输入与原始帧保持同一配置。

r20 单向接受热段的扩展为28条/105B，手写为29条/100B：首组件引用均在callee-saved寄存器中跨第二次point存活，count均为一次load及递增，许可均为load/test/je。通用从查询home取地址，手写保留查询地址；两侧输入与输出读取均按实际指令记录。剩余输出准备在通用World回调中先形成24B临时，再复制24B至filtered.Output，后继直接形成原filtered的Relation/Output字段。固定候选公开入口向独立core仅传原数组与ref具体输出，使World作用域控制状态的寿命留在循环调用侧。默认准备仍为216B栈/120B初始化；WithOptionalRead进一步用既有归一化CanRead事实形成轻复用入口，新定义路径保留原类型验证及构造。

r21 的30项反馈中11项通过：finite 3/18、default 6/9、character 2/3。独立core使World作用域状态留在调用侧；caller准备从360B降至280B，新增core为88B，二者峰值368B。filtered.Output从原输出直接复制一次24B。默认WithOptionalRead命中已有读取定义时在轻入口返回原值，创建路径独立；实际描述仍为两段32B、输出一段24B。

原始调度记录补充了测量控制层的实际活动：r20提交30项用时9.1070587秒，其中13项在最后一次提交前开始执行。后续完整系列将Submit-Measurements自身作为公共队列独占准备任务：准备期间直接取得固定场景列表，显式使用同一session依序提交全部case及collect/export/report，然后退出。case在准备任务结束后开始，报告读取与计算沿预排CPU任务完成。每份系列保留queue-submission.json中的准备起止、session、源码和报告任务编号；原有系列保留其实际时间戳与原始判定。

首个[受控完整系列controlled-r22](evidence/ECS-Relations-2026-10-19/controlled-r22/summary.md)对应源码0ac0159349d41d46fa8197b86a4e1bd148debf4a，108项均完成等价核对，126项严格指标通过108项。有限类别4/18、默认配对8/9、角色键11/12、bitmap加空间17/18、键加空间5/6；完整配对主/完整帧18/18、持续更新主/完整帧18/18、串并行调度输出18/18、物理消费9/9。每项原精度median/p95及18个剩余判断随同原始样本保存。

[队列边界审计](evidence/ECS-Relations-2026-10-19/controlled-r22/queue-boundaries.json)确认最后case提交03:28:27.997716Z、独占准备完成03:28:29.2372729Z、首case开始03:28:29.2382396Z；末case完成03:40:42.8891869Z，首分析任务03:40:42.8898856Z开始。测量733.6509473秒，108case及3个分析任务同session、退出码均为0。[完整命令与输出](evidence/ECS-Relations-2026-10-19/commands-controlled-r22.json)保留独占准备的result/stdout/stderr及r22实际候选函数。

r22 单向候选的扩展接受热段为28条/93B，手写为29条/100B；逐候选输出引用探测与提前Output地址形成已收敛，首引用跨第二点读取保存在寄存器，count为单次读取及递增。原具体输出局部56B在进入core时形成一次，并沿正常/finally路径回写；core准备为136B。该事实与首轮受控结果共同记录，后继定位继续按实际剩余路径进行。

[controlled-r23](evidence/ECS-Relations-2026-10-19/controlled-r23/summary.md)保存静态选择组合的首个受控27项反馈，源码204efb6b78a3c383e4637ed22aafe997c1daac37，finite 6/18、default 0/9。默认median比值范围为1.0004498425551056至1.0155228758169934，全部原精度p95与实际判定保留。其[队列审计](evidence/ECS-Relations-2026-10-19/controlled-r23/queue-boundaries.json)确认准备/测量/分析边界，测量163.6807398秒；[样本审计](evidence/ECS-Relations-2026-10-19/controlled-r23/sample-audit.json)确认27项等价、cold14、每侧248帧、31×8轮序一致及GC0/1/2合计0。

r23 位图Where回调以具体选择形成24B查询及24B输出，准备184B、初始化88B。默认准备由232B/120B变为216B/104B，metadata与执行描述各为24B，输出仍一次24B。当前默认Required热体与手写均为137条/673B，所有寄存器、栈位移、立即数、内部分支与共同调用逐项对应，栈均920B。后继以普通描述字段收敛metadata约束调用的第一份24B临时，声明读取和执行值副本各沿其数据范围使用。

[controlled-r24](evidence/ECS-Relations-2026-10-19/controlled-r24/summary.md)对应7acdf434f01f10277893af75aaee7a97bae9e81f，首个受控27项反馈为finite 5/18、default 1/9。默认small dynamic50为1.0146938775510204/1.0448454292158686，middle dynamic50为1.0112303542106498/1.0010711728942503，large dynamic50为1.0021519326965131/0.99989612269048656。[原始报告与完整命令](evidence/ECS-Relations-2026-10-19/commands-controlled-r24.json)保留该轮全部结果；[队列审计](evidence/ECS-Relations-2026-10-19/controlled-r24/queue-boundaries.json)确认准备、162.7652618秒测量及分析依次完成。

r24 单默认入口确认metadata24B临时消去，直接从原字段取得World与QueryHandle；实际执行描述24B、Output24B各形成一次。准备由216B/104B收敛为184B/80B，正常/finally状态回写继续在同一范围。该数据形成事实与本轮原精度结果并列保留。

后续机器配置诊断保持7ac的引擎实现，在同一Native产物内对default-small-dynamic50与finite-large-mask1-oneway分别运行Normal与Fixed。配置、实际处理器范围、优先级与CPU端点由[测量配置](ECS-Relations-Measurement-Configuration.md)定义；四项诊断作为自己的完整系列保存，用来核对机器配置与当前差值。

[machine-r25诊断](evidence/ECS-Relations-2026-10-19/machine-r25/diagnostic.md)保留四项原始结果及控制活动。Fixed有限类别large mask1单向的median/p95为0.9643794829654223/0.9604935553229572，Normal为1.0437229868497686/1.0440907032186741；Fixed默认small dynamic50为1.0212071778140293/0.99780131920847492。Fixed两个输入的全部逐帧CPU端点均为2，Normal有限类别前后端点变化分别为扩展20帧、手写22帧。后续统一使用Fixed及原进程32CPU范围、0/4worker；默认入口继续沿实际描述准备定位。

[controlled-fixed-r26](evidence/ECS-Relations-2026-10-19/controlled-fixed-r26/summary.md)对应9b3f58c90829b058b11ecbe0fb49f7bd1a4a5d9f，固定CPU2的首轮27项通过7项（finite6/18、default1/9）。默认small/middle/large dynamic50分别为1.0310271041369472/0.97523734027895759、1.0296317942723554/1.0243374911868706、1.0303313358861328/1.0566557359095816。Bitmap内部来源metadata24B临时已经消去，实际outer由216B/112B收敛为200B/88B；对应完整原生函数和本轮全部样本继续持久保存。

当前默认双方热体137条/673B逐项对应，成员表、chunk及行顺序相同。此前两份World按固定Relations先创建，实际World和数组地址分别由独立分配形成。后继将静态warm的数据范围收敛为一个MeasurementData，由外层统一持有；双方顺序复用真实输入及pair/contact工作区，各自查询、树、摘要、计数和作业输出资源保留其实际构造。Verify先保存第一侧完整真实结果，再执行第二侧。cold与持续更新继续独立构建，报告按实际工作区逐份列出一次构造和容量。引擎及独立手写算法沿9b版本，采样、阈值和输入值继续按原配置执行。

[controlled-shared-r27](evidence/ECS-Relations-2026-10-19/controlled-shared-r27/diagnostic.md)是共同工作区的首个受控27项，源码1906048b37581a883a665355b29c9c44642760ad，严格通过18项（default6/9、finite12/18）。每份报告的DataSharing、Workspaces与两侧IndexBuildTicks直接对应实际构造，内存payload按主要持有处分别列出。九个剩余项的原始尾部分析保留绝对微秒、GC0、CPU2和round/先后顺序/frame分布；其中两项出现跨多个帧的连续高耗时区间。原始单帧判断与后续控制层实验分列保存。

后继只将采样顺序收敛为round→frame→双方：每个实际帧立即配对E/H，下一round反向。16帧预热、31×8真实帧、单帧完整时钟、observer、算法和严格阈值沿原配置。Input显式记录SideSwitchIntervalFrames=1、FramesPerSample=1；原始8帧块交替及其九项失败继续保存。该控制层调整针对实际连续时间区间，逐帧p95继续由真实单帧值计算。

[controlled-paired-r28](evidence/ECS-Relations-2026-10-19/controlled-paired-r28/diagnostic.md)是逐帧交替的首个受控27项，源码8bd88b1dea3dad7508f822760a83c35836557c9e，严格通过finite6/18、default0/9。默认九项median比值为1.0190481153831121–1.0329585110507948；finite middle/large mask1 mutual的median/p95分别为1.0645942038096436/1.0519977369640303、1.1578234869830621/1.1378207371981459。全部实际等价结果、原始单帧分布和173.6511692秒控制边界在同一系列保存，后续定位继续沿这些具体调用路径进行。

r28当前原生五函数同时确认：Default候选体137条/673B逐条对应，Mutual四条拒绝路径分别为11/16/19/22条，两侧点读取次数为1/2/2/2；九字段视图已裁剪到24B成员值及24B调用参数。下一处准备优化把Requires判断集中到不可变QueryDefinition，QueryCursor委托同一事实，ComponentPairsWithin构造时保存两位置的_componentsRequired。原查询定义固定，WithOptionalRead保持成员Kind，枚举直接选择既有Required/Optional核心。构造成本进入实际描述准备和冷初始化；[红绿及聚焦检查](evidence/ECS-Relations-2026-10-19/commands-r29-checks.json)记录缺少公开入口的CS1061，以及All/唯一Any/混合Any、可选两位置、Where/Except和ref输出提前结束的五项通过。

[controlled-required-r29](evidence/ECS-Relations-2026-10-19/controlled-required-r29/diagnostic.md)对应e17a82f594c8e6c9ed4f4ff35b5e316a67afa314，严格通过10/27（finite8/18、default2/9），27项实际输出与消费均等价。Default caller的Requires调用已收为保存事实的字段分支，栈136→104B、初始化48→24B，24B查询参数副本也收为同址传参，原673B核心继续保留。本轮场景执行合计169.6701125秒、场景间隔1585.6874934000002秒，全部收据及原精度结果随同保存。授权状态读取06:07:41.1781258Z–06:07:41.3298360Z位于实际场景间隔；最终分析的result.json事件在成功或失败时结束监听。

后继准备调整使原持久成员查询在形成时声明该场景实际读取：finite为CollisionLayer，character为Hair，default/bitmap-space为Body，其余空间组合为Body与Hair。双方AddReads复用同一原定义，原Members持续持有同一QueryState与匹配数组；读取声明及匹配数据形成计入共同Setup及独立cold。每份工作区投影实际Terms/Access，具体范围见[测量配置](ECS-Relations-Measurement-Configuration.md)。引擎与手写核心、输入与采样继续沿e17。同一新Native产物先运行四项正式反馈与两项H1/H2同函数诊断，控制报告以中性字段单列Required=false，正式原精度判定独立保存。

[controlled-reads-r30](evidence/ECS-Relations-2026-10-19/controlled-reads-r30/diagnostic.md)保存持久读取配置的四项正式反馈与两项中性控制，源码f1cdfbb98a76b4e7e1b6a4d0da87b23afa6449b0。全部实际结果等价，EH/HH逐帧分配均为0；finite两项严格通过，Default-S10/L50的median分别为1.0027522935779816、1.0004441998868749，p95为0.97959734259602738、0.93563420265186903。HH Default-L50同函数median/p95为1.0007473816222079/0.99614245750650265，Required=false。两类原始分布与当前读取持有调整的作用范围分别保存，正式严格门槛沿原值。

r30的两侧Default World回调实码均为104B栈/24B初始化，均各调一次独立热函数；该共有边界与两项实测一并保留。后继只对ComponentPairsWithin私有核心增加AggressiveInlining提示，验证候选循环进入World回调后的实际调用/帧变化；原算法、值参数与手写函数继续沿现状，性能按新产物的两项反馈判断。

[controlled-inline-r31](evidence/ECS-Relations-2026-10-19/controlled-inline-r31/diagnostic.md)记录私有核心内联提示的两项受控回归，源码edbd5ab42d3dac63c7c4a98deecc070b57c8cf77，Default-S10/L50 median为1.1234684260131951/1.1375282213467639，p95为1.1492156001756415/1.1311168276851726。关系完整World回调由104B栈/24B初始化变为1848B/1744B，Required/Optional两体合并且行推进形成显式callee；手写同范围保持原值。后继只撤回该提示，恢复原独立私有核心；原始数据与全部命令继续保留。

原持久成员定义已包含全部只读访问时，后继终端通过引用相等确认同一定义，直接借用原句柄进入World.ExecuteQuery，省去同key的临时Query/Release登记。实际派生定义与可写/多输入路径沿原范围管理自身查询；独立手写Default、Bitmap、Key与空间入口同步采用同一复用规则。原句柄由原持有者最终释放，输出仍通过finally回写。

该范围对应源码`7f8d27eb97fdf34f020d1ab12e42fb3b59f7df82`。[聚焦行为回执](evidence/ECS-Relations-2026-10-19/commands-r32-checks.json)保存关系6项与空间2项的完整命令、输出和退出码：原句柄在提前结束后继续查询、只读版本与显式基线、可选组件位置、键与位图ref输出、单向空间组和完整空间组均通过，两个项目的行为规则检查均为0 violations。

后继全部场景采用32轮×8帧，使每侧256帧中先执行与后执行各128帧；16warm、7cold、每帧实际调用、时钟/observer和严格门槛沿当前定义。[测量配置](ECS-Relations-Measurement-Configuration.md)记录该对称分组；r31及此前31轮系列的248帧与128先/120后分布保持原始记录。

[r34聚焦结果](evidence/ECS-Relations-2026-10-19/focused-native-r34/summary.md)保存查询状态进一步收敛后的16项等价与Approved15/16；[r35原始结果](evidence/ECS-Relations-2026-10-19/focused-native-r35/summary.md)保存固定候选引用输出的4/5批准结果。[r36短循环单文件反馈](evidence/ECS-Relations-2026-10-19/focused-native-r36/diagnostic.md)保存Eq5/5、Approved3/5及相同字段的旧/新循环对照；[同源HH2](evidence/ECS-Relations-2026-10-19/diagnostic-native-r36-hh2/diagnostic.md)以H1/H2和Required=false独列。[大规模单向CPU采样](evidence/ECS-Relations-2026-10-19/profile-native-r36-large-oneway/diagnostic.md)保存原0c二进制的真实函数样本、点读取调用栈及11.6MB采集文件。静态指令变化、完整实测差值与CPU样本分别记录。
## 执行与复现

实际公共队列入口为当前主检出已有 `F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1`。提交脚本通过 QueueScript 参数指定该绝对入口，默认按仓库tools定位；每次queue的Executable/Arguments/Directory/SessionId与Submitted/Started/Finished时间均保存在持久index。以下命令使用运行器同一Release/NativeAOT输出，实际SourceCommit由原始报告和提交参数指定：

```powershell
[string[]]$publish = @('dotnet','publish','benchmarks/SomeEngine.ECS.Relations.Benchmarks/SomeEngine.ECS.Relations.Benchmarks.csproj','-c','Release','-r','win-x64','--no-restore','-p:SourceRevisionId=<实际提交>','-o','benchmarks/SomeEngine.ECS.Relations.Benchmarks/bin/Release/net10.0/win-x64/native-publish')
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 @publish
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 --session <会话> pwsh -NoProfile -File benchmarks/SomeEngine.ECS.Relations.Benchmarks/Submit-Measurements.ps1 -QueueScript F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 -Session <会话> -CollectReports -SourceCommit <实际提交> -Series <系列>
```

MSBuild属性通过实际 `[string[]]` 参数传递；每项场景分别FIFO提交。JIT在相同Release输出目录以`-p:PublishAot=false -p:SelfContained=false -p:SourceRevisionId=<实际提交>`构建普通托管入口，再以`-Backend JIT`单独提交系列；报告记录.NET10默认TieredCompilation/ReadyToRun及实际环境。`-Mode phases`只选择持续更新场景并保存真实独立阶段。原生进程首结果由[Measure-Startup.ps1](../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Measure-Startup.ps1)按七对交替新进程采样。

# 固定候选来源的短循环反馈

本轮源码为 `0c267d975a26539d9b48ec7cff9bc47bc7d44b1c`，前一轮为 `a182cc35b9bbf7d63008c8cbe4a96f976091614d`。这是基准代表实现中固定身份候选来源的设计：候选身份由来源持有，位图绑定沿公共 QueryCursor.TryReadRef 取得组件，具体许可持有匹配算法。

本轮单文件修改把既有短 foreach 放入公开 AggressiveInlining Enumerate，删除私有 NoInlining 转发。候选顺序、许可、ref 输出、提前结束、实际读取和采样参数继续沿原定义。源码/7项位图行为检查/Native 发布队列为 `b719b102537f4e3aa764dea217eb2fe6`，实际退出码0；四角色机器码读取为 `ecacd58dfc144c24abfe70c2242d0fd7`，实际退出码0。Native 对象时间为2026-10-19T19:04:11.0600041Z，大小26,199,322B。

每侧32轮×8帧，共256帧；16帧预热，7对冷样本，逐帧交替，Fixed配置CPU2。下表使用每项原始报告已经计算的 Query median/p95 E/H 比值，Approved要求两值均≤1.01，Strict字段保存≤1.00的原判定。

| 场景 | r35 median | r35 p95 | r36 median | r36 p95 | r36 Approved |
| --- | ---: | ---: | ---: | ---: | --- |
| finite-small-mask1-mutual | 0.98 | 0.9844567559249 | 0.9956962025316456 | 1.0382403992189195 | false |
| finite-small-mask8-oneway | 1.0037511318070107 | 0.9760522905279766 | 0.9557697059550284 | 0.9865863673692855 | true |
| finite-middle-mask8-oneway | 1.0136020373614403 | 1.0132711547548123 | 0.967870523003147 | 0.9677488921507927 | true |
| finite-large-mask8-oneway | 1.0051214893865508 | 1.0071951408021678 | 1.0160899602790086 | 1.030979933037002 | false |
| finite-large-mask8-mutual | 0.9976054157885903 | 1.0033269581906146 | 1.0018995569469131 | 1.004912354009327 | true |

r36五项输出等价、Approved3/5、Strict2/5。完整分布、逐帧样本、分配/GC、CPU端点、工作区和成员声明见[批准汇总](approved-summary.json)、[原始索引](index.json)、[前一轮原始索引](../focused-native-r35/index.json)。

机器码表比较相同候选处理范围。旧来源为独立私有核心，新来源为World回调中的内联循环；各完整函数及其原始边界保存在[命令与机器码记录](../candidate-loop-commands.json.gz)。

| 候选处理字段 | r35 OneWay | r36 OneWay | 手写 OneWay | r35 Mutual | r36 Mutual | 手写 Mutual |
| --- | --- | --- | --- | --- | --- | --- |
| 接受路径指令/字节 | 30/101 | 28/102 | 29/100 | 33/110 | 31/111 | 32/109 |
| 首组件缺失拒绝指令 | 13 | 11 | 11 | 13 | 11 | 11 |
| 次组件缺失拒绝指令 | 18 | 16 | 16 | 18 | 16 | 16 |
| 首方向拒绝指令 | 21 | 19 | 19 | 21 | 19 | 19 |
| 次方向拒绝指令 | — | — | — | 24 | 22 | 22 |
| 每候选输出接收方probe | 1 | 0 | 0 | 1 | 0 | 0 |
| Query地址传入 | 2次MOV | 2次LEA | 2次MOV | 2次MOV | 2次LEA | 2次MOV |
| 第一引用跨第二点读 | R14 | R14 | R15 | R14 | R14 | R15 |
| 许可 | load/test/直接分支 | load/test/直接分支 | load/test/直接分支 | 两方向load/test/直接分支 | 两方向load/test/直接分支 | 两方向load/test/直接分支 |

新OneWay和Mutual World回调均为200B栈、88B入口初始化；当前24B查询与24B输出状态各形成一次，最终回写输出。逐候选probe和早期输出地址形成已经移出循环；Query参数改为两次形成栈内查询地址。中规模单向改善与大规模单向反向变化都属于这次单文件修改的整体实测。表中的静态操作与完整耗时分别记录，后续CPU采样用于核对实际热点归属。

同源同函数控制独立记录于[HH2诊断](../diagnostic-native-r36-hh2/diagnostic.md)，Required=false。当前源码与Native保持本轮版本。准备任务 `87d55964e8d141f396fdc8478906853f` 实际退出码0，门控读取Approved3/5并保存Continue=false；producer启动器记录PREPARATION_STOP approved=3/5。

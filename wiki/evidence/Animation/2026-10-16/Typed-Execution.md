# 类型化动画执行证据

## 设备与方法

本记录对应 #48、#83、#49、#50、#51、#52 的共同公开执行路径。机器为 Intel Xeon E5-2698B v3（16 核、32 线程），Windows 11 专业工作站版 10.0.26200，.NET 10.0.13，X64。设备记录见 [machine.json](machine.json)。

全部构建、发布、执行与测量通过 `F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1` 进入共同 FIFO。Release 测量设置 `DOTNET_TieredCompilation=0`。完整可复现命令、每类计时边界和结果字段见 [消费者说明](../../../../tools/SomeEngine.Animation.Checks/README.md)。

纯批次使用 64 次预热、96 次采样，1,000 / 10,000 / 100,000 条轨道。直接循环与公共批次入口使用相同参数、状态、目标和时间；内置 Spring 与消费者 Spring 使用相同质量为一的积分公式。完整路径使用 12 次预热、48 次采样，固定 `1/120` 秒增量，目标值为 `1,000,000`，持续活动状态与最终字段值由公开 API 验证。

`executableBytes` 表示进程映像文件大小：Release 的 apphost 与 NativeAOT 的本机映像分别记录。算法方法和具体字段写入方法的机器码字节另列。峰值工作集为同一次进程的累积峰值；保留堆在完整 GC 后观察，首次声明、激活与预热分配分别记录。

## 纯算法 Release

队列 `e419456cab7546afb0e858d1696d99d7`，退出码 0。[标准输出](batch-release.stdout.txt) · [标准错误](batch-release.stderr.txt) · [队列结果](batch-release.result.json)。

100,000 条轨道，单位 ns/轨道：

| 算法 | 公共批次中位数 | P95 | P99 | 直接循环中位数 | 实例字节 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Tween | 4.927 | 5.829 | 6.026 | 3.833 | 40 |
| Spring | 9.563 | 11.291 | 14.089 | 6.507 | 52 |
| 消费者 Spring | 9.937 | 11.828 | 13.238 | 6.567 | 52 |
| 消费者 History | 5.535 | 6.343 | 6.859 | 4.757 | 80 |
| 消费者专用 History 批次 | 5.679 | 6.328 | 6.591 | 5.208 | 80 |

全部 30 组稳态分配为 0 B/批。实例字节包含参数、算法状态、端点、时间、采样值与活动标记。消费者的托管参数和历史通过普通 C# 泛型存储保留，公共批次入口处理三种实际布局。该结果提供算法成本参照；完整字段路径还包含系统、查询、来源维护、合成、字段写入和版本提交。

同公式静态缓动与标量函数适配追加队列 `0620ee4248c242349747d5c9e98812d3`，退出码 0。[标准输出](function-release.stdout.txt) · [标准错误](function-release.stderr.txt) · [队列结果](function-release.result.json)。两种路径都计算 `SquareEasing.Sample`，追加 12 组全部为 0 B/批。

| 100,000 条轨道 | 批次中位数 | P95 | P99 | 直接循环中位数 | 实例字节 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 静态 SquareEasing | 5.353 | 6.011 | 6.196 | 3.581 | 40 |
| 标量函数 SquareEasing | 6.444 | 8.114 | 12.961 | 4.621 | 56 |

标量适配每个未完成的活动样本调用一次参数中的函数地址，批次中位数增加 1.091 ns/轨道；该差值同时包含调用和实例步长差异。公共批次包含活动位与批次视图选择，直接循环遍历同一组持续活动数据。

## Release 机器码

队列 `70d8117777824c32b06c8ccead43d794`，退出码 0，`DOTNET_TieredCompilation=0`，`DOTNET_JitDisasm` 选取具体入口、求值和根写回方法。[完整机器码与输出](codegen-jit.stdout.txt) · [标准错误](codegen-jit.stderr.txt) · [队列结果](codegen-jit.result.json)。全部方法标为 `FullOpts`。

| 具体组合 | 分发跳板字节 | 批内循环字节 | 循环调用的算法体字节 |
| --- | ---: | ---: | ---: |
| Tween / FloatOps / SquareEasing | 6 | 457 | 0（已内联） |
| Spring / FloatOps | 6 | 501 | 252 |
| 消费者 History / FloatOps | 6 | 434 | 0（已内联） |
| FunctionTween / FloatOps | 6 | 458 | 标量缓动 5 |

公共 `MotionDispatcher` 在批次边界通过保存的 managed 地址执行一次 `call rax`，6 字节跳板再跳入确定的具体循环。Tween 的缓动与 FloatOps、History 的采样和历史递增均在循环内形成具体指令。Spring 的批次循环调用固定类型的 `Evaluate` 方法，其积分体中的值运算形成浮点指令。标量函数适配在循环内的未完成样本分支额外执行一次 `call rax`；类型化 `SquareEasing` 的平方形成乘法指令。

引用根的 `World.ExecuteComponentWrite` 为 714 字节，根循环 `Channel.WriteRoots<FieldRoot, FieldAccess>` 为 1,102 字节，包含 table/sparse 分支与 ECS 当前行解析、版本维护的共同代码。批次回调在 World 边界执行一次 `call rsi`。根循环在取得实际根后，具体访问及属性 setter 内联为对象引用读取和常量字段偏移写入；行定位、COW 与版本维护使用既有 ECS 方法。此处的字节统计包含该方法的检查和异常分支，热调用描述对应正常执行路径。

补充字段边界队列 `f69a85438aaf4b58960a400215b3c1b5`，退出码 0。[机器码与输出](codegen-field-jit.stdout.txt) · [队列结果](codegen-field-jit.result.json)。类型化 `Channel.WriteBatch` 为 242 字节，在批次边界从字段描述取得具体函数地址并执行一次 `call rax`；具体 `WriteFields<FieldRoot, FieldAccess>` 为 76 字节，将 span 和 Channel 放入栈上后调用具体根批次。加上 World 边界的一次回调，共两次应用层间接调用服务整个字段批次；每项的具体访问和 setter 继续内联到根循环。727 字节的 `ReferenceFieldEntry` 包含世界、对象、实体、输入数组创建及最终值检查，作为公开入口的对应证据。

## ECS 公开行为

Debug 独立消费者通过 `FieldAccessChecks.VerifyAll` 运行 13 条共享公开行为，另执行包含引用的泛型根及缓冲元素操作。最新根批次运行队列 `9615540e72d340fe83141c7abec6bf37`，退出码 0。[标准输出](ecs-root-batch.stdout.txt) · [标准错误](ecs-root-batch.stderr.txt) · [队列结果](ecs-root-batch.result.json)。

检查覆盖 table 与 sparse 的普通、值嵌套、引用嵌套和可写属性；行、列与任意实体批次写版本；当前对象身份；包含托管引用的泛型缓冲；managed 查询和系统回调的公共结果。任意实体批次共享一次根存储写权限和一个提交版本，批内解析当前行并调用具体访问类型。

既有 ECS 与 Systems 测试工程的规则诊断保存在 [ECS 输出](ecs-rules.stdout.txt)、[ECS 结果](ecs-rules.result.json)、[Systems 输出](systems-rules.stdout.txt) 和 [Systems 结果](systems-rules.result.json)。独立消费者共用公开行为源文件，提供各能力的运行证据。

## 算法与图公开行为

核心与类型化图的 Release 定向验证通过 27 项，行为规则检查为 0 项违规。队列 `01c002753d7a438b89c8ff24354c7e7e`，退出码 0。[标准输出](core-typed-graph.stdout.txt) · [标准错误](core-typed-graph.stderr.txt) · [队列结果](core-typed-graph.result.json)。覆盖完整类型参数、managed 状态与消费者算法、阶段和完成、共享图定义、实例参数更新、固定截止时间重定目标、自定义条件及自定义叶生命周期。

兼容图、销毁清理与输出卸载的补充执行合计形成 46 个不同公开检查，命令与原始三件集中在 [图行为验收](Graph-Checks.md)。

# 同一产物的机器配置诊断

四项使用 NativeAOT 源码 `90204159caede908528b16d5c7fed48c23570c92`，引擎代码沿 `7acdf434`。提交脚本源码为 `79644af4d3b214d881b336b8eca60a941b99fe4b`。两种机器配置共享输入、实现、31×8帧采样、16帧预热和七对冷初始化。原始样本见本目录四份压缩 JSON，[完整命令](../commands-machine-r25.json)保存发布及独占准备的 result/stdout/stderr。

| 输入 | 配置 | 扩展 median ticks | 手写 median ticks | median 比值 | p95 比值 | 严格判断 |
| --- | --- | --- | --- | --- | --- | --- |
| default-small-dynamic50 | Normal | 1251 | 1228 | 1.018729641693811 | 1.0527194324662681 | measured-overhead |
| default-small-dynamic50 | Fixed | 1252 | 1226 | 1.0212071778140293 | 0.99780131920847492 | measured-overhead |
| finite-large-mask1-oneway | Normal | 272395 | 260984 | 1.0437229868497686 | 1.0440907032186741 | measured-overhead |
| finite-large-mask1-oneway | Fixed | 261708 | 271374.5 | 0.9643794829654223 | 0.9604935553229572 | pass |

实际进程允许 mask 均为4294967295。Fixed 设置主线程 mask4、进程 High；两个输入的双方逐帧 CPU 端点全部为2。Normal 设置主线程 mask4294967295、进程 Normal；默认输入的端点全部为2，有限类别输入的端点跨多个处理器，扩展20帧、手写22帧的前后端点发生变化。设置后的即时 CPU 与全部逐帧端点分别保存在 Device.Machine、RawSamples 和 [processors.csv](processors.csv)。端点记录表达这些观察边界的实际 CPU。

[队列边界](queue-boundaries.json)记录最后case提交04:20:28.4312118Z、独占准备完成04:20:29.8779915Z、首case开始04:21:22.8422864Z、末case完成04:23:45.3972015Z、首分析开始04:23:45.3979396Z。四项case和三个分析任务均以同一session执行并返回0。跨会话调度使本组执行间包含排队间隔。代理在末项Normal有限类别case结束前发起了队列状态读取；该实际活动与本次诊断记录一并保留。后续统一配置系列通过最终报告事件确定读取边界。

后续正式系列统一采用 Fixed，按同一规则记录32处理器进程范围、主线程CPU2、High优先级和原0/4worker配置。默认配对仍保留本次原精度判断，继续沿其声明读取与执行准备的数据形成定位。机器配置及分配观察范围见[测量配置](../../../ECS-Relations-Measurement-Configuration.md)。

原始submission中的CaseId为单元素JSON数组。导出与汇总显式读取该唯一字符串，保留原submission以及原始Native报告字节；实际Native输入参数和报告文件名与本表一致。

# 逐帧交替的首个受控 27 项

NativeAOT 对应源码 `8bd88b1dea3dad7508f822760a83c35836557c9e`。本轮只将采样顺序改为 round → frame → side：偶数 round 为 E/H，奇数 round 为 H/E。每侧仍为 16 帧预热、31×8 个真实单帧样本、7 次冷构造，FramesPerSample=1、SideSwitchIntervalFrames=1。共同静态工作区、CPU2、High 优先级、实际公共调用及独立手写算法沿 r27。

[完整原精度结果](summary.md)为 6/27 同时通过 median 与 p95：finite 6/18，default 0/9。27 项真实输出与消费结果均等价。[metrics.csv](metrics.csv)保存全部严格判定和实际吞吐量；[sample-tails.json](sample-tails.json)保存 21 项剩余比较的绝对微秒、GC、CPU 端点、round、side order 和 frame 分布。

默认九项 median 比值在 1.0190481153831121–1.0329585110507948。finite middle mask1 mutual 的 median/p95 为 1.0645942038096436/1.0519977369640303，large 同类为 1.1578234869830621/1.1378207371981459。该类当前真实输出为零，原始样本继续保留其实际候选读取与双向许可成本。

[队列边界](queue-boundaries.json)确认最后 case 提交为 05:13:45.4267296Z，准备结束为 05:13:46.7730671Z，首 case 开始为 05:13:46.7740034Z，末 case 结束为 05:16:40.4251726Z，分析开始为 05:16:40.4260681Z。测量持续 173.6511692 秒，准备、27 个 case、三个预排分析均退出 0，并使用同一显式 session。保护窗口在最终 cold.csv 事件后结束。

本轮与 [r27 块交替](../controlled-shared-r27/diagnostic.md)各自保留完整严格结果。[实际命令](../commands-controlled-paired-r28.json)包含发布、独占准备、分析和尾部审计的 result/stdout/stderr；每个 case 的原始三件记录由本目录 index.json 持有。

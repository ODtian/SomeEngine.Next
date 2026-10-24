# 共享工作区首轮与尾部记录

本轮源码为1906048b37581a883a665355b29c9c44642760ad，NativeAOT、Fixed、31轮×8帧及16帧预热。27个实际case均完成等价核对，严格通过18项：default6/9、finite12/18。[原精度表](summary.md)、[原始记录](index.json)、[共同工作区](workspaces.csv)与[各侧初始化](initialization.csv)分别保留完整数据。[队列审计](queue-boundaries.json)记录准备结束04:56:31.801981Z、首case开始04:56:31.8029685Z、末case结束05:00:08.8742942Z、首分析开始05:00:08.8749664Z，217.0713257秒；所有case及报告任务均返回0。

静态warm共同工作区由外层构造及释放一次。Workspaces记录实际World/输入/候选与pair/contact数组容量，算法各自的树、摘要、计数及成对作业输出资源列在各侧内存。冷样本和持续更新按各侧实际构造及维护。第一侧完整真实结果在第二侧运行前捕获，既有手写算法继续独立执行。

以下九项使用每侧原始248帧的median与R-7 p95。每项两侧各有13帧达到自身p95阈值，frame0表示每次换到该侧后的首帧。

| case | E median μs | H median μs | E p95 μs | H p95 μs | E frame0尾数 | H frame0尾数 |
| --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask4-oneway | 423.85 | 425.6 | 491.84 | 455.33 | 3 | 1 |
| finite-small-mask4-mutual | 428.5 | 430.8 | 497.13 | 469.07 | 2 | 1 |
| default-small-dynamic10 | 120.95 | 116.3 | 141.965 | 141.79 | 4 | 3 |
| default-small-dynamic90 | 134.9 | 136.3 | 182.76 | 165.73 | 1 | 0 |
| finite-middle-mask4-mutual | 3413 | 3441.4 | 4156.735 | 3662.515 | 2 | 2 |
| default-middle-dynamic10 | 1566.45 | 1601.5 | 1755.25 | 1744.2 | 1 | 1 |
| finite-large-mask1-oneway | 26175.9 | 26322.55 | 27352.88 | 27216.29 | 1 | 2 |
| finite-large-mask4-oneway | 28031.4 | 28300.05 | 34318.685 | 30198.07 | 5 | 0 |
| finite-large-mask8-mutual | 27251.75 | 26672.1 | 28311.415 | 27729.745 | 3 | 1 |

九项全部侧的Gen0/1/2均为0，CPU前后端点均为2。完整round、SideOrder、frame分布、每个frame的独立分布及原始tail行保存在[sample-tails.json](sample-tails.json)。finite-middle-mask4-mutual扩展侧round17的八帧及round18的三帧进入尾部；finite-large-mask4-oneway扩展侧round27占七帧、round28占三帧。连续区间与首帧数据分别记录，具体执行成本与采样观察范围继续依据当前原生代码及实际样本定位。

离线分析来自[Analyze-SampleTails.ps1](../../../../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Analyze-SampleTails.ps1)，公共CPU队列92923d32524f4912b5d1250d1cb7085c。原case判定沿其原始单帧采样保存。

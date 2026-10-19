# 大规模单向CPU采样

源码为0c267d975a26539d9b48ec7cff9bc47bc7d44b1c，使用既有Native二进制，case为finite-large-mask8-oneway。原measure/Source/Fixed/输入参数沿用[r36五案](../focused-native-r36/diagnostic.md)，诊断输出进入独立目录。Required=false，捕获期间耗时单列。

VSDiagnostics CpuUsageHigh以4000Hz附加唯一Guid session 4826459c-f9f6-4fce-90c7-d18ff59b90f3。目标进程95016于19:38:24.6090524Z开始、19:38:42.2177709Z退出0；attach于19:38:32.2062207Z返回0，stop于19:39:12.9309938Z返回0。实际采样覆盖见[cpu.diagsession](cpu.diagsession)，文件11,644,346B。生命周期和编译产物信息在[capture.json](capture.json)。

[analysis.json](analysis.json)记录解包和符号导出命令。实际ETL为sc.user_aux.etl，67,502,080B；xperf profile-detail和目标进程ReadEntityQueryRef butterfly均退出0。符号目录采用该Native exe/PDB目录，进程环境在导出结束恢复。

- [函数样本](cpu-functions-1.csv)
- [点读取调用栈](point-stacks-1.txt)
- [原诊断执行结果](diagnostic-measurement.json.gz)
- [完整命令、stdout、stderr和退出记录](commands.json.gz)

首个023fc任务在PowerShell布尔值初始化处退出1；d901使用超出工具范围的数值session，工具stderr报告有效整数范围0–255或Guid，整体记录TraceCreated=false。合法Guid的66dd实际退出0并形成文件，7e023后处理实际退出0。原失败回执与成功记录一并保存。

## 整个Query的调用栈

同一ETL按两侧完整Query分别导出：[关系Query](relation-query-stacks.html)、[手写Query](handwritten-query-stacks.html)。[导出记录](query-stack-export.json)保存实际符号筛选、时间与退出码，公共CPU任务为6d848d02f00842f097a8307558f03377。

| CPU样本归属 | 关系Query | 手写Query |
| --- | ---: | ---: |
| 整个Query | 28,639 | 28,526 |
| 点读取及其子调用 | 27,797（97.06%） | 27,564（96.63%） |
| ReadEntityQueryRef自身 | 11,433（39.92%） | 11,245（39.42%） |
| Dictionary.FindValue自身 | 8,605（30.05%） | 8,679（30.42%） |
| MatchesRow自身 | 2,196（7.67%） | 2,191（7.68%） |
| 候选循环与World回调自身 | 808（2.82%） | 944（3.31%） |

样本把主体执行成本归入两侧共用的成员点读取。逐候选指令对齐、CPU样本与真实计时分别保存；调用者的数据位置、分支行为与执行停顿仍是后续需要观测的成本来源。

## 原始逐帧分布

公共CPU任务90c04b945a3648f7991e85d09f4b487f于19:58:59.0784433–19:59:07.4962648Z执行并退出0，读取r35五案、r36五案和本次带采样L8one共11份既有报告，生成22个边界、44个侧分布及704个逐轮比较。两种实现各256帧，每种先后顺序各128帧；全部22个Query侧分布的Gen0/1/2变化帧数均为0。

R7计算沿用Analyze-SampleTails.ps1的排序与floor/ceil插值。Query沿RawSamples的Query边界读取ElapsedTicks；Consume从同一原始帧作本地浅复制，将ElapsedTicks设为ConsumeTicks，保留Round、SideOrder、Side及帧GC字段。手写统计值为0时比值记null，绝对统计值继续保留。首个7e4bd脚本按Consume边界过滤出了空组而退出1，其46097后继门控退出2；90c实际修正该数据投影并一次完成以下全部输出。

| 场景 | 来源 | 每轮median比值最小 | 每轮median比值中位 | 每轮median比值最大 | >1.01轮数 |
| --- | --- | ---: | ---: | ---: | ---: |
| S1mut | r35 a182 | 0.931636446 | 0.985547024 | 1.043048284 | 4/32 |
| S1mut | r36 0c | 0.963181704 | 0.995374087 | 1.044776119 | 6/32 |
| M8one | r35 a182 | 0.986954143 | 1.011509106 | 1.043088400 | 19/32 |
| M8one | r36 0c | 0.938220855 | 0.970188437 | 1.011454908 | 1/32 |
| L8one | r35 a182 | 0.986954998 | 1.004962788 | 1.019152044 | 4/32 |
| L8one | r36 0c | 0.998158958 | 1.014733556 | 1.041234739 | 24/32 |

每项顺序分层都使用同样的median/p95比值列，E表示关系实现，H表示手写实现。

| 场景 | 来源 | E先 median | E先 p95 | H先 median | H先 p95 |
| --- | --- | ---: | ---: | ---: | ---: |
| S1mut | r35 | 0.987026526 | 1.023302303 | 0.979548907 | 0.974079513 |
| S1mut | r36 | 0.994293685 | 0.995842844 | 0.997468995 | 1.039884214 |
| M8one | r35 | 1.011785651 | 1.014261764 | 1.017772407 | 1.009273037 |
| M8one | r36 | 0.971293158 | 0.967230101 | 0.963789385 | 0.971807039 |
| L8one | r35 | 1.005129266 | 1.017544170 | 1.004651278 | 1.004456192 |
| L8one | r36 | 1.019019274 | 1.032199426 | 1.011279668 | 1.045970032 |

S1mut在r36的整体mean比值为1.002478377，余项集中于p95和H先分层。M8one的r36改善贯穿两种顺序及前后半程。L8one的r36在24/32轮、两种顺序与两个半程持续右移。

同0c的带采样L8one实际计时如下；带采样执行采用独立诊断口径。

| 执行 | median E/H ticks | median比值 | p95 E/H ticks | p95比值 | mean比值 |
| --- | --- | ---: | --- | ---: | ---: |
| 原r36 | 274736.5 / 270386 | 1.0160899602790086 | 317240 / 307707.25 | 1.030979933037002 | 1.0189373529579604 |
| 同0c带采样 | 297180 / 294954 | 1.007546939522773 | 318187.75 / 317866 | 1.0010122189853585 | 1.0058532371365194 |

- [每侧绝对统计与GC](sample-distributions.csv)
- [各边界总体比较](sample-comparisons.csv)
- [逐轮median/p95/mean](round-ratios.csv)
- [先后顺序分层](order-distributions.csv)
- [前后16轮分层](half-distributions.csv)
- [完整分析及原报告指针](sample-analysis.json)
- [六组每轮分布与trace摘要](round-query-summary.json)
- [本次原命令、输出与退出事实](query-analysis-commands.json.gz)

### PMC capture and target stack coverage

The frozen 0c267 Native binary completed one independent large mask8 OneWay diagnostic with process 29672. The kernel collector started and stopped its own session successfully. Capture ba2963d687eb483caef769c19c9aa981 completed with ExitCode 0. Instructions, unhalted core cycles, retired branch misses and LLC misses used the recorded interval 65,536. The product source and benchmark inputs stayed on that binary.

Whole-machine PMC text conversion completed; the following symbol-expanded event dump was interrupted after 715.65625 CPU seconds. Its e5cefa7577b7415aa205cc5d69de6932 receipt retains ExitCode -1 and the precise process, command and stop times. Direct TraceEvent reading then completed in 3.544 seconds; the stack-based revision completed in 8.582 seconds.

| Coverage field | Original PID filter | Target StackWalk association |
| --- | ---: | ---: |
| Target PMC records | 1,067 | 1,067 |
| PMC records paired to target stacks | 1,057 | 1,057 |
| Matched PMC with a different public PID | 0 | 0 |
| Target stack fragments | 19,051 | 19,051 |
| ETW events lost | 0 | 0 |
| Run cycles G / H | 0 / 0 | 0 / 0 |
| Run instructions G / H | 0 / 0 | 0 / 0 |
| Run retired branch miss samples G / H | 5 / 2 | 5 / 2 |
| Run CPI | null | null |

The second reader retained all 3,651,628 PMC records before joining the target payload stacks by QPC and thread ID. Its matching result refutes the proposed early PID filtering loss. Whole-machine totals were 2,777,857 cycle records, 839,833 instruction records and 33,938 retired branch miss records; LLC miss records totaled zero. Target Run coverage supports the recorded sampling limitation. The five and two branch samples remain raw counts.

The [PMC evidence](pmc/preservation.json) preserves both target subsets, both summaries, both reader sources, capture and interval facts, and all five queue receipts with original stdout and stderr. Each copied target gzip was read completely before the four consumed whole-machine ETL/text files were deleted. [Raw read checks](pmc/raw-read-check.json) record compressed and decompressed sizes; [capture records](pmc/capture-records.json.gz) retain the original script text and per-stage facts; [queue records](pmc/commands.json.gz) retain the successful capture, interrupted conversion, compile warning and successful reader stages.

# 同源 H1/H2 控制

本系列使用源码 `0c267d975a26539d9b48ec7cff9bc47bc7d44b1c` 的同一Native产物。两侧共享实际输入及输出工作区，各自保持Count和算法对象；两端均构造同一种手写实现。每侧32×8真实帧、16warm、7对cold、同seed、Fixed CPU2，Query与Consume沿原每帧时钟边界。控制项Required=false，与正式E/H批准指标分列。

| 场景 | H1实际类型 | H2实际类型 | Query median H1/H2 | Query p95 H1/H2 | Consume median H1/H2 | Consume p95 H1/H2 |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| finite-small-mask1-mutual | HandwrittenMutualCandidatesSide | HandwrittenMutualCandidatesSide | 1.0062362435803376 | 1.0080805844587115 | 1 | 1.0833333333333333 |
| finite-large-mask8-oneway | HandwrittenOneWayCandidatesSide | HandwrittenOneWayCandidatesSide | 0.9993558334238597 | 0.9902885428676688 | 1.004595490007481 | 1.0323459459459459 |

两项实际输出等价、两个Native任务退出码0。原始报告和类型在[index.json](index.json)，已计算分布在[control-metrics.csv](control-metrics.csv)、[control-summary.md](control-summary.md)，正式同源E/H单独见[r36五案](../focused-native-r36/diagnostic.md)。本次同函数Query波动与E/H实际差值分别保存。

修正后的Master为 `972285673fae4459bf09806288228d97`；监听缓存两项清单后在19:19:33.2730591Z写ready。实际任务结束分别为19:19:33.9470751Z、19:19:49.9453421Z，最终分析 `64081869ce2544ee9b999d3598192351` 在19:19:53.9971608Z退出0。

首个Master `9377ffbe562740a598e25560f4c35e01` 使用CaseId而HH选择入口为HandwrittenControlCaseId，得到0个case并在组装清单处退出1。修正提交使用HandwrittenControlCaseId与HandwrittenControlsOnly；原回执与两次实际命令保存在[命令记录](../candidate-loop-commands.json.gz)。

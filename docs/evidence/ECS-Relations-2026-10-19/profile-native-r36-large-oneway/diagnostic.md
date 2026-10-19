# 大规模单向CPU采样

源码为0c267d975a26539d9b48ec7cff9bc47bc7d44b1c，使用既有Native二进制，case为finite-large-mask8-oneway。原measure/Source/Fixed/输入参数沿用[r36五案](../focused-native-r36/diagnostic.md)，诊断输出进入独立目录。Required=false，捕获期间耗时单列。

VSDiagnostics CpuUsageHigh以4000Hz附加唯一Guid session 4826459c-f9f6-4fce-90c7-d18ff59b90f3。目标进程95016于19:38:24.6090524Z开始、19:38:42.2177709Z退出0；attach于19:38:32.2062207Z返回0，stop于19:39:12.9309938Z返回0。实际采样覆盖见[cpu.diagsession](cpu.diagsession)，文件11,644,346B。生命周期和编译产物信息在[capture.json](capture.json)。

[analysis.json](analysis.json)记录解包和符号导出命令。实际ETL为sc.user_aux.etl，67,502,080B；xperf profile-detail和目标进程ReadEntityQueryRef butterfly均退出0。符号目录采用该Native exe/PDB目录，进程环境在导出结束恢复。

- [函数样本](cpu-functions-1.csv)
- [点读取调用栈](point-stacks-1.txt)
- [原诊断执行结果](diagnostic-measurement.json.gz)
- [完整命令、stdout、stderr和退出记录](commands.json.gz)

首个023fc任务在PowerShell布尔值初始化处退出1；d901使用超出工具范围的数值session，工具stderr报告有效整数范围0–255或Guid，整体记录TraceCreated=false。合法Guid的66dd实际退出0并形成文件，7e023后处理实际退出0。原失败回执与成功记录一并保存。
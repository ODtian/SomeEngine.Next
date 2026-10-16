# Performance Evidence

性能结论必须对应明确 workload、设备、backend、构建配置与计时边界；证据页面可以按日期保存，但不得把一次 benchmark 的实现选择提升成长期架构规则。

当一个算法选择明确依赖性能数据时，architecture 页面只固定语义约束，具体候选和结论链接到相应 evidence/benchmark 记录。

## 串行测量

CPU、GPU benchmark 通过 `tools/Invoke-Benchmark.ps1 <原命令及参数>` 提交到公共 FIFO 队列。
同一用户的工作区共用执行器，命令按提交顺序运行；比较期间需要独占 CPU/GPU 的构建、内容导入与捕获也采用这一入口。

```powershell
./tools/Invoke-Benchmark.ps1 dotnet run --no-build --project tools/SomeEngine.Animation.Checks -c Release -- --benchmark
```

提交返回任务编号和 `artifacts/benchmark-queue/<编号>/` 结果目录，自动启动隐藏的后台执行器。
目录保存 `stdout.log`、`stderr.log` 与 `result.json`，结果包含命令、工作目录、起止时间及退出码。
测量使用提交时的环境变量和参数分组。原始测量证据随对应报告保存在 `wiki/evidence/`。

参见 [[evidence/RHI/Performance-Evidence-2026-08-20]] · [[architecture/cluster/Deform-Cache-Allocation]].

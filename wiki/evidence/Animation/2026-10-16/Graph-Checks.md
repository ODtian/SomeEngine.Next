# Core and graph public behavior

日期：2026-10-16。配置：Windows x64、.NET 10、Release。全部命令通过公共 FIFO，复用 core 工作树的编译产物。

| 范围 | 队列 | 结果 | 原始记录 |
| --- | --- | --- | --- |
| Motion、Timeline、ValueOps、TypedGraph | `01c002753d7a438b89c8ff24354c7e7e` | 27 项通过，规则 0 项违规，退出码 0 | [stdout](core-typed-graph.stdout.txt)、[stderr](core-typed-graph.stderr.txt)、[result](core-typed-graph.result.json) |
| 既有公开图编排 | `596e263075544938b03ffc9912204152` | 17 项通过，销毁句柄清理 1 项定位到已移除的条件状态缓冲 | [stdout](graph-compatibility.stdout.txt)、[stderr](graph-compatibility.stderr.txt)、[result](graph-compatibility.result.json) |
| 销毁句柄修复后的专项 | `956116ffcd8044deb491f5d2a0900856` | 1 项通过，规则 0 项违规，退出码 0 | [stdout](graph-cleanup.stdout.txt)、[stderr](graph-cleanup.stderr.txt)、[result](graph-cleanup.result.json) |

类型化检查覆盖跨算法区间、消费者收敛、共享定义的独立实例时钟、实例公式参数更新、固定截止时间重定目标、用户条件和公开叶节点回收。既有检查覆盖顺序、延迟、弹簧、并行开放阶段、独立分支、内联与池化、状态机淡入、Blend1D、多来源及容量拆分后的共同控制。

实体销毁时 ECS 归还普通条件状态缓冲，图清理组件归还共享定义引用。存活实例在停止、替换和系统释放时执行其条件状态释放路径。冷期构建与播放的成本由独立性能入口记录，行为检查保留字段结果与实体复用断言。

```powershell
& 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1' dotnet test tests/SomeEngine.Animation.Tests/SomeEngine.Animation.Tests.csproj -c Release '--filter=FullyQualifiedName~TypedGraphTests|FullyQualifiedName~MotionTests|FullyQualifiedName~TimelineTests|FullyQualifiedName~ValueOpsTests' --disable-build-servers '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false' '--property:PublishAot=false'

& 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1' dotnet test tests/SomeEngine.Animation.Tests/SomeEngine.Animation.Tests.csproj -c Release '--filter=FullyQualifiedName~AnimationGraphTests' --disable-build-servers '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false' '--property:PublishAot=false'

& 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1' dotnet test tests/SomeEngine.Animation.Tests/SomeEngine.Animation.Tests.csproj -c Release '--filter=FullyQualifiedName~DestroyedHandlesFinishCleanupWhileSharedInstancesContinue' --disable-build-servers '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false' '--property:PublishAot=false'
```

# Declared view motion allocations

日期：2026-10-17。配置：Windows x64、.NET 10、Release、Checked WorldJobs、零 worker。所有构建与测量经公共 FIFO，复用 core 工作树和既有编译目录。

公开检查先为同一个引用字段声明完整泛型 Linear、完整泛型 CubicOut 和便利 Tween Spec。在持久 `Scope.Compute` 中重复选择算法并调用 `Scope.Set`，每帧依次执行 State 提交、Views 和 Animation。预热 128 帧后记录 1,000 次完整帧的当前线程分配；诊断数组在计数前创建。三种选择的样本数分别为 333、333、334。

| 阶段 | FIFO | 结果 | 原始记录 |
| --- | --- | --- | --- |
| State / Views / Animation 分段 | `be7637453300460e8561bc4f77b4e642` | 1,000 帧合计 96,000 B；每种选择的 Views 均为 96 B/帧，State 与 Animation 均为 0 B | [stdout](view-switch-phases.stdout.txt)、[stderr](view-switch-phases.stderr.txt)、[命令与结果](view-switch-phases.result.json) |
| 声明 / Set / 重建次数归因 | `e06b3808f033424cb0e397295142506d` | 声明与 Set 均为 0 B；每帧 Build 与 Compute 各 1 次；稳定输入的 Views 更新为 0 B | [stdout](view-switch-builds.stdout.txt)、[stderr](view-switch-builds.stderr.txt)、[命令与结果](view-switch-builds.result.json) |
| 最终公开行为与分配 | `5f0c2587378147ca8e6d72ae1e289c3b` | 完整帧合计 0 B；默认公开消费者通过；Views 11 项与呈现恢复 4 项合计 15/15 通过 | [stdout](view-switch-final.stdout.txt)、[stderr](view-switch-final.stderr.txt)、[命令与结果](view-switch-final.result.json) |

三次任务退出码均为 0，构建均为 0 警告、0 错误。最终验证时间为 UTC 17:44:11–17:46:23。

被动画写入的 `AnimatedPanel` 同时作为 `IView`，引用叶写入更新其参数版本，推动每帧重建。原路径在 `ScopeData.BeginChildren` 创建 `ChildOrder`，在 `Scope.Enter` 创建 `ChildComputations`；这两份回滚记录的 x64 对象布局各为 48 B。交付分支的 `05015331` 将两份记录交由外层 `ScopeData` 重复 Capture，沿共享保留计数完成后清除 World、旧计算列表及子实体数组引用。修复后的测量保留每帧一次 Build 和一次 Compute，完整路径分配降至 0 B。

字段的算法声明集合由 `AnimatedField` 持有；具体分发函数直接访问当前选择。重新选择已声明的完整泛型或便利 Spec 复用既有声明对象。交付分支的诊断入口为 `c5c7a71b`，最终动画批次源码包含 `11da31ce`。

| 最终选择 | 样本 | State B | Views B | Animation B | Declare B | Set B | Build | Compute |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Linear | 333 | 0 | 0 | 0 | 0 | 0 | 333 | 333 |
| CubicOut | 333 | 0 | 0 | 0 | 0 | 0 | 333 | 333 |
| 便利 Tween | 334 | 0 | 0 | 0 | 0 | 0 | 334 | 334 |

稳定输入的 Views 单次更新记录为 0 B、Build 0、Compute 0。固定 64 卡的 HUD 负载与实测另见 [HUD workload](Hud-Workload.md) 和 [Interface execution](Interface-Execution.md)。

最终任务在仓库根目录执行以下命令；完整工作目录、参数和 UTC 时间保存在上表的结果文件中。

```powershell
$viewValidation = @'
& dotnet build tools/SomeEngine.Animation.Checks/SomeEngine.Animation.Checks.csproj -c Release -v quiet "-m:1" "-nodeReuse:false" "--property:UseSharedCompilation=false" "--property:PublishAot=false"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.exe
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.exe --view-switch-allocations
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Release --filter "FullyQualifiedName~TypedAnimationTests|FullyQualifiedName~AsyncBuildTests.ReadersFollowPresented" -v quiet "-m:1" "-nodeReuse:false" "--property:UseSharedCompilation=false"
exit $LASTEXITCODE
'@
& 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1' pwsh -NoProfile -Command $viewValidation
```

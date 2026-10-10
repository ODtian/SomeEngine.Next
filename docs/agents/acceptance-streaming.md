# Cluster 流送驻留验收

规格：[#189](https://github.com/ODtian/SomeEngine.Next/issues/189)、[#195](https://github.com/ODtian/SomeEngine.Next/issues/195)。本页归并几何流送的实际行为结果；Vulkan、阴影 RT 质量与 #190 性能分别保留其验收状态。

## 已有行为结果

原证据目录：

`F:/AI-Workspaces/session-3884061aa3d25950/work/streaming/artifacts/acceptance/streaming-189/`

| 记录 | 已验证范围 | 结果 | 对应源码 |
| --- | --- | --- | --- |
| streaming-demand-green.trx | 可见驻留保护、完整/截断需求、依赖补齐、优先级、粗细交接与 Deferred 恢复 | 9/9 | 986c17dc，集成 070e5123 |
| streaming-cycle-green.trx | 上述行为及冷 parent 环整体释放、重新加载 | 10/10 | d567b100，集成 88c6dfea |
| streaming-external-dependency-green.trx | component 外驻留细节保护其冷 parent 页 | 1/1 | 187f2613，集成 3d6c47df |
| prepare-demand-optimization-green.trx | 前述 11 项及新网格注册改变流送容量、新场景预算独立 | 12/12 | 1c72ccf2，集成 74f6add4 |

最后一批为 12 个不同用例的一次实际运行，不能把前面重叠批次相加。可见驻留、缺失同组依赖和冷环各自的红→绿记录保留在同目录。

12 项原 TRX 的实际 SHA256 已与 `evidence/performance/prepare-demand-optimization-identity.json` 核对一致：

`de104f013e9ad1309fa06c7f05452290ac984ec58c424d47af42fb21cbf20dbc`

该次使用既有匹配的 integration DefaultProject 内容。旧本地 cook 缺少 CompactClusteredLightGrid 的初始化失败单独记于 `prepare-demand-initialization-blocked.*`，不作为流送回归。内容、程序集及临时夹具路径的恢复见同 session 的 `evidence/performance/prepare-demand-content-identity.json` 与 `prepare-demand-optimization-review.md`。

1c72ccf2 到集成 5f4706e6 的整个 SomeEngine.Render.Cluster、流送反馈类、叶标志接合类及 GeometryStreamingHandoff/Priority 测试源码无差异。已有结果在这一核对范围复用；后续图、shader、backend 的独立变更不据此宣布通过。

## 新增跨驻留 epoch 行为

2026-10-10 UTC，在基点 `7b62617ccac6a42a13e990ed3b7578bd4943f864` 上新增：

`GeometryStreamingEpochTests.FeedbackFromDisposedSceneIsRejectedBeforeNewSceneRenders`

测试通过公共场景启动与真实 GPU 回读形成旧 packet。旧场景留下尚未消费的反馈后释放；新场景复用公开 ClusterStreamingFeedbackChannel，消费时必须抛出 ArgumentException，参数名为 ticket。随后新场景最终颜色、深度与相同内容的参考图一致。夹具仅增加可选的公开 channel 参数，未暴露生产内部状态、未伪造 ticket、未改变生产实现。

- D3D12 硬件单例：1/1 通过，0 跳过，10.0025703 秒。
- 默认测试入口的行为规则检查：0 违规。
- 进程终态：2026-10-10 12:49:23.636 UTC，exit 0，无 timeout。
- 测试编译预检、正常项目增量构建、恢复夹具路径后的最终构建分别为 7.38 / 9.95 / 8.98 秒，均 0 警告、0 错误。
- 未重跑已有 12 项；本次为独立增加的 1 项，未声称 13 项同批通过。

执行时只读既有 integration DefaultProject cook，没有复制、重建内容或新建输出树。catalog SHA256 为 `b0bb292319583d6c8b866e63152d7444944f4b137dae8056a36415f4c84e5272`；81 项实际资产哈希见 `streaming-epoch-content-identity.json`。临时绝对内容路径已撤回，原路径恢复后的最终测试程序集已正常重编；没有对已知旧本地 cook 重复运行用例。

实际执行的 TRX SHA256：

`c6b60e1653271164d0ef6b0bf8b0bddb62652bd303d1fc496cd3243462f41993`

完整证据为上述原目录中的 `streaming-epoch-green.{log,trx}`、`streaming-epoch-{start,exit,health,execution-identity,content-identity}.json` 及三份编译记录。运行时另一路的 FrameRecording marker 诊断代码处于关闭状态；保留的 App 五个二进制哈希在所有构建前后均未变化。健康交接确认 RTX 3080 状态 OK、ConfigManagerErrorCode 0，无 dotnet/testhost/Game.App 残留。

## 新增乱序回调与旧 fault 行为

在基点 `80ac99a153d8318b1ef3d438800dd287c86205d5` 上新增：

`GeometryStreamingOrderTests.OlderFaultsLoadWithoutReplacingTheNewestViewsResidentDetail`

测试从 GameWorld 场景与真实 GPU fault/demand 回读进入生产流送链。三个常驻粗页与两个细页的预算内，先加载可见 A 与随后变冷的 D；旧帧 2/3 显示缺两细页的 B，最新帧 4 只显示 A。两个公开 ExternalTimeline 和不属于图的第二 graphics queue 控制三帧资源的复用。实际图回读 FrameIdentity 为 **11、14、12、13**，即相对帧顺序 **1、4、2、3**，没有伪造反馈 packet 或序号。

收到该批反馈后暂停后续 Feedback region，独立图像回读仍逐帧运行，避免更晚反馈重新保护 A 而掩盖旧 demand 覆盖问题。B 的真实 IRangeSource 完成两个不同细页范围读取（offset 4370、4644），证明旧 fault 仍处理；推进旧页读取与发布的 27 帧中，A 的完整颜色、深度均与完整驻留参考图一致；其中最后 3 帧在确认两次来源读取完成之后。预算要求旧页读入释放冷页，新视图的细节必须持续受保护。

- D3D12 硬件单例：1/1 通过，0 跳过，11.9724125 秒；默认测试入口行为规则 0 违规。
- 进程终态：2026-10-10 13:42:13.101 UTC，exit 0，无 timeout。
- 纯测试编译预检 7.68 秒、正常 -m:1 增量 9.63 秒，均 0 警告、0 错误。
- 临时内容路径已恢复；默认路径最终正常 -m:1 增量于 13:48:32.498 UTC 完成，9.34 秒，0 警告、0 错误。没有对已知旧本地 cook 重跑 GPU 用例。
- 未重跑前述 12 项或 epoch 单例；现在为已有 12 项加两个分别运行的新行为，不声称 14 项同批通过。

两 gate 与独立信号队列在首个等待提交前完成构造。异常 finally 分别尝试释放两 gate，任一释放失败不阻断另一条；等待 gate 提交与全部已追踪帧（含部分提交）后才销毁 fence。正常逐帧 Render 仍按提交、等待、收集执行。所有夹具扩展位于测试项目，未暴露生产内部 API。

执行前既有 integration cook 的 catalog 与全部 81 资产逐项匹配 epoch 身份。实际执行程序集、临时路径及恢复哈希见 `streaming-order-execution-identity.json`、`streaming-order-content-identity.json` 和 `streaming-order-fixture-content-identity.json`。临时 marker/support-query 诊断在本例进程中均关闭，五个锁定 App 二进制在构建、运行前后保持原 SHA。2026-10-10 13:43:13 UTC RTX 3080 状态 OK、ConfigManagerErrorCode 0，本次 dotnet/testhost/监控进程均已退出。

实际 TRX：`streaming-order-01.trx`，SHA256：

`5a7599e2a27a14cb99ee677ce2494ac9902da2767819d703895a460ee4b1e5df`

日志、启动、退出与健康记录为原证据目录的 `streaming-order-01.log`、`streaming-order-01-{start,exit,health}.json`。这是功能验收，不作为 #190 性能通过或 Nsight 捕获成功的证据。

## 仍属源码审查的边界

- **多页卸载中途异常：** ClusterMeshes.StagePageEvictions 在任何失败出口取消全部已暂存成员；当前 demand、常驻粗页及 component 外可用子页继续阻止卸载。页空间仍按原读者完成边界归还。尚未执行公共入口的中途分配异常注入，不为此添加测试专用生产接口。

## 既存失败策略差异

Streaming-Failure.md 要求不可读取页进入终止失败状态。当前 PageStream.RecordFailure 将 SourceReadFailed 排除在终止集合之外；无效内容和永久容量失败则进入集合。MeshPayloadSource.ReadIntoAsync 只复用成功缓存，其他情况重新打开并读取来源；AssetByteCache 在失败后移除共享读取，符合其自身允许重试的规则。该行为早于本轮 demand/SCC 修改。本次仅记录此差异，没有改变失败策略，也没有把源码推导写成新增运行测试通过。

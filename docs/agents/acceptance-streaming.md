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

## SourceReadFailed 设计澄清与公开行为

旧 Streaming-Failure.md 的“不可读取页一律终止”来自 19da2ae92（2026-09-02）。后续交付记录 Render-Delivery-2026-09-09.md 的 B36 明确把“所有页面读取失败都永久拉黑”列为缺陷，提交 4b87345c648254bd73fa6b1ff2255677e9710b22 在 PageStream.RecordFailure 中排除 SourceReadFailed，允许后续 fault/demand 重新请求。当前策略是这一有意修复的延续；不是本轮 demand/SCC 引入的功能回归。

文档修正独立提交 d62849dd：认证、格式与解码的 InvalidPayload 仍是终止失败；普通来源异常没有瞬时/永久 I/O 分类。持续 SourceReadFailed 仍可随新反馈重试，目前没有单页退避或重试上限。没有擅自改变这一兼容策略。

新增 GeometryStreamingSourceFailureTests 两个用例，从公共 AssetLoader / IRangeSource 到真实 D3D12 场景图像观察：

- ASourceReadFailureKeepsCoarseCoverageUntilALaterRequestRecoversDetail：第一次 detail 读取抛 IOException；第2次读取显式挂起，5帧颜色/深度保持已证实非空的粗层。释放读取后，图像与完整细节参考场景相同，后续3帧保持；仅2次 detail 请求，其中1次成功。1024字节页堆内恢复说明失败没有永久占住后续页面所需容量。
- RepeatedSourceFailuresKeepCoarseCoverageAndSceneDisposalCancelsTheNextRead：连续3次 IOException 后仍产生第4次 detail 请求；7帧粗层完整颜色/深度不变。第4读等待真实来源取消 token，Scene.Dispose 取消并等待读终态，最终在途 detail 读与来源句柄计数均为0。

两例使用每用例自有来源包装和显式同步；没有生产内部接口、反射、失败消息断言或睡眠。资源计数观察的是测试所提供的公共来源，不是全引擎/GPU堆泄漏测量。无效内容黑名单及多页卸载中途分配异常仍不能由这两例宣布通过。

本次实际结果：2/2、0跳过；持续失败例7.5742598秒，恢复例5.9901940秒；默认测试规则0违规。dotnet进程于2026-10-10 14:40:12.904 UTC启动、14:40:44.982 UTC以exit0退出，无timeout。

排槽交接消息曾与另一条VSM正确性运行交叉。本次两例实际GPU用例窗口与VSM MaterialRanges（14:40:28.929–14:40:50.830 UTC）重叠，真实结果保留为披露这一条件的功能证据；不作为GPU独占或性能证据，不为重复计数重跑。具体事件记录见 streaming-source-failure-01-scheduling.json。14:41:32 UTC健康检查：RTX3080状态OK、ConfigManagerErrorCode 0，无dotnet/testhost/Game.App或本次监控进程残留。

执行使用既有 integration DefaultProject cook，catalog b0bb292319583d6c8b866e63152d7444944f4b137dae8056a36415f4c84e5272 和81资产全部与之前身份一致，没有新cook或复制。仅新测试的3个 contentRoot 实参临时指向该内容，执行后全部撤回。首次正常构建43.15秒、默认路径恢复后的最终正常构建9.29秒，均0警错；最终构建于14:43:19.190 UTC退出，testDLL为030ea7be74e53223048222cdd833ee86215d2cb13bf5c13f97d9241034dd429c，App五SHA在本次收尾前后不变。另一路的可选 traversalShader 未启用，现场BVH候选shader没有进入这两例。执行testDLL SHA256为9f2421fe78a8bc30f1cb2c213521aac5504e464d1a05dc81fab381ef6d4148ce。

实际TRX streaming-source-failure-01.trx，SHA256：

d813391a0b0c3782e0af8dffcd4c3f3232d2ec57f86979feb1c7c7be3b5f7adc

日志、启动/退出、健康、内容、程序集和临时实参恢复记录均在原验收目录的 streaming-source-failure-*。这些结果只新增失败恢复与关闭边界，没有重跑此前12项、epoch或乱序例。

## InvalidPayload 终止失败的公开行为

2026-10-10 UTC，在 streaming 基点 `791e70aec7da9030f16326e854f30f4e398ec9a3` 上新增：

`GeometryStreamingInvalidPageTests.ACorruptDetailPageKeepsCoarseCoverageWithoutBlockingHealthyPages`

测试由公共 AssetLoader / IRangeSource 提供两个真实文件网格。第一网格只在细页读取时改动一个实际来源字节，由正常摘要认证产生 InvalidDataException；保留最低粗层。随后同一场景加入健康网格，单并发加载预算仍能把其细页加载完成。4096 字节页堆足够容纳两网格的粗、细页，不能靠容量不足抑制坏页重试。

坏页粗层与健康细层的完整颜色、深度，与直接构造相同几何状态的参考图一致。细化后连续 12 帧仍只有一次坏页自动读取。再通过公共 ClusterMeshBlock.LoadPagesAsync 显式加载坏页，来源计数确实增加为 2，正常抛出 InvalidDataException；后续 3 帧图像与计数继续不变。显式读证明来源可再次被调用，健康细页已完成则证明唯一加载名额已释放，避免把未完成的 I/O 误认作终止策略。两来源在释放后句柄数均为 0。断言没有依赖生产内部状态或异常消息。

- 实际 D3D12 硬件用例于 19:42:28.2505298–19:42:34.2234963 UTC 执行，报告时长 5.9726874 秒，通过；规则检查 0 违规。
- 复用 HiZ 候选的同一次普通 Cluster.Tests 增量构建（14.38 秒，0 警错）与本树 DefaultProject 正常 cook，无临时内容路径、额外输出树或重复构建。
- 与 HiZ 两行按 OR filter 同进程执行，19:41:57.459–19:42:34.500 UTC，37.041 秒，exit 0、无 timeout。此处只新增 1 个流送行为；原 16 个分别运行的有效行为未重跑，不声称 17 项同批。
- 执行 TestDLL SHA256：`c2f4be3151ccd67a89d185cf514f0308e679c0f18cc1b4aa6221d8f1ee1b76db`；新增测试源码 SHA256：`14e89ab251ee5099c7ec10f653c248dc78d189c68b80e2d7760b553e7f3a88bf`。
- 正常 cook 的 81 资产 catalog SHA256：`f98ef63b03fc4275b566a86156f717372ad7cf95d8728117f8d116caacfe436e`。该内容刷新及 HiZ 候选不同于旧批次身份；不据这一个新用例追认其它 shader/backend 或所有旧用例均已复测。
- 19:44:03 UTC 收尾记录中原 dotnet/testhost 均已退出，RTX 3080 可正常查询。

本批原证据在同 session 的 `evidence/performance/hiz-static-pages-frame-public-01.{trx,log}`、`-start.json`、`-exit.json`、`-health.json`；普通构建与内容身份在 `hiz-static-pages-fullgraph-ready-identity.json`。原 TRX SHA256：

`5e8351ba6e9af43b57af7a3c299fc7379b8539530746c131a24e89ed77f96765`

### 本轮流送收口范围

SCC 冷环释放/再加载、component 外依赖保护、粗细完整交接、需求优先级与容量变化，复用已核 SHA 的 12 项原结果；跨驻留 epoch、乱序反馈各复用独立结果；SourceReadFailed 恢复与关闭复用原 2 项结果并保留其并行 GPU 窗口披露。本轮补齐 InvalidPayload 的终止自动重读与其它页继续推进。上述流送生产源码自 `1c72ccf2` 到 `791e70ae` 没有差异，本轮没有修改失败策略。

仍缺公共行为证据的是多页卸载中途分配异常的整体回滚。当前公开 API 无确定性触发该异常的入口，保留前述源码审查边界，不为验收暴露生产内部接口。本页不代替 #189 的其它渲染正确性验收或 #190 性能验收。


## 2026-10-11 当前工作树失败处理复验

在现有 streaming 工作树 `e4782239d679c29697de55478bcb4fd15dff9707` 上完成一次当前程序集复验。保留既有未提交 HiZ/caster 变更、测试辅助及 import 元数据；本次没有修改生产或测试源码，也没有新增故障注入接口。

- 普通 Release 增量构建：`dotnet build tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-restore -m:1`，7.59 秒，0 警告、0 错误。
- 原有 InvalidPage 与 SourceFailure 两个测试类一次正常 `dotnet test --no-build --no-restore`，**3/3 通过、0 失败、0 跳过**，默认行为规则检查 0 违规。
- 坏页终止自动重读/健康页继续推进 12.4100423 秒；持续来源失败/关闭取消 3.8829640 秒；来源失败后细节恢复 6.2011415 秒。三项保留完整颜色与深度断言、原容量和来源计数要求。
- 进程窗口 2026-10-11 06:20:56.277–06:21:39.611 UTC，exit 0，无 timeout。使用协调后的 GPU 窗口；开始前无 testhost/Game.App，结束后 06:21:58.829 UTC 再查仍为 0，随后释放 GPU 窗口。本轮不是性能测量。
- 运行前后核对 69 个流送核心/接合/测试源码、26 个执行程序集和 81 个既有内容资产，均无变化；整个既有 dirty diff 的 SHA256 也保持。未重建 cook、改内容路径或添加输出树。
- TestDLL SHA256：`0dfc9f541113bb35975ed53117580b551650353194e95e60e5e7f1aeffff13cd`；catalog SHA256：`f98ef63b03fc4275b566a86156f717372ad7cf95d8728117f8d116caacfe436e`。
- 本次 TRX SHA256：`83c8a0e00e7431f73a7c55fcbef6362c27126506f53481bbd2e4398a75ec994f`。日志、构建、启动/退出、进程终态、源码与执行内容身份、最终核对记录保存在本页原证据目录的 `streaming-current-closure*`。

同时重新计算前述 12 项、epoch、乱序、来源失败 2 项及坏页 1 项五份原 TRX 的 SHA256，全部与本页记载一致。`src/SomeEngine.Render.Cluster` 及流送反馈接合从 `1c72ccf2` 到当前工作树没有源码差异。历史结果继续按其原范围使用；本次是重测已有 3 项，不增加独立行为数量，不声称全部 17 项在当前程序集同批通过。

多页卸载中途托管分配异常仍限于源码审查：`ClusterMeshes.StagePageEvictions` 的 finally 撤回全部已暂存成员；`MeshPages.StageEviction` 在修改该成员状态前扩容，取消使用已存在的链表节点，BVH patch checkpoint 回滚不分配。当前公共场景入口仍无安全且确定性的中途分配失败触发点，未运行该故障注入，也未把源读取失败测试当成它的替代。本轮已复验坏页、来源重试、恢复和关闭范围；不据此关闭 #189 全部渲染正确性或 #190 性能事项。


## 2026-10-11 全部 17 项同批复验

在同一 `e4782239` 工作树、相同 TestDLL 与本地 DefaultProject 内容上，把前述历史 17 项一次运行。此节更新当前覆盖状态；上节 3 项结果保留为先前那次独立运行的记录。

- 选择 `FullyQualifiedName~GeometryStreaming`；先由正常 `dotnet test --list-tests --no-build --no-restore` 核对实际程序集，再与五份历史 TRX 逐个测试名称比较。发现与最终执行均为 17 项，缺失、额外和重复均为 0。
- 当前同批 **17/17 通过，0 失败、0 跳过、0 超时**；默认行为规则 0 违规。包括 Handoff 11 项、Priority 1 项、Epoch 1 项、Order 1 项、SourceFailure 2 项、InvalidPage 1 项。
- 使用前述已通过的正常增量构建，没有重复构建。进程 PID 42676 于 2026-10-11 07:47:58.975 UTC 启动、07:50:00.420 UTC 以 exit 0 结束，121.445 秒；测试报告时长 1 分 43 秒。GPU 窗口经过协调，执行后确认该进程、testhost 与 Game.App 均已退出，并已释放窗口。
- Code Tools 请求在自己的等待上限返回 transport timeout；已落盘的正常进程退出记录、完整日志和 TRX 共同证明测试未超时且已成功结束。没有因为工具等待超时重新运行测试。
- 执行前后 69 个相关源码、26 个程序集与 81 个内容资产逐项 SHA256 相同，原有未提交变更完整保留。8 类测试临时目录在前后检查中均为 0。没有生产/测试源码修改、cook、内容路径替换或额外程序集输出树。
- TestDLL 和 catalog SHA256 与上节相同。本批 TRX SHA256：`33179c7e8e110f87bf496028402fe5a89e700811e5b4320918c9233c4f3405c2`。
- 完整证据在本页原目录的 `streaming-full-current*`：baseline、selection、discovery、preflight、start、exit、log、trx 与 verification。verification 保存每项结果、源文件/程序集/资产变化集合及临时目录检查；变化集合均为空。

这些用例通过正常本地 DefaultProject ApplicationSettings、ClusterShaders、材质与 shader 加载，使用公开 ClusterBuilder/MeshAsset/AssetLoader/IRangeSource 和 GameWorld→RenderingWorld→RenderGraph→颜色/深度回读链。自给几何与真实文件来源保留；实际范围是 D3D12、64×64、ForceHardwareRaster 的流送行为，并非完整应用三场景、Vulkan、VSM RT 或性能验收。来源优先级例观察真实读取顺序，其余按各测试原有图像、异常、反馈和来源终态断言执行，未改容量、时间边界或判据。

本批解决“17 项只有分批历史结果”的覆盖缺口；多页卸载中途托管分配异常仍保持前述源码审查边界，没有执行不安全 OOM 注入或增加测试专用生产接口。

### 最终集成携带范围

07:41 UTC 只读核对既有 integration `d7c14d9b4ea6e707acff0dbf0aa9b288693b3c17` 时，33 个流送生产/接合/测试/文档文件中，生产与全部 17 项测试逻辑均已存在。epoch、乱序、来源失败及坏页提交分别已作为 `12a866c2`、`4bca9024`、`c7bf5211`、`fdd37865` 集成；坏页文件仅换行差异，不应重复重放旧提交。

仅需携带本轮验收文档增量及证据。FrameStageScene 的 `SetMeshDepthCacheClass` 辅助方法是独立 caster-cache 候选所需，归对应性能变更管理。最终集成在代码、内容与方法学稳定后可使用相同 17 项 filter 复验；本批结果不得替代尚未执行的最终集成运行。

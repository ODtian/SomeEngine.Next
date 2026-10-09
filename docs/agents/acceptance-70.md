# #70 RenderGraph 与帧资源验收

> 执行源码：48b9d68ea810f1c423854328387313bd6ef99b1e。2026-10-09 本地验证完成；CI 因账户额度无法启动，用户已明确授权跳过 CI 合并。Vulkan Presentation 能力跳过与未执行的独立测试均如实保留。

## 范围与交付

本次按 [#70 当前规格](https://github.com/ODtian/SomeEngine.Next/issues/70) 验收区段树、当帧区段、帧阶段骨架、帧上传/持久回读、实例存储及 CPU 渲染数据读写锁，实现票为 #71–#80。采用已保存的公开行为证据，并对本次实际修改做必要验证，符合 `docs/agents/acceptance-70.md` 的现行规则。

- 原交付为 [PR #136](https://github.com/ODtian/SomeEngine.Next/pull/136)，目标保留 `codex/someui-base-7b03495`（远端 `be6be444f639d9e786914d4b3661f8b8e9441bd0`）。
- 本次验证前 PR 远端 head 为 `96b7329812d089a0e270d28514f9dcd11b5762b0`；本次验证源码为 `48b9d68ea810f1c423854328387313bd6ef99b1e`。不能把候选验证直接称为远端 head 的新增验证。
- VSM/UE 对齐的 [#189](https://github.com/ODtian/SomeEngine.Next/issues/189)，性能的 [#190](https://github.com/ODtian/SomeEngine.Next/issues/190) 与 [#81](https://github.com/ODtian/SomeEngine.Next/issues/81) 独立推进。本次不声明其完成，也不以其新增目标阻塞 #70。

## 复用的历史行为证据

[#71](https://github.com/ODtian/SomeEngine.Next/issues/71)、[#72](https://github.com/ODtian/SomeEngine.Next/issues/72)、[#73](https://github.com/ODtian/SomeEngine.Next/issues/73)、[#74](https://github.com/ODtian/SomeEngine.Next/issues/74)、[#75](https://github.com/ODtian/SomeEngine.Next/issues/75)、[#76](https://github.com/ODtian/SomeEngine.Next/issues/76)、[#77](https://github.com/ODtian/SomeEngine.Next/issues/77)、[#78](https://github.com/ODtian/SomeEngine.Next/issues/78) 均已关闭，复用其原交付，不要求为了增加计数重跑未改动的历史组合。

历史交付声明与本次实际读取的原始结果分别如下：

- 材质增删替换：[#75 交付评论](https://github.com/ODtian/SomeEngine.Next/issues/75#issuecomment-5973805049) 记录 D3D12/Vulkan、硬件/软件光栅的颜色和深度与全新安装逐字节相同，Cluster 51、RenderGraph 171、局部分析 8 项通过。
- 层级加深首帧及其它阶段：[#76 交付评论](https://github.com/ODtian/SomeEngine.Next/issues/76#issuecomment-5973847681) 记录五项验收通过，Cluster 64、RenderGraph 174 项通过，交付 `f5a59f3f3849cecc0cfee445eb1582d29389c799`。当前仍有 `StageRegionRenderingTests.AddedHierarchyLevelsProduceGeometryInTheirFirstFrame`。本次未定位该历史运行的原始 TRX，不将交付声明冒称新执行结果。
- 上传、取消及持久回读：[#77 交付评论](https://github.com/ODtian/SomeEngine.Next/issues/77#issuecomment-5973849457) 记录 13 项新增 WARP 行为，以及 Debug 161/Release 155 项图测试通过。
- Cluster 延迟消费、指标/epoch、标量下一帧和相机遮挡恢复：[#78 交付评论](https://github.com/ODtian/SomeEngine.Next/issues/78#issuecomment-5973663478) 记录 Cluster 39、RenderGraph 186 项通过。其交付 `427d53d491b469bd311e400fbd911413dcb6f2c3` 的旧 `ClusterRenderingTests` 确实检查 received=consumed、消费数不超过先前帧、消费页缺失数大于零及有效 epoch；该旧类后来在 `bd72407a` 的场景测试迁移中删除。当前纯覆盖图像测试不单独充当新的 N→N+k 消费证据。

本次直接读取的原始归档是 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/acceptance-95285a3b.zip`：

- `ticket-134/material-routing.trx`、`ticket-135/gpu-window/headless-work-material.trx`：材质替换各 8/8 通过，覆盖两后端、硬件/软件及普通/细分材质。
- `ticket-133/public-rendering.trx`：增加和删除细分材质的 D3D12 行通过；对应 Vulkan 行为 NotExecuted，未计为通过。
- `ticket-133/render-graph.trx`、`ticket-134/render-graph.trx`：持久回读各 4/4，通过项包括完成后一次交付和录制标签、禁用、取消及提前结束提交。
- `ticket-135/gpu-window/public-transitions.trx`：D3D12 几何增删、相机遮挡恢复和流送覆盖通过；相关 Vulkan Presentation 行为 NotExecuted。

## 候选 48b9d68e 的实际验证

已读取 `F:/AI-Workspaces/session-930423fde53dfc2b/work/integration/artifacts/test-results/rg70-integration-48b9d68e/` 下 12 份终态 TRX：

| 证据 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| `rendergraph-01.trx` 至 `rendergraph-07.trx` | 197 | 0 | 0 |
| `render-01.trx` 至 `render-04.trx` | 101 | 0 | 0 |
| `cluster-01.trx` | 7 | 0 | 0 |
| 合计 | 305 | 0 | 0 |

这些结果对应本次修改的公开行为：

- `FrameBufferTransfers`：跨线程写入登记顺序、写入组提交和取消、重叠字节读回。
- `BufferRangeSet`：Whole 范围保留 descriptor 子视图，Whole 与显式范围共同使用。
- `FrameAnalysis`、`FrameExecutor`、`FrameRegionRows`、`RenderGraphFrameState`：静态稀疏读取复用，局部区段增减不使独立区段重新分析，失败声明丢弃。
- `RenderInstanceResources`、`ClusterMaterialAssignments`：稀疏更新保留未变列，共享绑定/无变化批及必需替换列行为；材质偏移直接填充最终跨度。
- `ClusterInstanceUploadTests`：精确上传范围和实际 GPU 值，包含相邻属性、取消/重试、并行 packet、四个 RenderMesh 派生属性和单行材质偏移。`ClusterRenderResources` 本批仅补充变化列语义说明。

这是该候选的受影响模块/行为证据，不是新的全仓库、全硬件组合或性能测量结论。

## #79 重开项处置

[#79 重开评论](https://github.com/ODtian/SomeEngine.Next/issues/79#issuecomment-6008481348) 指出的相邻不同属性过量上传及 computed roots/expansions 中间缓冲问题，已有按属性变化行写入与最终映射跨度实现，并由上述 7 项 Cluster GPU 回读/精确范围测试覆盖。`RenderMesh` 作为一个 ECS 变化列，其 root、bounds expansion、depth cache class、first-person 四个派生属性共享变化行；断言保留精确四段，并验证其它行和变换列未变化。

空间排序原验收复用 [#79](https://github.com/ODtian/SomeEngine.Next/issues/79) 的已接受提交 `3f0f7da464a664bd6bc6511ffba03e62ab40d634`。排序输入/重映射、`ScenePositions` 行映射、公开断言及 `_sceneQuery` 在该提交、`96b73298`、`48b9d68e` 均保持一致。原标准为迁移输出一致，不额外扩大为全图 hash 刷新条件。

## #80 普通 UI 验证

同一源码 `48b9d68e` 的 Debug 和 Release 构建、测试命令均退出 0；两配置的 D3D12 普通 UI 图像断言均通过，每次执行四组 12 帧捕获。两配置的 Vulkan 行均因适配器不支持 Presentation 而 NotExecuted，未计为通过。

| 配置 | 总行数 | 通过 | 失败 | 能力跳过 |
| --- | ---: | ---: | ---: | ---: |
| Debug | 2 | 1 | 0 | 1 |
| Release | 2 | 1 | 0 | 1 |

原始结果目录：`F:/AI-Workspaces/session-930423fde53dfc2b/work/integration/.agent-tmp/ui80-final-20261009T130837Z/`。

- `ui-Debug.trx` SHA-256：`6d1af694f488a750ce371033af2c365af7673d00640016ff9e38ebca1514df7f`。
- `ui-Release.trx` SHA-256：`54922e7b38bda61095f44669ea4d0bff73804082157d699a24b30e06b8069ea0`。
- 测试筛选：`FullyQualifiedName~DebugUiImageTests`，原图像断言未放宽。
- 临时执行器因严格要求零跳过而最终返回 1；这不等于断言失败，也不能把能力跳过写成通过。两份 TRX 是具体结果依据。
- 上传页的 Vertex/Index 用途、Runtime 单一帧资源准入及 CPU-only 读写锁文档经源码核查；GPU 完成独立性已由上述 Render 101 项中的公共测试覆盖。
- 本次另增的捕获元数据测试四行未取得终态执行结果，不列为通过；它们不是 #80 普通 UI 的新增门槛。

## CI 记录与最小合并路径

PR #136 可合并且没有冲突；现有目标分支未受保护。当前两次软件适配器检查 [37922322283](https://github.com/ODtian/SomeEngine.Next/actions/runs/37922322283) 和 [37922315971](https://github.com/ODtian/SomeEngine.Next/actions/runs/37922315971) 在 runner 分配/步骤执行前失败。已核对的 GitHub annotation 指向账户付款/消费额度限制；这不是测试已经执行后失败，也不应写成 CI 通过。

用户最新明确要求“跳过ci合并啊”，授权不等待受账户额度限制而无法启动的 Actions。合并说明保留该限制及实际本地证据，不将未执行检查改写为通过。仓库保持 PRIVATE，不改变可见性。本地 #80 普通 UI 的终态记录已在上文列明。

最小路径是保留既有 PR、目标及源码历史，将已验证候选交付到原 head 分支，据上述证据处置 #79/#80 的重开点，再完成 #70 交付。无需新增源码拆分或改目标为 main。远端 main 的 license/NOTICE 分叉不改变本 PR 已约定的目标；若用户另行要求交付到 main，再独立处理该目标选择。

已读取的 PR 关闭语句指向 #189/#191–#201 而未指向 #70；发布收尾证据时，应使关闭语句只对应本次确实完成的 #70 及相应实现票，将独立 VSM/性能工作保留为相关链接，避免因合并误关闭未完成事项。远端推送、PR 元数据与合并状态以 GitHub 实际记录为准；本文件仅记录验收证据和授权范围。

GPU 需求优先级与实际页面加载

来源固定 UE 5.8.3 commit `396c9f059903aed5fec78ecd3d437a40c6415368`：`NaniteStreaming.ush:19–31` 的页范围请求及类别/浮点优先级打包；`NaniteClusterCulling.usf:583` 的实际需求生产；`NaniteDataDecode.ush:996–997/1063` 的类别解码与主视图类别3。VSM 类别0沿既有 compact view 写入。SHA/bytes 位于 native-sources.json。

GPU 每页保留最强优先级，收尾 pass 把 leaf/page range/priority 写入同一请求记录，经原 readback/channel/epoch 解码到 CPU。CPU 一次展开需要加载的依赖组，队列保留较强优先级，按优先级选择受预算约束的页面读取。已驻留页面保留可见需求记录，沿既有 satisfied 状态跳过加载依赖展开。并发、字节容量、撤销、晚到读回和 epoch 路径沿用现有驻留系统。

唯一公开行为 `APageReadBudgetLoadsTheGreaterProjectedDetailDemandFirst`：先登记小而远的球，再登记较大而近的球；实际资产页面源通过显式同步点观察读取，`MaxConcurrentPageLoads=1` 要求高投影需求球先读。

- FIFO 对照仅适配四字请求与公开读取预算，保留既有先到先读取的选择：1 failed / 0 passed / 0 skipped，实际先读低需求球，见 red.trx/log、red-source.json。
- 首次排序片仍被已驻留最低细节页的最大误差及依赖展开覆盖：1 failed，见 resident-dependency-red.trx/log。
- 恢复 satisfied 页面加载跳过语义后，相同公开行：1 passed / 0 failed / 0 skipped，7 s。高需求球先读，并发预算同步点只观察到1个读取。见 green.trx/log、green-source.json。
- 统一测试规则0 violations；增量构建0 warnings/errors。独立 finish SPIR-V 3680 bytes，资源反射仅 Views、PageFaultBuffer 两项。

首轮 PageLists 绑定失败与 fixture metrics 设置失败分别留存；实际优先级 red/green 以上述独立记录为准。全量 backend 和三场景性能由 #70 统一验收推进。

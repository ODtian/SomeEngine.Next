# Change tracking  [[architecture/ECS-Architecture|ECS Architecture]]


时钟窗口与写入版本是两个不同概念。`AcquireSystemTick()`在推进clock的同时返回推进前的baseline，供consumer保存“上次观察到哪里”；`AcquireSystemVersion()`返回推进后的新version，供本次写入发布row/coarse/journal facts。公开`ExecuteQuery`不再允许caller传入current version：writable query在exact query admission之后自动取得新write version，read-only query直接使用当前tick而不推进clock；显式current overload只保留给已经拥有精确owner的runtime-internal adapter。

对于直接写World storage的普通data owner，write/change epoch不在Schedule时更新。generated `IJobEntity`先完成完整owner admission与chunk/row filters，只有存在matching row且immutable descriptor含direct World write时才惰性取得一个共享version；empty、fully filtered和matching read-only执行都不推进clock。每个table/buffer/sparse写入复用该version，wrap-aware coarse publication防止较旧packet晚完成时倒退chunk watermark。hierarchy propagation更严格：packet入口与context创建都不取version，只有首个实际`context.Write`在relationship、capability、component presence与exact stable-row owner检查全部通过后，才通过internal trusted primitive取得一次共享version；所有packets复用它。read-only callback、声明Write但未调用、empty normalized roots和在guard处拒绝的访问均不产生假epoch。

topology packet是明确的例外，因为它的packet body只改detached staging而不写World。`TopologyPacketContext`只暴露用于selection/filter的`LastSystemVersion`，不分配或暴露`CurrentSystemVersion`，staging arrays也不携带commit version。唯一的topology commit version属于后续serial finalizer：它先取得Parent/topology writer admission，在structural candidate内调用`AcquireSystemVersion()`取得新write version，再把同一version用于本次Parent/Children的row、coarse与journal facts。empty capture和staged no-op均不调度World writer、不取tick，也不发布假topology revision。

当前`SystemGroup`除保存保守的system `LastSystemVersion`外，还为同步update callback提供
`SystemChangeCheckpoints`：checkpoint以World identity + `QueryHandle`区分，成功的query执行先
stage精确可见version，只有整个system update成功才提交；query或后续system body fault时stage
全部丢弃，下一次回退到上次成功checkpoint或保守baseline。因此，同一同步callback里的多个
consumer已经不必共享一条伪全局游标。这个实现不跨越异步Job scope，也不提供terminal lane、
`ScopeCompleted`提交或copied-handle identity语义。

若后续需要把上述同步checkpoint扩展为跨异步Job scope的per-consumer
`QueryCheckpointHandle`，必须一次性满足以下契约，而不能只把system结束时的全局tick写回Last：

1. 先以完整 structural match ranges admission；Changed 不能用于提前缩窄 owner ranges；
2. 等所有相关 component/enablement/topology hazards完成，此时 later conflicting writers已被当前 owner挡住；
3. 捕获相关 storage 的 completed-write upper watermark并在 owner内过滤 `(last successful, upper]`；
4. 整个 `ScopeCompleted` 成功后提交 checkpoint；
5. fault/cancel 不推进 checkpoint，下一次是 at-least-once retry。

届时checkpoint应是runtime registry中带identity/generation的`QueryCheckpointHandle`，复制handle仍指向同一lane。每次使用在lane内原子reserve sequence、读取previous tail并添加`AfterCompletion(previous)`，再发布新tail；上一次fault不传播，但checkpoint未推进，下一次重复处理。在system callback内，generated adapter可用ambient `SystemInstanceId + adapter id`解析persistent lane；同一system里需要多个同-type lanes时用户显式提供不同handle。脱离generated system的manual caller也必须显式传handle，SG不能假装从任意call site/副本推断identity。这是checkpoint私有terminal order，不是`GlobalDependency`。

该未来checkpoint lane必须是coordinator/root的顺序状态，不允许从同一active scope的dynamic descendant重入，也不允许新增lane edge后回到current/previous tail scope；它与普通semantic edge一起做cycle detection并在Schedule时确定性拒绝，而不是等待成死锁。diagnostic应同时报告checkpoint id、previous owner、current scope和闭环ancestor edge。dynamic branches使用不同checkpoint handles，或由coordinator在scope完成后再次使用同一K。这样才能保留schedule-order、fault retry和不漏change，而不把checkpoint降成会overtaking的resource hazard。

upper 不是“最大已发号 writer”这种会被 out-of-order completion跳过的全局 max；query在 hazards完成后读取各 matching chunk/shard的 stable completed versions，并用捕获的 epoch fence限定本次窗口。structural membership、enablement和chunk generation publication也取得 epoch并参加对应 filter watermark。


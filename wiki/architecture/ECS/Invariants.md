# 不变量  [[architecture/ECS-Architecture|ECS Architecture]]


- 访问描述可以存储；真实 ref/span/view 不能在 owner 之外存在。
- 名称也是契约：所有导出的 `*View` 必须是 byref-like 的 callback-scope borrow；可以跨
  publication 保存、由 `ReadOnlyMemory<T>` 固定 immutable generation 的值必须命名为
  `*Snapshot`，不能伪装成 View。
- `*Desc` / `*Descriptor` 必须拥有多个已经正规化的独立事实及其验证规则；只保存一个
  被转发对象、没有自身不变量的 descriptor 是禁止的包装层。
- 产品代码中的 `ToArray` 只允许出现在显式 serialization plan、snapshot publication、
  immutable owner/descriptor construction、scheduler handoff、topology transaction 或
  SourceGen 边界；query row/chunk 枚举、component/buffer/sparse read/write 热路径不得物化。
- 显式依赖先完成，resource admission 后发生；未 ready 的 job 不预占资源。
- 一个 ECS query job 使用一个逻辑 owner和多个内部 work items；内部 batch 不互相进入 resource frontier，但必须先通过 per-packet self-alias proof。
- 所有可能被 entity destroy、component membership change、relocation 或 chunk retirement 失效的 ECS borrow 都隐式持有 World topology Read；只有 immutable generation pin 例外。结构事务持有 topology Exclusive。
- relation endpoints、Children、Depth、adjacency/index 等 derived 或受保护的不变量数据不能通过普通 RW ref 修改。`Parent<D>` 是例外：owner 内的 job-safe writable ref/span 是合法的 deferred hierarchy mutation，必须经过 domain access admission、change publication 和 owner 结束时的 forest validation。
- 结构事务在 live commit 前完成验证与分配，并原子发布它本次包含的 canonical state、derived state、journal 和 epoch。该原子性不消灭 hierarchy 的显式 deferred 语义：合法 Parent 可以先于 Children 可见，Children 在 maintenance 前表示 last-applied inverse。
- system callback 保持稳定顺序，负责编排并可进入显式 caller-owner Execute；它不裸访问 ECS，重工作由 job承担，也不存在 frame-global completion。
- 调度时序、worker 完成顺序和 dictionary 顺序都不是业务确定性来源。


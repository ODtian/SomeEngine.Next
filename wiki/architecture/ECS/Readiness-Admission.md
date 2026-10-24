# Readiness-order admission  [[architecture/ECS-Architecture|ECS Architecture]]


一次异步提交执行以下顺序：

```text
normalize descriptors
  -> reserve ResourceState/container identity and acquire lifetime permits
  -> attach scope and register full semantic dependencies
  -> wait semantic ScopeCompleted
  -> atomically admit a fixed access set, or run the sealed ECS preparation protocol
  -> wait conflicting predecessor WorkReleased
  -> create views and execute
  -> release accesses/pins
  -> join descendants
```

这意味着先调用 `Schedule` 但仍等待 dependency 的 D，可以被后提交且已经 ready 的 X 超越。resource access 的契约是“不重叠”，不是“后 Schedule 的一定读到先 Schedule 的结果”。需要读新结果时必须传显式 `JobHandle`。

owner 进入 ready-admission queue 后按 ready enqueue sequence获得公平服务；同时 ready 的稳定 coordinator submissions以 logical job id打破平局。公平性防止 dependency 刚完成的 owner 被持续新提交饿死，但不把原始 Schedule 顺序伪装成 freshness。

如果在 Schedule 时把尚未 ready 的 access 预先放入 conflict frontier，动态 scope 会产生不可消除的环：父 P 持有 R，D 占据 R 且依赖 P，P 创建的 child C 需要 R，随后另一个依赖链又等待 D。readiness admission 删除的是未 ready owner 的 conflict ownership，因此 resource edges 只在可执行 owner 之间建立；submission 仍保留不参与冲突的 identity/lifetime reservation，防止等待 dependency 时 `ReleaseResource` 复用同一 identity。dependency success 才在一个锁内针对当时 frontier 激活固定 access set，dependency fault/cancel则释放 reservation。

固定 access set 的多资源 admission 在一个线性化事务中完成；动态 ECS query 使用下节定义的 prepare/seal 等价协议。frontier 使用 whole-access frontier 加 augmented interval index，range 查询目标复杂度为 `O(log N + K)`。

access normalizer 构造 piecewise mode map，而不是无条件让 whole 吞掉 ranges：whole Write 可覆盖所有 ranges；whole Read 只覆盖 range Reads；whole Read + subrange Write 必须保留为 default Read 加 Write overrides，或显式保守提升到 whole Write。任意重叠段取最强 mode，相邻同 mode 段合并。

semantic graph 仍要做 cycle detection。child 直接或间接依赖当前/ancestor scope 必须拒绝，`CombineDependencies` 不能丢失 prerequisite graph。任何 job/Execute body 内的 `Complete` 和嵌套 `Execute` 都默认拒绝；需要嵌套 helper 时复用 ambient owner，并验证请求只是现有权限的子集。

pin 使用强 container anchor、distinct ResourceState references 和 generation。`ReleaseResource` 在所有 safety modes 都不得释放或复用 active/pinned resource；Fast 只能减少诊断，不能把 pending access 变成 use-after-free。


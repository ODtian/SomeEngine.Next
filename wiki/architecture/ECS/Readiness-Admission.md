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

## 显式 Scheduler 的资源绑定

这是现行公共 API 的使用说明，对应 [`JobResourceAccess`](../../../src/SomeEngine.Job/JobResourceAccess.cs) 与其 [Scheduler 重载](../../../src/SomeEngine.Job/JobResourceAccess.Scheduler.cs)。

`JobResourceAccess.Read`、`Write`、`Exclusive` 的首参数可以传入目标 `Scheduler`。数组、数组切片、List、Dictionary、Stack、Queue 以及数组支持的 Memory 共用该 Scheduler 的对象资源表。同一数组的 Span、Memory 与 ReadOnlyMemory 使用数组本身的资源身份，范围坐标包含切片在数组中的起点。

以下是数组切片与 Memory 别名的使用示例，两项声明覆盖同一数组区间：

```csharp
using var jobs = new Scheduler();
int[] values = new int[128];
JobResourceAccess written = JobResourceAccess.Write(jobs, values, 32, 16);
JobResourceAccess read = JobResourceAccess.Read(jobs, values.AsMemory(32, 16));
```

省略 Scheduler 的便捷入口使用当前 Job 所属的 Scheduler；在 Job 回调外使用 `JobSystem.Default`。同一批提交的资源声明、依赖句柄和提交入口应属于同一个 Scheduler。

[`IJobResourceProvider<TContainer, TAccess>`](../../../src/SomeEngine.Job/IJobResourceProvider.cs) 的三个静态方法接收目标 Scheduler，再依据实际数据形成访问。自定义提供者使用 `JobResourceAccess.Read<TContainer, TProvider>(jobs, ref data)` 等入口；省略 Scheduler 的提供者调用也获得上述当前或默认 Scheduler。

Dots 的 `IJob`、`IJobParallelFor`、`IJobFor` 和 `IJobChunk` 扩展均提供首个普通参数为 Scheduler 的提交重载，随后接工作范围、资源访问和依赖。生成式 Job 则从 World 的 Job 连接取得 Scheduler；组件资源描述器的 [`JobResourceAccessBuilder`](../../../src/SomeEngine.ECS.Systems/JobComponentResources.cs) 使用同一个 Scheduler 绑定实际对象及 Memory 范围。

## 容器数据的使用期

这是当前资源使用期行为的说明，对应[容器绑定](../../../src/SomeEngine.Job/ResourceManager.ContainerResources.cs)、[依赖等待期的访问预留](../../../src/SomeEngine.Job/ResourceManager.Reservations.cs)和[活动访问记录](../../../src/SomeEngine.Job/ResourceManager.State.cs)。对象资源表以容器对象为键，资源状态保存容器弱引用；提交时形成的预留记录和活动访问记录持有实际容器的强引用。

等待显式依赖时，预留记录保持容器有效；依赖就绪后，实际容器随活动访问记录进入执行期。工作释放资源访问时清除记录持有的引用。使用相同容器的子工作通过自己的资源声明取得相同身份及强引用，使数据有效期覆盖子工作的实际使用。生成式 Job 通过现有配置引用与 World 连接使用计数保持其数据到完整子工作树完成。

Checked 与 Fast 采用相同的有效使用期：`ReleaseResource` 和 `ReleaseResourceToken` 在访问预留或活动访问仍存在时抛出 `JobResourceSafetyException`，工作完成后可归还资源身份。仅保留值类型的 `JobResourceAccess` 描述符时，容器仍按实际使用记录的有效期回收。


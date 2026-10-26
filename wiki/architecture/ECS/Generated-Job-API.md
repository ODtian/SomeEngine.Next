# Generated job API  [[architecture/ECS-Architecture|ECS Architecture]]


entity-oriented API 采用生态已经验证的 `IJobEntity`、`Execute(in/ref)`、`Schedule`、`ScheduleParallel`：

```csharp
public partial struct IntegrateJob : IJobEntity
{
    public void Execute(Entity entity, in Velocity velocity, ref Position position)
    {
        position.Value += velocity.Value;
    }
}

JobHandle integrated =
    new IntegrateJob().ScheduleParallel(world, dependency);

new ConsumeJob().Execute(world, integrated);
```

SG 从签名推导 query 和 RO/RW，生成 cache、access set、owner admission、packet alias proof、chunk adapter、batch dispatch 和 diagnostics。Job struct 在 Schedule 时按值捕获，每个 packet 得到独立值副本；Job 字段保存配置，ECS 行保存各实体的输出。Job 字段可直接持有普通资源对象，调用方通过 `externalAccesses` 声明实际读取或写入的对象、数组及范围。

以下是共享数组采样的使用例子：

```csharp
public partial struct SampleHeight(float[] heights) : IJobEntity
{
    private readonly float[] _heights = heights;

    public void Execute(in SampleIndex index, ref Height height)
    {
        height.Value = _heights[index.Value];
    }
}

JobResourceToken data = scheduler.GetResourceToken(heights);
JobHandle sampled = new SampleHeight(heights).ScheduleParallel(
    world,
    [JobResourceAccess.Read(data)],
    new JobEntityScheduleOptions(rowsPerPacket: 64),
    dependency);
```

`Scheduler.GetResourceToken(object)` 将实际对象绑定到指定 Scheduler；数组范围由 `JobResourceAccess.Read/Write(token, start, length)` 声明。使用默认 Scheduler 时，也可直接调用现有 `JobResourceAccess.Read/Write(array)`。不同对象分别取得身份；对象内部共享数组时，各使用者声明该数组的身份和访问范围。

`Schedule`、`ScheduleParallel` 和同步 `Execute` 均接受显式资源集合。提交时复制访问集合并保留 Job 配置；语义依赖完成后，捕获查询的工作与执行行的子任务沿既有就绪及资源协议交接访问。依赖句柄表达读取哪次写入结果，独立范围允许并行推进。Job 中的引用由完成状态持有至全部子任务完成，随后随普通引用生命周期回收。执行仍通过生成的强类型 adapter 调用用户方法。

组件字段中的普通资源引用由参数上的 `JobResources<TResources>` 选择静态描述器。描述器实现 `IJobComponentResources<TComponent>`，在 `Collect(in component, ref resources)` 中声明此次调用实际读取或修改的数据。组件列的 `in/ref` 表达引用值的访问，`JobResourceAccessBuilder` 的 `Read/Write/Exclusive` 表达所指数据的访问。

以下是组件引用共享高度数据的使用例子：

```csharp
public readonly record struct HeightSource(ReadOnlyMemory<float> Heights, int Index) : IComponent;

public readonly struct HeightResources : IJobComponentResources<HeightSource>
{
    public static void Collect(in HeightSource component, ref JobResourceAccessBuilder resources)
        => resources.Read(component.Heights);
}

public partial struct SampleComponentHeight : IJobEntity
{
    public void Execute([JobResources<HeightResources>] in HeightSource source, ref Height height)
        => height.Value = source.Heights.Span[source.Index];
}

new SampleComponentHeight().ScheduleParallel(world, dependency).Complete();
```

泛型 Job 使用 `[JobResources(typeof(Resources<>))]` 指定开放泛型描述器。生成器将组件类型的实参填入描述器，再生成闭合的静态接口调用。例如 `Source<T>` 配合 `Resources<>` 会得到 `Resources<T>`；这是开放泛型描述器的参数匹配规则。

描述器本身由 Job 类型参数提供时，使用 `[JobResources(nameof(TResources))]`。
生成器在该 Job 的类型参数中解析名称，并核对其 `IJobComponentResources<TComponent>` 约束，
随后生成同一 `Table<TComponent,TResources>` 静态资源路径。
具体使用见 `WriteDeviceGeometryInputsJob<TGeometry,TReader>`，原组件与引用数据在依赖就绪后读取。

Builder 接受普通对象、数组及范围。`Memory<T>` / `ReadOnlyMemory<T>` 的重载沿用 `JobResourceAccess` 的数组底层与切片规则：两个指向同一数组的视图使用相同身份，范围按数组偏移协调，实际数组随工作保留。描述器的 `GetVersion(in component)` 默认为零；引用对象内部重新选择数据时，实现该方法返回随数据绑定变化的版本，并将其可变映射数据纳入访问声明。替换整个组件引用由 ECS 列写入序号追踪。

组件资源查询按既有 Scheduler 的固定访问集合执行：语义依赖完成后，在 ECS 列读取权限下按查询分区发现引用、数据版本与范围；按实际资源身份和重叠范围合并冲突行；取得完整 ECS 与资源集合后再次核对列写入序号、启用序号、拓扑代次及描述器版本。发生变化时，将重新发现作为子工作提交。校验成功后，持有该访问集合的作业通过 `Scheduler.ExecuteParallel` 执行相容组并同步等待范围完成。正数 `RowsPerPacket` 同时提供组分包粒度；零值使用该同步入口的自动分包。返回的 JobHandle 覆盖引用发现、就绪后的复核和全部组执行，完整权限持续到范围结束。

只读共享数据和独立范围分别允许并行。含写入或 Exclusive 的重叠范围进入同一组，组内按查询行顺序执行；多个资源的冲突关系共同决定分组。查询描述器按 World 缓存分组；引用、成员、筛选状态及资源数据版本保持一致时复用分组。组件写入序号逐次递增，可区分同一 ECS tick 中的多次引用替换。已取得的组件引用和实际数据保留至执行子任务全部结束。

chunk/SIMD API 扩展现有 `IJobChunk`，由 SG 生成显式 adapter：

```csharp
public partial struct IntegrateChunks : IJobChunk
{
    public void Execute(ReadOnlySpan<Velocity> velocity, Span<Position> position)
    {
    }
}
```

普通 Roslyn generator 只能 additive，不能重写用户方法或 arbitrary call site。因此同步 query 由 runtime `ref struct` enumerator 自动持有 owner，不能承诺复制 Unity 的 `SystemAPI.Query` call-site rewriting。

SourceGen 必须作为 analyzer build-transitive 地进入每个 consumer compilation。generated code 位于用户 assembly，所需 runtime plumbing 使用 public + `EditorBrowsable(Never)` 的窄入口和不可伪造 runtime token；Checked/Strict 交叉验证 descriptor、World generation、owner 和 epoch。


## Related concepts

- [[Random-Lookup|Random lookup]]
- [[Special-Invariant-Data|特殊不变量数据]]

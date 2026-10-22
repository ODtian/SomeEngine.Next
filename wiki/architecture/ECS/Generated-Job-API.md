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

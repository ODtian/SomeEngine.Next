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

SG 从签名推导 query 和 RO/RW，生成 cache、access set、owner admission、packet alias proof、chunk adapter、batch dispatch 和 diagnostics。job struct 在 Schedule 时按值捕获，每个 packet得到独立 value copy；SG 拒绝依赖 job instance field回写结果。reference fields仍然 alias，必须声明 external resource或使用认证容器/reduction。

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

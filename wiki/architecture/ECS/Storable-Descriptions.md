# 可存储  Parent: [[Borrow-and-Storable-Descriptions]] 

- `QueryDefinition` / `QueryHandle`
- `ComponentLookupHandle<T>`
- generated access descriptor
- `HierarchyChildrenSnapshot<D>`（固定一份已发布的 immutable child generation）
- `SystemChangeCheckpoints`（仅限 `SystemGroup` 同步 update callback 的成功提交范围）
- future asynchronous `QueryCheckpointHandle`（跨 Job scope 的 terminal lane 当前未实现）

它们只描述未来访问，不包含可解引用的 storage capability。handle 必须携带并验证 World/runtime generation。

manual synchronous shape使用现有`QueryHandle`与runtime-owned callback，不保留`RunQuery(lastSystemVersion, currentSystemVersion)`，也不让caller提供current version：

```csharp
QueryHandle moving = world.Query(
    world.QueryDefinition()
        .Read<Velocity>()
        .Write<Position>());

world.ExecuteQuery(moving, lastSystemVersion, cursor =>
{
    foreach (QueryRow row in cursor.Rows)
        row.ReadWrite<Position>().Value += row.Read<Velocity>().Value;
});

world.ExecuteQuery(moving, lastSystemVersion, cursor =>
{
    foreach (QueryChunkView chunk in cursor.Chunks)
    {
        ReadOnlySpan<Velocity> velocity = chunk.Read<Velocity>();
        Span<Position> position = chunk.ReadWrite<Position>();
        for (int i = 0; i < chunk.Count; i++)
            position[i].Value += velocity[i].Value;
    }
});
```

`QueryCursor`、`Rows/Chunks` enumerator、`QueryRow`、`QueryChunkView`与其中的span/ref都只在callback owner内创建；callback返回后没有release capability或borrow可逃逸。query declaration之外的component访问拒绝。具体tuple sugar可由SG添加，但正确性不依赖call-site rewriting。


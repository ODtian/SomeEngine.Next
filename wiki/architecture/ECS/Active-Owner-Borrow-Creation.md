# owner 活跃时创建  Parent: [[Borrow-and-Storable-Descriptions]] 

- `RefRO<T>` / `RefRW<T>`
- checked borrowed row/range wrappers
- generated callback 内的 `ReadOnlySpan<T>` / `Span<T>`
- `DynamicBuffer<T>` / `BufferView<T>`
- `ComponentLookupView<T>`
- relation adjacency borrow
- callback 内的 hierarchy current-generation traversal borrow
- `QueryRow` / `QueryChunkView`

这些类型形成完整 `ref struct` 链，并携带 owner id、borrow epoch 和 storage/snapshot generation。foreach convenience path 的 wrapper 每次 dereference 都执行不可关闭的 active-owner check；Fast 只可在 runtime 控制的 generated callback boundary 内把重复检查 hoist/elide，不能删除 admission、pins、generation lifetime 或 hazard ordering。

C# 的 `ref struct` 不是 affine/linear type：它仍可复制，且无法在已经抽出的裸 ref/span 每次使用时重新检查。因此强安全、高性能入口是 runtime 调用的 generated `Execute` callback；runtime 自己持有并在 `finally` 释放 owner，用户拿不到 release capability，raw spans 只作为这个 callback 的 `scoped` 参数存在。

当前`QueryChunkView`、`QueryRow`与child row cursor均为`ref struct`或只在其ref-struct链内可达。同步callback由runtime在`finally`中释放owner；raw Span只在callback词法生存期内出现，不能由普通public enumerable在owner外重新取得。

`HierarchyChildrenSnapshot<D>` 是刻意的例外而不是 borrow：它只持有
`ReadOnlyMemory<Entity>` 和 generation，publication 以后旧数组不再修改，所以该值可以存储。
它暴露的 `ReadOnlySpan<Entity>` 仍不能脱离持有 snapshot 的词法使用；需要独立数组所有权时
必须显式调用 `ToArray()`。旧名 `HierarchyChildrenView<D>` 已删除，防止调用者把可存储
generation 错认成 callback-scope capability。

copy-returning `World.Read<T>` 和 value/structural setter 可以是短同步 Execute boundary。独立 `World.Get<T>() -> ref T` 不能在 owner 外自动安全释放，因此只允许 ambient owner 内部使用或由 borrow view 替代。

同步 `Rows/Chunks` 枚举本身也是 Execute scope：正常结束、break 或 exception 时先释放枚举 body 的 resource owner，再 join 枚举期间创建的 descendants。共享 owner state 令 Dispose 幂等，但复制后的另一个 enumerator不能继续使用；它的下一次 MoveNext/wrapper dereference在所有 modes 拒绝。无法接受 analyzer约束的调用方必须使用 generated `Execute`，不能把 foreach 当作裸 borrow primitive。


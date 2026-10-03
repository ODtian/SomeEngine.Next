# Region Reconciliation

## Definition

`GraphRegionMap<TKey, TValue>` 保存一个持久父区段及业务键、值到子区段的不可变映射。
它用于材质箱、细分组等由业务键维护的局部结构。图结构仍由 RenderGraph 的区段树持有。

## Reconciliation

`Reconcile(ref RenderGraphEdit edit, IEnumerable<KeyValuePair<TKey, TValue>> desired,
GraphRegionBuilder<TKey, TValue> build)` 比较此父区段下的期望键值，返回新映射。

| 键和值 | 编辑行为 |
| --- | --- |
| 键消失 | 删除原来的子区段及其全部工作 |
| 新键 | 在父区段末尾创建子区段，调用构建回调 |
| 值变化 | 删除原来的子区段，再在父区段末尾创建并构建替代区段 |
| 值相同 | 保留原区段身份、pass 与分析结果 |

新增及替代区段按期望序列中的顺序追加。已有且值相同的键保持原位置。
值使用构造时给出的 `IEqualityComparer<TValue>` 比较，默认使用
`EqualityComparer<TValue>.Default`。`TryGetRegion(key, out region)` 提供键对应的图身份。
`Values` 枚举子区段当前保留的业务值，可用于更新该区段的帧输入和释放对应上传数据。

构建回调接收编辑、子区段、键和值，通过普通编辑 API 安装该键的工作。它可继续在
子区段中创建嵌套区段。变化沿现有 [[Incremental-Reconciliation]] 与资源依赖分析传播。

## Commit boundary

调用方保留原映射，在提交成功后替换为返回值：

```csharp
RenderGraphEdit edit = graph.BeginEdit();
try
{
    GraphRegionMap<TKey, TValue> next = regions.Reconcile(ref edit, desired, BuildRegion);
    edit.Commit();
    regions = next;
}
finally
{
    edit.Dispose();
}
```

原映射的内容始终保持原值。编辑丢弃或构建回调抛出异常后，原映射继续对应已提交结构，
调用方可以使用它发起下一次调和。

## Behavior evidence

`RegionReconciliationTests` 通过 WARP 缓冲回读、映射查询和公共分析统计，覆盖新增、
删除、值变化、值相同、单键变化的身份与分析范围，以及丢弃编辑后继续使用原映射。

## Relations

[[Regions]] · [[Structural-Changes]] · [[Retained-Graph]] · [[Incremental-Reconciliation]] · [[Graph-Versioning]]

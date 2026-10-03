# Lists

## Current design
- 父计算通过 `Each(query)` 只依赖查询成员；每行是按实体做 key 的子视图，读取自己实体的数据，某一行变化只重跑该行。
- `VirtualList` 根据自身 `LayoutRect`、滚动偏移与固定或估计的行高给出可见范围：

```csharp
foreach (int i in Visible(list, count))
    using (Key(items[i].Id)) Row(items[i]);
```

- 滚动偏移是组件，滚动时列表视图重跑，只创建或释放进出视口的行。

## Relations
[[architecture/someui/Dependencies|Dependencies]] · [[architecture/someui/Node-Identity|Node Identity]]

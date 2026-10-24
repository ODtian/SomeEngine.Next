# Translucency

**状态：实现已于 2026-10-04 删除（#88）。** `TranslucencyFeature` 及其输出类型的引用核对见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

## 历史设计

Translucency 是 composition/rendering feature，不因为使用 Cluster geometry 就成为 Cluster core。

其排序、混合、composition policy 属于自身 pipeline；可按需要选择 Cluster 或其它 geometry path。

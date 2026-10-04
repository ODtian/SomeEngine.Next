# Debug Rendering

**状态：实现已于 2026-10-04 删除（#88）。** `DebugFeature`、输出类型及显示枚举的引用核对见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

## 历史设计

Debug rendering 是独立 consumer，可以读取 geometry、visibility、streaming diagnostics 等信息，但不把 debug pass、颜色编码或诊断状态写入 Cluster core contract。

# RHI-DSC-007 — Parameter binding inputs

`ResourceBinding` 是执行期 payload：binding kind 加具体 public view/Sampler reference，或该设备允许的 typed null；相等性使用对象引用身份。

`ParameterBlockBindings` 是 stack-only packet，包含直接 Slang `VariableLayoutReflection`、按该布局顺序排列且长度受限的 `ResourceBinding` span，以及包含 padding 的 ordinary-data bytes。该反射句柄由 pipeline 保留的 `IComponentType` 生命周期锚定。packet 不保存 register、space、root index 或后端 token。

长期 parameter bindings 绑定到一个 pipeline 与一个直接反射的 parameter object。更新先构造完整新值，再让后续 bind 使用新值；并发 recording 各自保留实际使用的那版数据直到 submission 结束。Transient 与长期 binding 使用同一 Slang 布局和后端 placement。

Static sampler 的位置、数组 shape 与 stage visibility 直接来自 Slang；其状态进入 pipeline compatibility/cache identity。
^rhi-dsc-007

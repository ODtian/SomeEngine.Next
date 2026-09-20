# RHI-DSC-007 — Parameter binding inputs

`ResourceBinding` 是执行期 payload：binding kind 加具体 public view/Sampler reference 或被允许的 typed null；相等性使用对象引用身份。

`ParameterBlockBindings` 是 stack-only packet，包含一个 Slang# detached variable layout、按该 layout 顺序排列且长度受限的 `ResourceBinding` span，以及包含 padding 的 ordinary-data bytes。它不保存 register、space、root index 或 backend token。

长期 parameter bindings 绑定到一个 Pipeline 与一个 reflected parameter object。更新先构造完整新值，再让后续 bind 使用新值；并发 recording 各自保留自己实际使用的那一版数据直到 submission 结束。Transient 与长期 binding 使用同一 Slang# layout 与 backend placement。

Static sampler 的 register、space、array shape 和 stage visibility 只来自 Slang# reflection；其状态进入 Pipeline compatibility/cache identity。
^rhi-dsc-007

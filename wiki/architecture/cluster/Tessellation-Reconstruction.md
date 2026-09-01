# Tessellation Reconstruction

Tessellated visibility 固定只保留 original visible cluster、original triangle identity 与 depth。不得增加 diced triangle identity、diced barycentric、UV 或其它 tessellation payload 来回避 shade reconstruction。

Shade 根据 depth 恢复最终位置，再结合 original triangle 调用该 surface 的 `reconstruct` 得到原三角 barycentric 及导数。调用接口统一，但具体数学不统一，也不在运行时按 surface 类型分支。

- Linear surface 使用原三角的闭式 perspective barycentric reconstruction。
- 沿插值 normal 的 scalar displacement 使用专用反解：把最终位置中的 displacement 当未知辅助标量求掉；inverse 阶段不重新采样 displacement texture。
- Phong quadratic surface 使用其二次参数式和解析 Jacobian 做 Newton/Gauss-Newton reconstruction。
- 其它用户 surface 自己实现适合其 forward mapping 的 reconstruction，也可以显式复用提供的数学 helper。

框架不通过增加 visibility bits 为某种 surface 保存额外参数；surface 必须在固定 visibility 信息预算内完成 reconstruction。
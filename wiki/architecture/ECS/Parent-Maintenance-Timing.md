# Parent Maintenance Timing

Parent 是 hierarchy 的真实关系；Children 等反向数据可以选择立即更新或延后批处理，但两种路径使用同一 transition kernel。

Immediate：Parent 改变后同一调用中更新对应 inverse data，返回时两者一致。

Deferred：先提交 Parent change facts，inverse data 保持上一 applied state，后续 hierarchy maintenance 批处理同一 transition kernel。

CommandBuffer 只决定结构命令何时执行，不隐式选择 immediate/deferred policy。
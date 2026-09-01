# RHI-CORE-005 — Error transport

真实执行环境失败使用明确错误通道：native/GPU operation failure 使用 `GraphicsException` 与 `GraphicsError`；Device Lost 保留为终止性设备事实；Acquire、Present、Reconfigure、CPU wait 等正常控制流使用各自 typed status。

内部 programmer misuse 不需要在 shipping 热路径持续构造 argument/object-lifetime exception；这类错误属于 [[architecture/validation/Development-Checks|Development Checks]]。调用边界若面对不可信外部输入，则按 [[architecture/validation/External-Input-Checks|External Input Checks]] 处理。

能力缺失使用明确 capability query；调用已确认不存在的能力可以报告 `NotSupportedException`。
^rhi-core-005

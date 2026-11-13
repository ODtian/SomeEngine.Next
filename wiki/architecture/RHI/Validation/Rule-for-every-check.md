# RHI-VAL-003 — Rule for every check

RHI 不建立独立于全引擎的永久 validation 哲学。每个检查必须归入 [[architecture/validation/Validation|Validation]] 定义的四类之一。

RHI 内部 programmer misuse、对象来源、Queue family、command lifecycle、binding shape、split barrier 配对等若属于引擎内部不变量，则只在开发检查或可选 `SomeEngine.Graphics.Validation` 中诊断，shipping direct backend 不为这些事实永久付费。

设备描述、资源与视图描述、节点可见性、管线描述、描述符值、参数包、查询、稀疏映射、共享资源、交换链 acquire/submit/present 顺序，以及调用所需的 capability，统一在 `ValidationLayer` 入口检验。设备通过只读属性投影原有功能与 tiling 数据，检验层直接使用这些事实。

后端保留缓存文件和导入原生句柄的输入解析、Slang/SPIR-V/native 返回元数据核对、分配容量耗尽、真实 native failure 与 Device Lost。向原生内存写入前的长度和范围保护集中在 `NativeMemoryChecks`。公开 mapped-memory 和 native-borrow 值访问继续检查有效序列，以保证暴露的原生地址可用。

Validation 不把 backend-private root index、descriptor offset 或 native placement 当作公共真相；shader 事实来自 Slang#。
^rhi-val-003

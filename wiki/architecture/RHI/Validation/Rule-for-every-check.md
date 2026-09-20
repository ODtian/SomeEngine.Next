# RHI-VAL-003 — Rule for every check

RHI 不建立独立于全引擎的永久 validation 哲学。每个检查必须归入 [[architecture/validation/Validation|Validation]] 定义的四类之一。

RHI 内部 programmer misuse、对象来源、Queue family、command lifecycle、binding shape、split barrier 配对等若属于引擎内部不变量，则只在开发检查或可选 `SomeEngine.Graphics.Validation` 中诊断，shipping direct backend 不为这些事实永久付费。

真实 native failure、Device Lost、I/O failure、不可用能力与保证 memory safety 所需的边界逻辑仍按其实际语义保留。

Validation 不把 backend-private root index、descriptor offset 或 native placement 当作公共真相；shader 事实来自 Slang#。
^rhi-val-003

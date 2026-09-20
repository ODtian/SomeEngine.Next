# Execution Failures

## Definition
设备丢失、native API 失败、I/O 失败、内存不足、不可用能力和真实损坏读取属于执行环境失败。

## Invariants
- shipping 必须正确传递或处理。
- 不和内部程序错误混成同一 validation 层。
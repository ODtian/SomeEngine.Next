# External Input Checks

## Definition
面对不可信 source asset、editor 输入、网络/文件数据等边界时进行的数据合法性检查。

## Invariants
- 尽量在 import/cook/load 边界完成。
- 已 cook 且由本引擎产生的可信内部结构不在每个 hot access 重复验证。
- 失败应在进入设备热路径前暴露。
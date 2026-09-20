# Development Checks

## Definition
只用于发现引擎内部不变量破坏或 API 使用错误的检查。

## Invariants
- shipping 构建中调用与参数求值均消失。
- 统一使用 Core 提供的极小开发检查设施。
- 不在每次资源访问、每 draw、每 cluster 永久验证内部协议。
- 对 profile 证明值得优化的循环，可在入口开发检查后使用 unchecked/ref 访问减少边界检查。
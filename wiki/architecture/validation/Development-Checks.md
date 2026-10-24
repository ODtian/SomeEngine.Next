# Development Checks

## Definition
只用于发现引擎内部不变量破坏或 API 使用错误的检查。

## Invariants
- shipping 构建中调用与参数求值均消失。
- 统一使用 Core 提供的极小开发检查设施。
- 公开入口为 `SomeEngine.Core.Diagnostics.DevelopmentCheck.Require(bool, string)`、`Fail(string)` 和 `Report(string)`。唯一实现位于基础程序集 `SomeEngine.Diagnostics`，Core 通过类型转发保留该入口；ECS 和上层调用相同的条件检查方法。
- `Require` 与 `Fail` 抛出使用错误；`Report` 将已经隔离的错误送给应用的 `Trace` listeners。三个入口均以 `Conditional("DEBUG")` 控制调用和参数求值。SomeUI 的构建错误经过隔离后用 `Report` 发出报告，再继续当前帧。
- 不在每次资源访问、每 draw、每 cluster 永久验证内部协议。
- 对 profile 证明值得优化的循环，可在入口开发检查后使用 unchecked/ref 访问减少边界检查。

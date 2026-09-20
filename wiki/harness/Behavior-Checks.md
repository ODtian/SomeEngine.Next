# Behavior Checks

Behavior checks验证 externally observable behavior、failure handling 与关键状态转换。它们不应锁死内部实现算法，除非算法本身就是架构契约。

对 rendering 等高性能路径，测试可验证 capacity/overflow、资源生命周期、同步结果和 ABI，但不要求 shipping 代码为了测试方便保留额外 tracking。

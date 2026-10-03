---
status: accepted
---

# 异步构建在视图重建阶段恢复

`Build` 返回自定义的 `BuildTask`，可以使用真正的 `async`/`await`。`BuildTask` 的方法构建器接管所有 `await` 的恢复位置：任何等待对象完成后只把（视图，状态机）放入队列，由视图重建系统在 UI 线程的重建阶段恢复，恢复前设置当前作用域。

因此作用域继续使用线程静态变量，不使用 `AsyncLocal`；恢复时 world 归 UI 阶段独占，ECS 访问安全；不依赖 `SynchronizationContext`。`Rerun` 模式的构建挂起期间依赖变化时取消并重新开始。同步完成零分配，首次挂起时状态机装箱一次并池化。

用户在 2026-10-03 确定。

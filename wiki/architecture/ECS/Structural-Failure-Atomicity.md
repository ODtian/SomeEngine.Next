# Structural Failure Atomicity

结构变化中所有可能失败的用户 hash/equality/copy、allocation 与检查必须发生在切换 World 根之前。失败时丢弃 candidate，当前 World 不变。

真正切换根的短路径不得调用用户代码或执行会分配的工作。切换完成后的 cleanup/observer failure 只报告错误，不回滚已经完整可见的新 World。

若检测到理论上不可达的根损坏，不继续暴露部分状态。
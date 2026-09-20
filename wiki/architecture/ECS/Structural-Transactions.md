# Structural Transactions

结构变化通过 detached candidate 解释并一次提交，不直接在已发布 World 上逐命令修改。

## Core sequence
producer streams 结束 → deterministic merge → 建立 candidate → replay once → 检查最终不变量 → 一次 root/epoch 切换 → 延后 cleanup。

## Relations
- [[Structural-Command-Ordering]]
- [[Structural-Copy-on-Write]]
- [[Structural-Failure-Atomicity]]
- [[Parent-Maintenance-Timing]]
- [[Job-Command-Production]]
- [[Bundle-Structural-Writes]]
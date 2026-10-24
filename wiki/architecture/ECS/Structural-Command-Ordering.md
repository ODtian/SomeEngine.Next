# Structural Command Ordering

并发 producer 的结构命令必须基于确定的业务路径与 producer-local sequence 形成 total order；不得使用 worker id、调度先后或完成时间作为顺序来源。

所有 command 在同一个 detached candidate 中严格按该顺序解释一次。Destroy、reparent、relation retarget、component add/remove 等因此使用普通 imperative semantics，而不是先计算最终状态再回放第二遍。

Deterministic mode 对没有可重现 producer key 的并发外部 producer 直接拒绝；普通模式可只保证 memory safety 而不承诺 byte-identical order。
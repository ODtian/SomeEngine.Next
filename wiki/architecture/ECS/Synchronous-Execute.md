# 同步 Execute  [[architecture/ECS-Architecture|ECS Architecture]]

公共同步范围批次与便利入口见 [Job 状态与完成语义：同步范围执行](Job-Completion.md#同步范围执行)。

`Execute` 不是“先 Complete conflicts，再裸调用 body”。它自己是 owner：

```text
pin -> semantic wait -> atomic admission -> hazard wait
    -> caller-thread body -> WorkReleased -> descendants join
```

admission 是线性化点：先存在的冲突 owner 是 predecessors，后提交的冲突 owner 会看到 Execute owner 并成为 successors，因此 wait 返回和 body 开始之间不存在 schedule-after-wait race。

等待使用 cooperative pumping；zero-worker runtime 也能前进。小 workload 使用相同 Execute 语义。managed/thread-affine workload 还必须声明目标 executor 和真实 alias resource；“当前 caller”本身不构成线程或对象安全证明。


# ScopeCompleted  Parent: [[Job-Completion]] 

`ScopeCompleted` 表示 `WorkReleased` 加所有 descendants 和 scope-owned resources 都已结束。public `JobHandle` 的显式 dependency 等待这个事件。

默认 `RequireSuccess` dependency 传播 fault/cancel；显式选择的 `AfterCompletion` dependency 只建立 scope 顺序，用于 retry、cleanup 和 checkpoint serialization。两者参加相同 cycle detection。单纯 resource predecessor 的 fault 不自动取消 successor。值写 job 不是事务，body fault 后可能留下部分值写入，因此需要有效结果的消费者必须使用默认 dependency 依赖该 handle。


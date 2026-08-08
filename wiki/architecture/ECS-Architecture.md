# ECS Architecture

全 C# ECS 游戏引擎架构决策。

- ECS 框架自研（[[Engine-Goals]]）：`SomeEngine.ECS` 与 `SomeEngine.Job` 作为内部项目
- QVVS 坐标系统
- 所有系统建立在 ECS 基础上

Job、Relation、Hierarchy、结构事务与安全借用的统一终态见 [[ECS-Job-Relation-Hierarchy]]。

结构候选通过单一 `WorldStructureRoot + epoch` 发布；entity record page、chunk backing、
sparse/index/shared/buffer/table side ownership以及分页面 journal 使用按写分离。未绑定 Job
coordinator 的同步 root-control mutation 与结构候选共享 mutation gate，避免 query registry、
clock或journal control-plane 更新被候选提交覆盖。

存档采用两个强类型契约：`RawCheckpoint` 只服务同一构建/ABI的最快恢复，`DurableSave`
只接受显式稳定schema和canonical codec。完整决策见
`docs/adr/0005-ecs-save-contracts.md`。

参见 [[Engine-Goals]]。

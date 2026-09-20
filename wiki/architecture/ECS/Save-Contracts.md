# Save Contracts

ECS 存档保留两个不同用途的强类型契约：同一构建/ABI 的最快恢复，与面向长期保存的显式 schema/codec。两者不共享一个模糊的“自动兼容”格式。

具体序列化细节与版本规则由 `docs/adr/0005-ecs-save-contracts.md` 记录；本页只固定两种用途必须分离。

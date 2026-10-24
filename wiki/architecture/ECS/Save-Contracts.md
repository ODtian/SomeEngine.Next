# Save Contracts

ECS 存档保留两个不同用途的强类型契约：同一构建/ABI 的最快恢复，与面向长期保存的显式 schema/codec。两者不共享一个模糊的“自动兼容”格式。

具体序列化细节与版本规则由 `docs/adr/0005-ecs-save-contracts.md` 记录；本页只固定两种用途必须分离。

公共读档失败分类与 codec 注册数据见 [BinarySerialization 的 Read failures](../../../docs/BinarySerialization.md#read-failures)。

含界面数据的 World 使用 Views 的生成注册与读回后重建入口，见
[[architecture/someui/Assemblies|视图序列化]]。

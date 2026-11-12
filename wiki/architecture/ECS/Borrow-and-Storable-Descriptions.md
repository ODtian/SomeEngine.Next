# 可存储描述与临时数据范围

查询和系统描述保存未来执行所需的类型、筛选、版本与顺序。数据引用由实际执行范围建立，调用方在范围内完成读取或修改。

- [可存储描述](Storable-Descriptions.md)：QueryDescription、Query 与 SystemState。
- [活跃范围中的访问](Active-Owner-Borrow-Creation.md)：QueryScope、QueryChunk、类型 lookup、buffer 与层级读取。
- [同步范围执行](Synchronous-Execute.md)：Job 资源声明、依赖与具体范围工作。

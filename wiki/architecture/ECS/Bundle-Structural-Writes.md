# Bundle Structural Writes

Bundle spawn/add/replace/batch 与普通结构命令使用同一 candidate transaction。

批处理逐行 materialize；future rows 在 callback 执行前不可被 query/hook/index 看见。World-touching write 只能通过声明过的 bundle access 或记录到后续结构命令，不能通过捕获 World 绕过访问边界。

关系创建将受保护端点、Applied 状态和载荷交给一次 Bundle spawn，直接在三列 archetype 分配实体。
callback 取得该实体后准备关系索引，再按端点、Applied、载荷顺序写入；Bundle 写入结束后发布邻接标记。
类型化关系入口通过内部 `WriteRelationshipSource<T>` 写入端点；公共 Bundle 写入继续执行组件保护规则。
载荷沿共同 `WriteAdded` 执行索引和 Hook，失败时由原结构事务及关系恢复链收尾。

`CommandBuffer.Clear` 保留记录容量供下一次记录复用；`Dispose` 及 Job producer 完成发布后的释放清空记录数组容量。
已发布的 deferred handle 继续保存实体身份，其所引用的已释放命令对象只保留解析所需的状态。

关系 Create 的参数、载荷和 deferred cell 引用存入按载荷类型划分的数组，FIFO 命令头保存类型和数组下标。
回放从该行读取端点，沿共同 `RelationGraph.Create` 建立关系，并将实体身份写回 cell。
Clear 复用参数数组并清空记录引用；Dispose 释放数组。deferred cell 独立保存发布结果及失败失效状态。

命令回放在共同 topology-write admission 和结构事务中调用现有实体、组件及关系数据持有者。
关系内部的存活、cleanup 与 marker 存在检查读取同一批次已经借用的数据。
公开 World 入口继续建立访问范围；EntityDestroying 等实际生命周期通知沿原入口交付。
回放在实际结构命令执行前推进 topology revision，使批次内此前捕获的派生范围沿原规则失效；整批提交继续推进 structure epoch。
producer callback 在自己的命令 segment 上持有一次记录 gate，内部记录复用该 gate；原 producer 身份及记录生命周期检查随各次入口继续执行。

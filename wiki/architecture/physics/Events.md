# 物理事件

`PhysicsEventBuffer` 汇集当前步骤各阶段的结果，内部的 `EventLog<PhysicsEvent>` 持有已完成步骤的事件。接触、触发、断裂、休眠和唤醒共用这一个日志。消费者持有通用 `EventReader<PhysicsEvent>`。

## 产生与完整步交付

接触观察读取执行器提供的 `PhysicsContacts`。一个完成步的触碰形状对集合与前次完成结果比较，产生 `Begin`、`Stay`、`End`。形状对的多个触碰点合并为一个事件，单个形状对的 `Occurrences` 为 1，聚合记录保存形状对事实数，触碰点数由接触视图读取。力由这些点累加，代表点取最小间距。结束事件保留最近的几何观测并把力置零。

只有作用端中至少一个主体带 `ReportContacts`，而且日志已有读取者时，接触观察才产生记录。触发来自公共点列的 `Trigger` 标记。`Subject` 用于断裂、休眠和唤醒的实际实体；接触以双方几何实体、形状编号和特征定位。

`BreakConstraints` 读取当前约束的实际主体力和力矩，记录断裂负载并通过普通命令回放删除约束。活动阶段按岛传播唤醒，积累静止时间，并记录休眠和唤醒的实际发生时刻。

`EmitPhysicsEvents` 在最后一个子步把暂存值追加到日志，并把发布接到生产者完成链。每个事件保存 `StepNumber`、`StepStartTime`、`StepEndTime`，以及实际 `Substep` 和 `Time`。读取者即使跨多个步骤保留记录，也能恢复各记录的原步骤边界。

## 读取

调用 `Watch()` 登记匿名读取者，或用 `Watch(Guid)` 登记具名读取者。登记后的事件进入该读取者的区间。

消费者的普通 Job 在物理步骤依赖完成后调用 `Capture()`，用该区间的 `Ready` 和 `Read` 安排读取。`Open(in range)` 提供 `First`、`Second` 两段连续数据及 `Through`。消费完成后释放读取范围并调用 `Advance(Through)`。读取进度由通用日志保存。

设备生产者按 `HasReaders` 决定 CPU 回读；回读和日志发布沿实际设备完成链。公共列读取及接触数值状态继续由执行器的结果接口交付。

日志保留、资源范围和具名读取进度的通用规则见 [ECS changes](../ECS/Rebuild/Changes.md) 与 [Readiness](../ECS/Readiness-Admission.md)。

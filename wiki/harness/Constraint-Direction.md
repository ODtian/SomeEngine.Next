# Constraint Direction

**约束不反向流动。**

Harness 是契约，代码服从 harness。如果 harness 与代码实现冲突：
- ✘ 不改 harness 放松约束
- ✔ 终止当前开发 loop，重新 grill 澄清需求
- ✔ 重新生成 harness

约束只能通过 grill 产出，不能为迁就代码而手改。

参见 [[Harness-Definition]]。

# Run classification uses accepted terms

## What to review

检查 batch instructions、review artifacts、status 与最终报告的范围分类，确认只使用本轮已完成、本轮未完成、不属于本轮三种 accepted terms，并且分类与 capability ledger、harness 结果和用户保留的并行工作一致。

## Pass conditions

- 已由产品代码、测试、review result 和 single harness 闭合的内容标为本轮已完成。
- 未通过 hard gate 的 run 不宣称完成。
- ECS、Job、RenderWorld、Cluster renderer 接入、旧 SomeEngine 仓库明确标为不属于本轮且未被提交。

## Fail conditions

- 使用含糊阶段词代替 accepted classification，或把 warning/未执行检查写成已完成。
- 将用户无关脏改动纳入 run 0004。

## NEEDS_GRILL

只有新的证据证明既有 accepted classification 无法表达真实范围并会改变交付边界时，才使用 `NEEDS_GRILL:`。

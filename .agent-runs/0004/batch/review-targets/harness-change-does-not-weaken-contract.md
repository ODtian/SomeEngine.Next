# Harness change does not weaken contract

## What to review

检查 run 0004 对 capability continuity、质量基线、依赖可复现性与 review authoring 的 harness 修改，确认修改只提高证据精度或修复错误输入，不降低 hard gate、required lane、mandatory level、测试发现数或失败策略。

## Pass conditions

- 质量基线只接受 `c0ac382e` 中可机械定位的既有诊断，并按 assembly、path、line、symbol、metric 精确匹配。
- capability/API inventory 数量与等级没有为了通过而减少。
- clean-checkout、真实 D3D12 display、required product tests 与 review results 仍是 hard gate。
- 新增或变差的质量诊断继续失败。

## Fail conditions

- 使用通配符、目录级豁免、`NoWarn`、跳过、减少测试发现数或降低 capability target 使结果变绿。
- 把缺失依赖、缺失 native execution 或失败的 review target 改成 warning。

## NEEDS_GRILL

只有现有 accepted contract 与可验证的原始证据直接冲突、且修正会改变本 run 的能力范围时，才使用 `NEEDS_GRILL:`。

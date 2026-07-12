# Clean checkout, benchmark, and scope are real

## What to review

审查依赖闭包、harness接线、benchmark/soak artifact和git diff scope，防止本机缓存或无关脏改动掩盖交付问题。

## Pass conditions

- AssetCook/solution不依赖 untracked `dxc.exe`；所有 runtime/native inputs tracked或由明确 package/catalog提供。
- 四个 Graphics/RG产品项目与四个相关测试项目在 declared boundary和single product-test runner中，required tests非零发现。
- benchmark/soak输出版本化 JSON，包含 CPU、OS、adapter、driver、build/commit、scenario、iteration和结果 metadata；10k-frame lightweight soak可执行。
- migration diff仅含本 run Graphics/RG/Assets shader contract/harness/docs/wiki，不包含 ECS、Job、RenderWorld或 `F:\SomeEngine`。

## Fail conditions

- 依赖 `Library/`、bin/obj、本机 PATH或被 ignore 的 binary才能构建/测试。
- benchmark只写文档/伪造阈值/无机器 metadata，或性能测试替代功能正确性。
- 通过整体 stage混入并行用户工作。

## NEEDS_GRILL

只有当 clean-checkout必须引入新的外部二进制授权/供应链决策，或 mandatory test必须修改并行用户scope时使用 `NEEDS_GRILL:`。

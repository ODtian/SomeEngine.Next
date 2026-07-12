# Migration has no temporary exceptions

## What to review

检查迁回的 RHI/RG 产品实现、测试、项目文件、capability ledger 和质量配置，确认没有为本轮加入临时 allowlist、空实现、兼容分支、silent fallback、测试 skip 或以后再补的路径。

## Pass conditions

- mandatory API 在 public contract、Null、D3D12、RG 与 executable test 中形成闭环。
- bindless 保持可选，FL12_0、SM6.2 与 traditional binding 仍是准入基线。
- advanced features 按真实层级记录，未实现项保留明确 API gap。
- checkpoint quality baseline 每项都能回溯到原提交的具体符号和指标。

## Fail conditions

- 发现本轮新增 `NoWarn`、skip、catch-and-ignore、空 backend method、删除 API 或不受验证的过渡配置。
- 用 bindless、SM6.6、Tier 3 或特定设备白名单替代已接受基线。

## NEEDS_GRILL

只有关闭临时路径会迫使 accepted scope 或设备基线发生实质变化时，才使用 `NEEDS_GRILL:`。

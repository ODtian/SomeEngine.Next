# Shader parameter generation uses asset truth

## What to review

审查 `PassParameters` / `ShaderParameters` source-generated binding链，确认参数形状与 shader identity解耦，现有 ShaderAsset/MaterialPass reflection是唯一 shader truth。

## Pass conditions

- pairing在 graph compile前完成并进入 canonical/cache invalidation；Execute热路径无 reflection或字段字符串查找。
- generated glue发出 exact access、view、constant packing、descriptor writes；逻辑 resource ID + 完整 view desc 是不可拆的单个值。
- descriptor kind/count/access/shape与 cooked reflection不一致时 fail-close。
- 不出现 shader path/entry attribute、generated shader marker或旁路 asset database/importer/runtime material链。

## Fail conditions

- parameter type绑定具体 shader/path/entry，或 runtime通过字段名猜 binding。
- generated code只做便利包装，authoritative access/binding仍靠手工双录。
- reflection缺失时运行时 fallback、默认 read access或静默忽略字段。

## NEEDS_GRILL

若现有 asset reflection无法表达 accepted parameter contract且需要改变 shader/material truth source时使用 `NEEDS_GRILL:`。

# Validation

## Definition
检查必须按成本与错误来源分类；内部程序错误不应永久进入 shipping 热路径。

## Categories
- [[Development-Checks]]
- [[External-Input-Checks]]
- [[Memory-Safety-Guards]]
- [[Execution-Failures]]

## Invariants
每个新增检查必须能归入其中一类，不允许子系统自行建立另一套永久 validation 规则。
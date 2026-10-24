# Missing Resource

## Definition
缺失资源使用统一引擎路径，不在各 pipeline 中散布 null/placeholder 判断。

## Invariants
- 只处理不存在、损坏或无法建立最低有效表示的资源。
- 正常 Texture streaming 依赖 Bootstrap，不把 missing 当作普通首用策略。
- 具体 feature 可以引用统一资源，但不拥有自己的全局 missing 体系。
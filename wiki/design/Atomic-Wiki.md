# Atomic Wiki

Wiki 按可独立变化的概念拆页，不按一次讨论、一份 prompt、一个源码文件或一个 assembly 拆页。

## Rules
- 一条设计事实只有一个 owning page；其它页面只链接。
- 如果一个算法改变时另一个事实不必重新定义，它们应拆成不同页面。
- 领域入口页只做 MOC/navigation，不重新解释所有子页面。
- Definition / Invariants 是长期设计；具体算法、benchmark 选择和迁移步骤单独记录并允许迭代。
- 历史记录和性能证据不反向定义当前 architecture。
- Current source 只能说明现状；当源码与架构 Wiki 冲突时，除非先修改架构页，否则视为实现尚未迁移。
- 大型旧页拆分后保留到 `history/`，不继续作为当前权威。

## Page shape
一个页面只在需要时使用这些部分：Definition、Invariants、Relations、Non-goals、Current design、Open questions、Evidence。没有内容的部分不为了格式完整而添加。

## MOC
MOC 用来提供领域路径和阅读入口；它不拥有被链接页面中的规则。

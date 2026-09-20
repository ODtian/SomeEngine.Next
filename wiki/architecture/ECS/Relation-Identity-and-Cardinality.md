# Relation Identity and Cardinality

每条 relation 是独立 edge entity，并以 typed edge identity 执行单-edge 操作。Relation type 静态声明 cardinality：Parallel、UniquePair、UniqueSource、UniqueTarget 或 OneToOne。

Policy 属于 relation type registration，不逐 edge 存储。违反 cardinality 的 create/retarget 使整个结构变化失败，不自动 merge、upsert 或覆盖旧 edge。